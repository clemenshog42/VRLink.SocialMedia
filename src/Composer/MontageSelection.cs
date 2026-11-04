using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRLink.Shared.Contracts;

namespace VRLink.Composer;

/// <summary>One rendered highlight clip (&lt;id&gt;.mp4 + &lt;id&gt;.json in the clip contract) as the montage sees it.</summary>
public sealed class MontageClip
{
    [JsonPropertyName("highlightId")] public string HighlightId { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("matchId")] public string? MatchId { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("durationSec")] public double DurationSec { get; set; }
    [JsonPropertyName("heroIsBot")] public bool? HeroIsBot { get; set; }
    [JsonPropertyName("heroLabel")] public string? HeroLabel { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("hashtags")] public List<string>? Hashtags { get; set; }
    [JsonPropertyName("venue")] public string? Venue { get; set; }
    [JsonPropertyName("createdUtc")] public DateTime? CreatedUtc { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("video")] public string? Video { get; set; }
    [JsonPropertyName("players")] public List<ClipPlayer>? Players { get; set; }
    /// <summary>Optional timing hint: second of the decisive moment inside the clip.</summary>
    [JsonPropertyName("peakSec")] public double? PeakSec { get; set; }
    /// <summary>Optional timing hint: second where the VRLink end card starts.</summary>
    [JsonPropertyName("endCardSec")] public double? EndCardSec { get; set; }

    [JsonIgnore] public string VideoPath { get; set; } = "";
    [JsonIgnore] public string JsonPath { get; set; } = "";
    /// <summary>Date of the clip (createdUtc, else the folder / file date).</summary>
    [JsonIgnore] public DateOnly Date { get; set; }

    [JsonIgnore] public string HeroKey => (MatchId ?? "") + "|" + (HeroLabel ?? "");
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Humanize(Type) : Title!;

    public static string Humanize(string type)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < type.Length; i++)
        {
            if (i > 0 && char.IsUpper(type[i]) && !char.IsUpper(type[i - 1])) sb.Append(' ');
            sb.Append(type[i]);
        }
        return sb.Length == 0 ? "HIGHLIGHT" : sb.ToString().ToUpperInvariant();
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed record MontageSelection
{
    /// <summary>How many plays (clamped to 3..7 by <see cref="MontageSelector"/>).</summary>
    public int Top { get; init; } = 5;
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    /// <summary>Test material is bot matches; production leaves this false.</summary>
    public bool AllowBots { get; init; }
    /// <summary>Production path: only these (customer-approved) clip ids may be used. null = no restriction.</summary>
    public IReadOnlySet<string>? Ids { get; init; }
    public int MaxPerType { get; init; } = 2;
}

public static class MontageSelector
{
    public const int MinTop = 3, MaxTop = 7;

    /// <summary>Finds every clip (json + mp4) below <paramref name="dir"/>; one clip per highlightId (the copy that has a video wins).</summary>
    public static List<MontageClip> Load(string dir)
    {
        var byId = new Dictionary<string, MontageClip>(StringComparer.OrdinalIgnoreCase);
        foreach (var jf in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            MontageClip? c;
            try { c = JsonSerializer.Deserialize<MontageClip>(File.ReadAllText(jf), MontageClip.Json); }
            catch (JsonException) { continue; }
            if (c == null || string.IsNullOrWhiteSpace(c.HighlightId)) continue;
            var folder = Path.GetDirectoryName(jf)!;
            var mp4 = Path.Combine(folder, string.IsNullOrWhiteSpace(c.Video) ? c.HighlightId + ".mp4" : c.Video);
            if (!File.Exists(mp4)) continue; // 2026-10-19 style: json without a video
            c.VideoPath = mp4; c.JsonPath = jf;
            c.Date = c.CreatedUtc is { } cu ? DateOnly.FromDateTime(cu.ToUniversalTime())
                   : DateOnly.TryParse(Path.GetFileName(folder), CultureInfo.InvariantCulture, out var d) ? d
                   : DateOnly.FromDateTime(File.GetLastWriteTimeUtc(jf));
            byId[c.HighlightId] = c;
        }
        return byId.Values.OrderBy(c => c.HighlightId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Ids file: one id per line, blank lines and '#' comments ignored (a trailing ".mp4"/".json" is tolerated).</summary>
    public static HashSet<string> ReadIds(string file)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || line.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) line = Path.GetFileNameWithoutExtension(line);
            set.Add(line);
        }
        return set;
    }

    /// <summary>
    /// The plays of the montage, best first (index 0 = #1): filter (date range, ids, bots, no earlier montages), score descending,
    /// at most <see cref="MontageSelection.MaxPerType"/> per type, different heroes preferred (second pass relaxes only the hero rule).
    /// </summary>
    public static List<MontageClip> Select(IEnumerable<MontageClip> all, MontageSelection s)
    {
        int top = Math.Clamp(s.Top, MinTop, MaxTop);
        var pool = all
            .Where(c => !string.Equals(c.Format, "montage", StringComparison.OrdinalIgnoreCase) && c.Type != "WeeklyTop")
            .Where(c => s.From is not { } f || c.Date >= f)
            .Where(c => s.To is not { } t || c.Date <= t)
            .Where(c => s.Ids == null || s.Ids.Contains(c.HighlightId))
            .Where(c => s.AllowBots || c.HeroIsBot != true)
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.CreatedUtc ?? DateTime.MinValue).ThenBy(c => c.HighlightId, StringComparer.Ordinal)
            .ToList();

        var picked = new List<MontageClip>();
        var perType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool TypeOk(MontageClip c) => perType.GetValueOrDefault(c.Type) < s.MaxPerType;
        void Take(MontageClip c) { picked.Add(c); perType[c.Type] = perType.GetValueOrDefault(c.Type) + 1; }

        foreach (var c in pool)
        {
            if (picked.Count >= top) break;
            if (TypeOk(c) && picked.All(p => p.HeroKey != c.HeroKey)) Take(c);
        }
        foreach (var c in pool)
        {
            if (picked.Count >= top) break;
            if (!picked.Contains(c) && TypeOk(c)) Take(c);
        }
        return picked.OrderByDescending(c => c.Score).ThenBy(c => c.HighlightId, StringComparer.Ordinal).ToList();
    }
}
