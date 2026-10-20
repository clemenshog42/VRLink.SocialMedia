using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRLink.Composer;

/// <summary>One line of Builds/Highlights/highlights_&lt;date&gt;.jsonl (written by the Unity HighlightDetector). Times are server seconds.</summary>
public sealed record HighlightRecord
{
    [JsonPropertyName("v")] public int V { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("matchId")] public string MatchId { get; init; } = "";
    [JsonPropertyName("round")] public int Round { get; init; }
    /// <summary>Ace, Kills4, Kills3, OneTapStreak, Clutch, LastSecondDefuse, GrenadeMultiKill, NinjaDefuse.</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = new();
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("heroClientId")] public ulong HeroClientId { get; init; }
    [JsonPropertyName("heroTeam")] public string HeroTeam { get; init; } = "";
    [JsonPropertyName("heroLabel")] public string HeroLabel { get; init; } = "";
    [JsonPropertyName("heroIsBot")] public bool HeroIsBot { get; init; }
    [JsonPropertyName("kills")] public int Kills { get; init; }
    [JsonPropertyName("clutchVs")] public int ClutchVs { get; init; }
    [JsonPropertyName("defuseSecondsLeft")] public double DefuseSecondsLeft { get; init; } = -1;
    [JsonPropertyName("roundStart")] public double RoundStart { get; init; }
    [JsonPropertyName("clipStart")] public double ClipStart { get; init; }
    [JsonPropertyName("clipEnd")] public double ClipEnd { get; init; }
    [JsonPropertyName("moments")] public List<HighlightMoment> Moments { get; init; } = new();
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; init; }

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Types that also get the "split reality" format (real top, in-game POV bottom).</summary>
    public bool WantsSplit => Type is "Ace" or "Kills4" or "Kills3" or "Clutch";

    public string DemoFileName => (string.IsNullOrEmpty(MatchId) ? "nomatch" : MatchId) + "_r" + Round.ToString("00") + ".vrdemo";
}

public sealed record HighlightMoment
{
    [JsonPropertyName("t")] public double T { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "Kill";
    [JsonPropertyName("victim")] public ulong Victim { get; init; }
    [JsonPropertyName("victimLabel")] public string? VictimLabel { get; init; }
    [JsonPropertyName("weapon")] public string? Weapon { get; init; }
    [JsonPropertyName("hs")] public bool Headshot { get; init; }
    [JsonPropertyName("oneTap")] public bool OneTap { get; init; }
    [JsonPropertyName("dist")] public double Dist { get; init; }
}

/// <summary>Builds/Highlights/server_state.json, written by the Unity server.</summary>
public sealed record ServerState
{
    [JsonPropertyName("utcNow")] public DateTime UtcNow { get; init; }
    /// <summary>Boot, Lobby, Tutorial, Match, PostMatch, ... (the game's session phase).</summary>
    [JsonPropertyName("phase")] public string Phase { get; init; } = "";
    [JsonPropertyName("connectedClients")] public int ConnectedClients { get; init; }
    [JsonPropertyName("matchRunning")] public bool MatchRunning { get; init; }
}

/// <summary>Heavy work (Unity layer render, ffmpeg compose) only when it cannot disturb a match.</summary>
public static class ServerGate
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    /// <returns>(allowed, reason)</returns>
    public static (bool Allowed, string Reason) Evaluate(ServerState? s, DateTime nowUtc)
    {
        if (s == null) return (false, "server_state.json missing → state unknown, waiting");
        var age = nowUtc - s.UtcNow.ToUniversalTime();
        if (age > MaxAge) return (false, $"server_state.json is {age.TotalSeconds:0} s old → state unknown, waiting");
        if (s.MatchRunning) return (false, "match running");
        return s.Phase switch
        {
            "PostMatch" or "Lobby" => (true, s.Phase),
            "Boot" when s.ConnectedClients == 0 => (true, "Boot with 0 clients"),
            _ => (false, $"phase {s.Phase} with {s.ConnectedClients} client(s)"),
        };
    }

    public static ServerState? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ServerState>(fs, HighlightRecord.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}

/// <summary>layers.json written next to the layer files by the Unity -renderlayers mode (or the fake generator).</summary>
public sealed record LayerManifest
{
    [JsonPropertyName("v")] public int V { get; init; } = 1;
    [JsonPropertyName("highlightId")] public string HighlightId { get; init; } = "";
    [JsonPropertyName("utcStart")] public DateTime UtcStart { get; init; }
    [JsonPropertyName("fps")] public double Fps { get; init; } = 30;
    /// <summary>Overlay size (camera resolution × scale).</summary>
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("frames")] public int Frames { get; init; }
    [JsonPropertyName("hero")] public LayerHero Hero { get; init; } = new();
    /// <summary>Per overlay frame: hero head in OVERLAY pixels.</summary>
    [JsonPropertyName("heroTrack")] public List<TrackPoint> HeroTrack { get; init; } = new();
    [JsonPropertyName("moments")] public List<LayerMoment> Moments { get; init; } = new();
    [JsonPropertyName("players")] public List<LayerPlayer> Players { get; init; } = new();

    public double DurationSec => Frames / Fps;
}

public sealed record LayerHero
{
    [JsonPropertyName("clientId")] public ulong ClientId { get; init; }
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("team")] public string Team { get; init; } = "";
}

public sealed record TrackPoint
{
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("depth")] public double Depth { get; init; }
    [JsonPropertyName("visible")] public bool Visible { get; init; }
}

public sealed record LayerMoment
{
    [JsonPropertyName("utc")] public DateTime Utc { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "Kill";
    [JsonPropertyName("victimLabel")] public string? VictimLabel { get; init; }
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("headshot")] public bool Headshot { get; init; }
    [JsonPropertyName("oneTap")] public bool OneTap { get; init; }
}

public sealed record LayerPlayer
{
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("team")] public string Team { get; init; } = "";
    [JsonPropertyName("isBot")] public bool IsBot { get; init; }
}
