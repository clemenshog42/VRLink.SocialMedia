using System.Numerics;

namespace VRLink.Shared.Demo;

/// <summary>
/// C# port of the Unity <c>Tactical.Highlights.DemoFormat</c> (read side only).
/// Source of truth: C:\vr\VRCsGo\Assets\_Tactical\Scripts\Highlights\DemoFormat.cs + DemoReader.cs (UVCS).
/// A golden-file test (tests/data/*.vrdemo) keeps both readers in sync.
///
/// .vrdemo = GZip( header | players | strings | body ), little-endian, strings = .NET BinaryWriter strings (7-bit length prefix + UTF-8).
/// Header: magic "VRDM", ushort version, string matchId, int round, [v2+: string mapScene], [v3+: <see cref="DemoV3Header"/>],
///         double startTime, double endTime, float sampleRate, long createdUtcTicks.
/// Exact v3 byte layout: VRCsGo Assets/_Tactical/Scripts/Highlights/README.md ".vrdemo byte layout".
/// Players: ushort n × {ulong clientId, byte team, bool bot, bool female, float eyeHeight, string anonLabel}.
/// Strings: ushort n × string (weapon ids; 0xFFFF = none).
/// Body: int frames, int events, int bodyBytes, then tagged records (time-ordered), then tag End (0).
/// Times are server Time.timeAsDouble seconds.
///
/// Coordinates: Unity arena space, left-handed, y up (x right, z forward), metres. Quaternions are Unity (x,y,z,w).
/// </summary>
public static class DemoFormat
{
    /// <summary>Newest version this reader understands (v3 = UTC anchor + UtcSync records, venue outline, calibration kind; v4 = Doors records).</summary>
    public const ushort MaxReadVersion = 4;
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
public enum RoundPhase : byte { None = 0, BuyPhase = 1, Action = 2, BombPlanted = 3, RoundEnd = 4, Regroup = 5 }

/// <summary>Mirrors Tactical.Core.RoundEndCause.</summary>
public enum RoundEndCause : byte { Elimination, BombExploded, BombDefused, TimeOut, Aborted }

public enum DemoRecord : byte
{
    End = 0, Frame = 1, Shot = 2, Impact = 3, Kill = 4, Grenade = 5, BombPlanted = 6, BombDefused = 7,
    BombExploded = 8, Phase = 9, RoundEnd = 10, FreeObjects = 11,
    /// <summary>v3: t + int64 UTC ticks (fresh server-time/UTC pair every ~10 s and after hitches). Event field A = ticks.</summary>
    UtcSync = 12,
    /// <summary>v4: t, byte n, n × {Vector3 doorPos, byte leafCount, leafCount × float angle}: every door's leaf angles (deg, 0 = closed), written on change.</summary>
    Doors = 13,
}

/// <summary>One door of a Doors record (v4): root position (identity) and leaf angles about the hinge (deg, 0 = closed).</summary>
public struct DemoDoorState
{
    public Vector3 Pos;
    public float[] Angles;
}

public sealed class DemoDoorFrame
{
    public double Time;
    public DemoDoorState[] Doors = [];
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
/// v3 header block (directly after mapScene): byte kind (0 round, 1 calibration), string venueId, int64 utcAnchorTicks,
/// float64 utcAnchorServerTime, float32 boundaryBuffer, uint16 outlineCount, outlineCount × (float32 x, float32 z).
/// </summary>
public sealed class DemoV3Header
{
    /// <summary>0 = round demo, 1 = calibration recording.</summary>
    public byte Kind;
    public string VenueId = "";
    /// <summary>UTC DateTime ticks of the anchor instant.</summary>
    public long UtcAnchorTicks;
    /// <summary>Server time (seconds, same clock as frames) at the same instant.</summary>
    public double UtcAnchorServerTime;
    /// <summary>Venue playable inset (m).</summary>
    public float BoundaryBuffer;
    /// <summary>Venue floor outline in arena space: Vector2(x, z), metres.</summary>
    public Vector2[] Outline = [];
    public bool IsCalibration => Kind == 1;
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
    /// <summary>v4: door leaf angles whenever a leaf moved (empty in older demos).</summary>
    public readonly List<DemoDoorFrame> DoorFrames = new();

    public int SlotOf(ulong clientId) => Players.FindIndex(p => p.ClientId == clientId);

    /// <summary>True when UTC mapping is exact (v3 anchor + UtcSync records).</summary>
    public bool HasUtcAnchor => V3 != null && V3.UtcAnchorTicks > 0;

    /// <summary>(server time, UTC ticks) pairs in time order: header anchor first, then every UtcSync record.</summary>
    public List<(double ServerTime, long UtcTicks)> UtcSyncPoints()
    {
        var list = new List<(double, long)>();
        if (HasUtcAnchor) list.Add((V3!.UtcAnchorServerTime, V3.UtcAnchorTicks));
        else list.Add((StartTime, CreatedUtc.Ticks)); // v1/v2: CreatedUtc ≈ recording start; approximate only
        foreach (var e in Events) if (e.Kind == DemoRecord.UtcSync) list.Add((e.Time, (long)e.A));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>
    /// UTC(t) = utcTicks_k + (t − t_k)·1e7 with (t_k, utcTicks_k) the LAST sync point with t_k ≤ t (else the first one).
    /// Do not interpolate between syncs: server time lags the wall clock across hitches (Unity caps deltaTime).
    /// </summary>
    public DateTime UtcAt(double serverTime)
    {
        var pts = UtcSyncPoints();
        var k = pts[0];
        foreach (var p in pts) { if (p.ServerTime > serverTime) break; k = p; }
        return new DateTime(k.UtcTicks + (long)((serverTime - k.ServerTime) * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>Inverse of <see cref="UtcAt"/> (last sync point whose UTC ≤ utc).</summary>
    public double ServerTimeAt(DateTime utc)
    {
        var pts = UtcSyncPoints();
        var k = pts[0];
        foreach (var p in pts) { if (p.UtcTicks > utc.Ticks) break; k = p; }
        return k.ServerTime + (utc.Ticks - k.UtcTicks) / (double)TimeSpan.TicksPerSecond;
    }
}
