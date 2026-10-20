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

    /// <summary>Caption variants for a clip: (variant id, template) from the first matching key "format.type", "format.*", "type", "*".</summary>
    public static List<(string Id, string Template)> Variants(RuntimeSettings s, string? format, string type)
    {
        var f = string.IsNullOrWhiteSpace(format) ? "game" : format.ToLowerInvariant();
        var t = type.ToLowerInvariant();
        foreach (var key in new[] { $"{f}.{t}", $"{f}.*", t, "*" })
            if (s.CaptionTemplates.TryGetValue(key, out var list) && list.Count > 0)
                return list.Select((tpl, i) => ($"{key}#{i}", tpl)).ToList();
        return [("default#0", "{caption}")];
    }

    /// <summary>Fills one template (hook first) and appends the hashtags.</summary>
    public static string ComposeWith(Clip clip, RuntimeSettings s, string template, int maxChars)
    {
        var caption = string.IsNullOrWhiteSpace(clip.Caption) ? $"{clip.Type} highlight" : clip.Caption.Trim();
        var text = template.Replace("{caption}", caption)
                    .Replace("{type}", clip.Type)
                    .Replace("{venue}", clip.Venue ?? "")
                    .Replace("{round}", clip.Round.ToString(CultureInfo.InvariantCulture))
                    .Replace("{score}", clip.Score.ToString("0", CultureInfo.InvariantCulture));
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        var tags = string.Join(' ', (clip.Hashtags + " " + s.DefaultHashtags)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        var full = tags.Length > 0 ? text + "\n\n" + tags : text;
        return full.Length <= maxChars ? full : full[..maxChars];
    }

    public static List<string> TemplatesFor(RuntimeSettings s, string type)
    {
        if (s.CaptionTemplates.TryGetValue(type, out var list) && list.Count > 0) return list;
        if (s.CaptionTemplates.TryGetValue("*", out var fb) && fb.Count > 0) return fb;
        return new() { "{caption}" };
    }

    /// <param name="rotationIndex">How many posts of this type this platform already has (rotates the template).</param>
    public static string Compose(Clip clip, RuntimeSettings s, int rotationIndex, int maxChars)
    {
        var templates = TemplatesFor(s, clip.Type);
        var t = templates[((rotationIndex % templates.Count) + templates.Count) % templates.Count];
        var caption = string.IsNullOrWhiteSpace(clip.Caption) ? $"{clip.Type} highlight" : clip.Caption.Trim();
        var text = t.Replace("{caption}", caption)
                    .Replace("{type}", clip.Type)
                    .Replace("{venue}", clip.Venue ?? "")
                    .Replace("{round}", clip.Round.ToString(CultureInfo.InvariantCulture))
                    .Replace("{score}", clip.Score.ToString("0", CultureInfo.InvariantCulture));
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        var tags = string.Join(' ', (clip.Hashtags + " " + s.DefaultHashtags)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        var full = tags.Length > 0 ? text + "\n\n" + tags : text;
        return full.Length <= maxChars ? full : full[..maxChars];
    }
}
