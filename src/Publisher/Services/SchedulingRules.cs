using System.Globalization;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

public record Slot(DateTime Utc, string Key);

/// <summary>Daily posting slots in local time (pure, unit tested).</summary>
public static class SlotPlanner
{
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { return TimeZoneInfo.Local; }
    }

    public static List<TimeOnly> ParseSlots(IEnumerable<string> slots) =>
        slots.Select(s => TimeOnly.TryParseExact(s.Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? (TimeOnly?)t : null)
             .Where(t => t.HasValue).Select(t => t!.Value).Distinct().OrderBy(t => t).ToList();

    private static Slot Make(DateOnly day, TimeOnly t, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(t, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1); // DST gap: shift forward
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return new Slot(utc, local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>The latest slot with slot &lt;= now &lt;= slot + grace, or null.</summary>
    public static Slot? DueSlot(DateTime nowUtc, IReadOnlyList<TimeOnly> slots, TimeZoneInfo tz, TimeSpan grace)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var today = DateOnly.FromDateTime(nowLocal);
        Slot? best = null;
        foreach (var day in new[] { today.AddDays(-1), today })
            foreach (var t in slots)
            {
                var s = Make(day, t, tz);
                if (s.Utc <= nowUtc && nowUtc - s.Utc <= grace && (best == null || s.Utc > best.Utc)) best = s;
            }
        return best;
    }

    public static List<Slot> Upcoming(DateTime nowUtc, IReadOnlyList<TimeOnly> slots, TimeZoneInfo tz, int count)
    {
        var result = new List<Slot>();
        if (slots.Count == 0) return result;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));
        for (int d = 0; result.Count < count && d < 60; d++)
            foreach (var t in slots)
            {
                var s = Make(today.AddDays(d), t, tz);
                if (s.Utc > nowUtc && result.Count < count) result.Add(s);
            }
        return result;
    }
}

public record QueueCandidate(int PostId, string Type, double Score, DateTime CreatedUtc);

/// <summary>Picks the next clip for a slot: best score first, never the same type as the previous post (pure).</summary>
public static class ClipPicker
{
    public static QueueCandidate? Pick(IEnumerable<QueueCandidate> candidates, string? lastPostedType, bool strictAlternation)
    {
        var ordered = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.CreatedUtc).ThenBy(c => c.PostId).ToList();
        if (ordered.Count == 0) return null;
        if (lastPostedType == null) return ordered[0];
        var other = ordered.FirstOrDefault(c => !string.Equals(c.Type, lastPostedType, StringComparison.OrdinalIgnoreCase));
        if (other != null) return other;
        return strictAlternation ? null : ordered[0];
    }
}

/// <summary>Caption templates per highlight type, rotated so consecutive posts are not identical.</summary>
public static class CaptionComposer
{
    public const int TikTokMaxChars = 2200;   // title limit (UTF-16 runes) per TikTok docs
    public const int InstagramMaxChars = 2200; // VERIFY: Instagram caption limit 2200 chars / 30 hashtags
    public const int YouTubeMaxChars = 4000;   // YouTube description limit is 5000 BYTES (UTF-8); YouTubePublisher cuts exactly, this keeps room for the booking link

    public static int MaxCharsFor(Platform p) => p switch { Platform.TikTok => TikTokMaxChars, Platform.YouTube => YouTubeMaxChars, _ => InstagramMaxChars };

    /// <summary>Type group of a highlight type for caption lookup: the game's types (kills3, kills4, grenademultikill, onetapstreak / lastseconddefuse,
    /// ninjadefuse) share the "multikill" / "defuse" templates (before 2026-11-04 those keys never matched a real type).</summary>
    public static string? GroupOf(string type) => type.ToLowerInvariant() switch
    {
        "kills3" or "kills4" or "grenademultikill" or "onetapstreak" => "multikill",
        "lastseconddefuse" or "ninjadefuse" => "defuse",
        "weeklytop" => "montage",
        _ => null,
    };

    /// <summary>Lookup keys in order: "format.type", "format.group", "format.*", "type", "group", "*".</summary>
    public static IEnumerable<string> Keys(string? format, string type)
    {
        var f = string.IsNullOrWhiteSpace(format) ? "game" : format.ToLowerInvariant();
        var t = type.ToLowerInvariant();
        var g = GroupOf(t);
        yield return $"{f}.{t}";
        if (g != null) yield return $"{f}.{g}";
        yield return $"{f}.*";
        yield return t;
        if (g != null) yield return g;
        yield return "*";
    }

    /// <summary>Caption variants for a clip: (variant id, template) from the first matching key (<see cref="Keys"/>).</summary>
    public static List<(string Id, string Template)> Variants(RuntimeSettings s, string? format, string type)
    {
        foreach (var key in Keys(format, type))
            if (s.CaptionTemplates.TryGetValue(key, out var list) && list.Count > 0)
                return list.Select((tpl, i) => ($"{key}#{i}", tpl)).ToList();
        return [("default#0", "{caption}")];
    }

    /// <summary>Venue name for captions: the game's placeholder server name "TacticalLBE" (the default when no venue is set) never reaches a post.</summary>
    public static string VenueName(string? venue) =>
        string.IsNullOrWhiteSpace(venue) || venue.Trim().Equals("TacticalLBE", StringComparison.OrdinalIgnoreCase) ? "" : venue.Trim();

    /// <summary>Max hashtags per post (setting per platform; 0 = no limit).</summary>
    public static int MaxHashtagsFor(RuntimeSettings s, Platform? p) => p switch
    {
        Platform.TikTok => s.MaxHashtagsTikTok,
        Platform.Instagram => s.MaxHashtagsInstagram,
        Platform.YouTube => s.MaxHashtagsYouTube,
        _ => 0,
    };

    /// <summary>Fills one template (hook first), adds the call to action and the hashtags.</summary>
    public static string ComposeWith(Clip clip, RuntimeSettings s, string template, int maxChars, Platform? platform = null)
    {
        var caption = string.IsNullOrWhiteSpace(clip.Caption) ? $"{clip.Type} highlight" : clip.Caption.Trim();
        var text = template.Replace("{caption}", caption)
                    .Replace("{type}", clip.Type)
                    .Replace("{venue}", VenueName(clip.Venue))
                    .Replace("{round}", clip.Round.ToString(CultureInfo.InvariantCulture))
                    .Replace("{score}", clip.Score.ToString("0", CultureInfo.InvariantCulture));
        text = Tidy(text);
        if (!string.IsNullOrWhiteSpace(s.CallToAction)) text += "\n" + s.CallToAction.Trim();
        // the brand tag, then the local tags (they survive the limit), the other default tags, then the clip's specific tags
        static string[] T(string? x) => (x ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(h => h.StartsWith('#')).ToArray();
        var defaults = T(s.DefaultHashtags);
        var all = defaults.Take(1).Concat(T(s.LocalHashtags)).Concat(defaults.Skip(1)).Concat(T(clip.Hashtags))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int max = MaxHashtagsFor(s, platform);
        if (max > 0 && all.Count > max) all = all.Take(max).ToList();
        var tags = string.Join(' ', all);
        if (tags.Length == 0) return Cut(text, maxChars);
        // the text is cut before the hashtags are dropped
        int room = maxChars - tags.Length - 2;
        if (room <= 0) return Cut(text, maxChars);
        return Cut(text, room) + "\n\n" + tags;
    }

    /// <summary>Collapses blanks and the separators a missing placeholder leaves ("VRLink : x", "x | VRLink ", "VRLink :").</summary>
    public static string Tidy(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+([:,.!?])", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\|\s*$", "");
        return text.Trim();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max)].TrimEnd();

    public static List<string> TemplatesFor(RuntimeSettings s, string type)
    {
        foreach (var key in Keys(null, type).Where(k => !k.StartsWith("game.")))
            if (s.CaptionTemplates.TryGetValue(key, out var list) && list.Count > 0) return list;
        return new() { "{caption}" };
    }

    /// <param name="rotationIndex">How many posts of this type this platform already has (rotates the template).</param>
    public static string Compose(Clip clip, RuntimeSettings s, int rotationIndex, int maxChars, Platform? platform = null)
    {
        var templates = TemplatesFor(s, clip.Type);
        var t = templates[((rotationIndex % templates.Count) + templates.Count) % templates.Count];
        return ComposeWith(clip, s, t, maxChars, platform);
    }
}
