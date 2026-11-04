using System.Text.Json;
using System.Text.Json.Nodes;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

/// <summary>
/// One-time upgrades of stored runtime settings (version marker <see cref="RuntimeSettings.SettingsVersion"/>).
/// A value is only replaced while it still equals the OLD default; anything the owner edited is kept.
/// </summary>
public static class SettingsUpgrade
{
    public const int Current = 3;

    // v1 defaults (before V5)
    public static readonly List<string> V1TikTokSlots = ["12:30", "19:00"];
    public static readonly List<string> V1InstagramSlots = ["13:00", "19:30"];
    public static readonly Dictionary<string, List<string>> V1CaptionTemplates = new()
    {
        ["*"] = ["{caption}", "{caption} | VRLink {venue}", "Highlight from VRLink {venue}: {caption}"],
        ["ace"] = ["ACE! {caption}", "One player, five eliminations. {caption}", "Ace in round {round}. {caption}"],
        ["clutch"] = ["Clutch! {caption}", "1vX in round {round}. {caption}", "Nerves of steel. {caption}"],
        ["multikill"] = ["{caption}", "Multi kill at VRLink {venue}. {caption}"],
        ["defuse"] = ["Last-second defuse. {caption}", "Defused! {caption}"],
    };

    // v2 defaults (V5, before the 2026-11-04 question variants)
    public static readonly Dictionary<string, List<string>> V2CaptionTemplates = new()
    {
        ["irl.ace"] = new() { "5 kills. Real legs. {caption}", "This ace happened in a real room. {caption}", "No controller on a couch. Real ace. {caption}" },
        ["irl.clutch"] = new() { "1 vs all. In real life. {caption}", "Heart rate: yes. Real clutch. {caption}" },
        ["irl.*"] = new() { "Real room. Real legs. Real game. {caption}", "This is what VR looks like from outside. {caption}" },
        ["split.*"] = new() { "Top: real. Bottom: in game. {caption}", "Same moment, two worlds. {caption}" },
        ["reaction.*"] = new() { "When it gets real. {caption}", "Nobody was ready for this. {caption}", "Real reactions only. {caption}" },
        ["game.*"] = new() { "{caption}", "Highlight from VRLink {venue}: {caption}" },
        ["*"] = new() { "{caption}", "{caption} | VRLink {venue}", "Highlight from VRLink {venue}: {caption}" },
        ["ace"] = new() { "ACE! {caption}", "One player, five eliminations. {caption}", "Ace in round {round}. {caption}" },
        ["clutch"] = new() { "Clutch! {caption}", "1vX in round {round}. {caption}", "Nerves of steel. {caption}" },
        ["multikill"] = new() { "{caption}", "Multi kill at VRLink {venue}. {caption}" },
        ["defuse"] = new() { "Last-second defuse. {caption}", "Defused! {caption}" },
    };

    /// <summary>Stored version: 0/1 when the json has no marker (written before the marker existed).</summary>
    public static int StoredVersion(string json)
    {
        try { return JsonNode.Parse(json)?["SettingsVersion"]?.GetValue<int>() ?? 1; } catch { return 1; }
    }

    public static bool SameTemplates(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));

    /// <returns>Human-readable list of what was upgraded (empty = nothing to do).</returns>
    public static List<string> Apply(RuntimeSettings s, int fromVersion)
    {
        var changes = new List<string>();
        if (fromVersion < 2)
        {
            var d = new RuntimeSettings();
            if (s.TikTokSlots.SequenceEqual(V1TikTokSlots)) { s.TikTokSlots = d.TikTokSlots; changes.Add($"TikTok slots {string.Join("/", V1TikTokSlots)} → {string.Join("/", d.TikTokSlots)}"); }
            else changes.Add("TikTok slots kept (edited by you)");
            if (s.InstagramSlots.SequenceEqual(V1InstagramSlots)) { s.InstagramSlots = d.InstagramSlots; changes.Add($"Instagram slots {string.Join("/", V1InstagramSlots)} → {string.Join("/", d.InstagramSlots)}"); }
            else changes.Add("Instagram slots kept (edited by you)");
            if (SameTemplates(s.CaptionTemplates, V1CaptionTemplates)) { s.CaptionTemplates = d.CaptionTemplates; changes.Add("caption templates → hook-first A/B variants"); }
            else changes.Add("caption templates kept (edited by you)");
        }
        if (fromVersion < 3)
        {
            var d = new RuntimeSettings();
            if (SameTemplates(s.CaptionTemplates, V2CaptionTemplates)) { s.CaptionTemplates = d.CaptionTemplates; changes.Add("caption templates → + question variants, multikill/defuse keys match the game's types, montage"); }
            else if (fromVersion >= 2) changes.Add("caption templates kept (edited by you)");
        }
        s.SettingsVersion = Current;
        return changes;
    }

    /// <summary>"Reset to defaults" per Settings section.</summary>
    public static void Reset(RuntimeSettings s, string section)
    {
        var d = new RuntimeSettings();
        switch (section)
        {
            case "schedule":
                s.TikTokSlots = d.TikTokSlots; s.InstagramSlots = d.InstagramSlots; s.YouTubeSlots = d.YouTubeSlots; s.UseLearnedSlots = d.UseLearnedSlots;
                s.SlotGraceMinutes = d.SlotGraceMinutes; s.StrictTypeAlternation = d.StrictTypeAlternation; s.ExplorationShare = d.ExplorationShare;
                break;
            case "checks":
                s.ScoreThreshold = d.ScoreThreshold; s.MinDurationSec = d.MinDurationSec; s.MaxDurationSec = d.MaxDurationSec;
                s.RequiredWidth = d.RequiredWidth; s.RequiredHeight = d.RequiredHeight; s.MaxFileSizeMb = d.MaxFileSizeMb; s.MinFileSizeKb = d.MinFileSizeKb;
                s.AllowBotHighlights = d.AllowBotHighlights;
                break;
            case "stats":
                s.MaxAttempts = d.MaxAttempts; s.StatsIntensiveHours = d.StatsIntensiveHours; s.StatsIntensiveDays = d.StatsIntensiveDays; s.StatsStopAfterDays = d.StatsStopAfterDays;
                break;
            case "captions":
                s.CaptionTemplates = d.CaptionTemplates; s.DefaultHashtags = d.DefaultHashtags; s.LocalHashtags = d.LocalHashtags; s.CallToAction = d.CallToAction;
                s.MaxHashtagsTikTok = d.MaxHashtagsTikTok; s.MaxHashtagsInstagram = d.MaxHashtagsInstagram; s.MaxHashtagsYouTube = d.MaxHashtagsYouTube;
                break;
            default: throw new ArgumentException("unknown section " + section);
        }
    }
}
