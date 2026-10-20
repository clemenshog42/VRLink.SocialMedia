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

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };
}
