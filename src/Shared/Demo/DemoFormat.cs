using System.Numerics;

namespace VRLink.Shared.Demo;

/// <summary>
/// C# port of the Unity <c>Tactical.Highlights.DemoFormat</c> (read side only).
/// Source of truth: C:\vr\VRCsGo\Assets\_Tactical\Scripts\Highlights\DemoFormat.cs + DemoReader.cs (UVCS).
/// A golden-file test (tests/data/*.vrdemo) keeps both readers in sync.
///
/// .vrdemo = GZip( header | players | strings | body ), little-endian, strings = .NET BinaryWriter strings (7-bit length prefix + UTF-8).
/// Header: magic "VRDM", ushort version, string matchId, int round, [v2+: string mapScene], double startTime, double endTime,
///         float sampleRate, long createdUtcTicks, [v3+: see <see cref="DemoV3Header"/>].
/// Players: ushort n × {ulong clientId, byte team, bool bot, bool female, float eyeHeight, string anonLabel}.
/// Strings: ushort n × string (weapon ids; 0xFFFF = none).
/// Body: int frames, int events, int bodyBytes, then tagged records (time-ordered), then tag End (0).
/// Times are server Time.timeAsDouble seconds.
///
/// Coordinates: Unity arena space, left-handed, y up (x right, z forward), metres. Quaternions are Unity (x,y,z,w).
/// </summary>
public static class DemoFormat
{
    /// <summary>Newest version this reader understands. v3 is a provisional stub until the Unity layout is final.</summary>
    public const ushort MaxReadVersion = 3;
    public const ushort MinReadVersion = 1;
    public static ReadOnlySpan<byte> Magic => "VRDM"u8;
    public const ushort NoString = 0xFFFF;
    public const string Extension = ".vrdemo";
    public const byte FlagAlive = 1, FlagWeapon = 2, FlagWeaponLeft = 4;

    public static Quaternion ReadQuat(BinaryReader r)
    {
        var q = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
        float m = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return m < 1e-6f ? Quaternion.Identity : new Quaternion(q.X / m, q.Y / m, q.Z / m, q.W / m);
    }

    public static Vector3 ReadVec(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}

/// <summary>Mirrors Tactical.Core.Team.</summary>
public enum Team : byte { None = 0, TacticalUnit = 1, Syndicate = 2 }

/// <summary>Mirrors Tactical.Core.ImpactKind.</summary>
public enum ImpactKind : byte { World = 0, Player = 1, Target = 2 }

/// <summary>Mirrors Tactical.Core.RoundPhase (values used in demos; unknown values are kept as numbers).</summary>
public enum RoundPhase : byte { None = 0, BuyPhase = 1, Action = 2, BombPlanted = 3, RoundEnd = 4 }

/// <summary>Mirrors Tactical.Core.RoundEndCause.</summary>
public enum RoundEndCause : byte { Elimination, BombExploded, BombDefused, TimeOut, Aborted }

public enum DemoRecord : byte
{
    End = 0, Frame = 1, Shot = 2, Impact = 3, Kill = 4, Grenade = 5, BombPlanted = 6, BombDefused = 7,
    BombExploded = 8, Phase = 9, RoundEnd = 10, FreeObjects = 11,
}

public sealed record DemoPlayerInfo(ulong ClientId, Team Team, bool IsBot, bool Female, float EyeHeight, string Label);

public struct DemoPlayerSample
{
    public int Slot;
    public bool Alive;
    public Team Team;
    public Vector3 HeadPos; public Quaternion HeadRot;
    public Vector3 LeftPos; public Quaternion LeftRot;
    public Vector3 RightPos; public Quaternion RightRot;
    public bool HasWeapon;
    public bool WeaponInLeftHand;
    public string? WeaponId;
    public Vector3 WeaponPos; public Quaternion WeaponRot;
}

public sealed class DemoFrame
{
    public double Time;
    public DemoPlayerSample[] Players = [];
}

/// <summary>Non-frame record; field use per kind as in the Unity DemoEvent.</summary>
public struct DemoEvent
{
    public DemoRecord Kind;
    public double Time;
    public ulong A, B;
    public Vector3 P1, P2;
    public float Value;
    public int IntA, IntB;
    public string? Text;
    public bool Flag;
    public byte Code, Code2;
}

public struct DemoFreeObject
{
    public uint ObjectId;
    public byte Kind;
    public string? WeaponId;
    public Vector3 Pos;
    public Quaternion Rot;
}

public sealed class DemoFreeFrame
{
    public double Time;
    public DemoFreeObject[] Objects = [];
}

/// <summary>
/// v3 header extension (PROVISIONAL until the Unity Highlights dev publishes the exact layout; only <see cref="DemoReader.ReadV3Header"/> must change):
/// long utcAnchorTicks, double utcAnchorServerTime, string venueId, ushort n × {float x, float z} arena boundary polygon (floor plane, arena space).
/// </summary>
public sealed class DemoV3Header
{
    /// <summary>UTC DateTime ticks of the anchor instant.</summary>
    public long UtcAnchorTicks;
    /// <summary>Server time (seconds, same clock as frames) at the same instant.</summary>
    public double UtcAnchorServerTime;
    public string VenueId = "";
    public Vector2[] Boundary = [];
}

public sealed class DemoData
{
    public ushort Version;
    public string MatchId = "";
    public int Round;
    public string MapScene = "";
    public double StartTime, EndTime;
    public float SampleRate;
    public DateTime CreatedUtc;
    /// <summary>Counts as written in the header (the golden test compares them with what was parsed).</summary>
    public int HeaderFrames, HeaderEvents, HeaderBodyBytes;
    public DemoV3Header? V3;
    public readonly List<DemoPlayerInfo> Players = new();
    public readonly List<string> Strings = new();
    public readonly List<DemoFrame> Frames = new();
    public readonly List<DemoEvent> Events = new();
    public readonly List<DemoFreeFrame> FreeFrames = new();

    public int SlotOf(ulong clientId) => Players.FindIndex(p => p.ClientId == clientId);

    /// <summary>Server time ↔ UTC mapping (exact with a v3 anchor, approximate otherwise).</summary>
    public DemoClock Clock => V3 != null
        ? new DemoClock(new DateTime(V3.UtcAnchorTicks, DateTimeKind.Utc), V3.UtcAnchorServerTime, Exact: true)
        // v1/v2: CreatedUtc is taken when recording begins (≈ StartTime). Good to roughly ±1 frame, not for clap sync.
        : new DemoClock(CreatedUtc, StartTime, Exact: false);
}

/// <summary>Maps demo server time (seconds) to UTC via one anchor (same instant on both clocks).</summary>
public readonly record struct DemoClock(DateTime AnchorUtc, double AnchorServerTime, bool Exact)
{
    public DateTime ToUtc(double serverTime) => AnchorUtc.AddTicks((long)Math.Round((serverTime - AnchorServerTime) * TimeSpan.TicksPerSecond));
    public double ToServerTime(DateTime utc) => AnchorServerTime + (utc - AnchorUtc).Ticks / (double)TimeSpan.TicksPerSecond;
}
