using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRLink.Shared.Contracts;

/// <summary>&lt;highlightId&gt;.json as written by the Unity ClipRenderer (contract: README.md "Clip contract"). Shared by Publisher and Composer.</summary>
public class ClipManifest
{
    [JsonPropertyName("highlightId")] public string HighlightId { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("matchId")] public string? MatchId { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("durationSec")] public double DurationSec { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("hashtags")] public List<string>? Hashtags { get; set; }
    [JsonPropertyName("venue")] public string? Venue { get; set; }
    [JsonPropertyName("createdUtc")] public DateTime? CreatedUtc { get; set; }
    /// <summary>Optional. True when the highlight's main player is a practice bot (skipped unless allowed in Settings).</summary>
    [JsonPropertyName("heroIsBot")] public bool? HeroIsBot { get; set; }
    /// <summary>Optional (V2). "irl" (arena camera + game overlay), "split" (real top / in-game POV bottom) or "game" (game-only replay).</summary>
    [JsonPropertyName("format")] public string? Format { get; set; }
    /// <summary>Optional (V2). What the clip is made of, e.g. ["arenaCam:cam1","overlay","pov","gameAudio","roomAudio"].</summary>
    [JsonPropertyName("sources")] public List<string>? Sources { get; set; }
    /// <summary>Optional (V2). True when the clip contains no music (TikTok: add a trending sound in the app).</summary>
    [JsonPropertyName("musicFree")] public bool? MusicFree { get; set; }
    /// <summary>Optional (V2). Anonymous players in the clip (for the share pack).</summary>
    [JsonPropertyName("players")] public List<ClipPlayer>? Players { get; set; }
    /// <summary>Optional. Video file name if it differs from &lt;highlightId&gt;.mp4.</summary>
    [JsonPropertyName("video")] public string? Video { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}

public sealed class ClipPlayer
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("team")] public string? Team { get; set; }
    [JsonPropertyName("isBot")] public bool IsBot { get; set; }
    [JsonPropertyName("hero")] public bool Hero { get; set; }
}
