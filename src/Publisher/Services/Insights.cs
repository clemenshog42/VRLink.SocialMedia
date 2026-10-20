using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VRLinkPublisher.Services;

public sealed record InsightRow(string Key, int N, double MeanScore, double TimesMedian, double Engagement, double MedianViews48h);

/// <summary>Grouped performance for the Insights page and the monthly report (pure).</summary>
public static class Insights
{
    public static List<InsightRow> By(IEnumerable<PostOutcome> o, Func<PostOutcome, string> key) =>
        o.GroupBy(key).Select(g => new InsightRow(g.Key, g.Count(), g.Average(x => x.Score), Math.Exp(g.Average(x => x.LogRatio)),
                                                  g.Average(x => x.Engagement), Performance.Median(g.Select(x => x.Views48h).ToList())))
         .OrderByDescending(r => r.MeanScore).ToList();

    public static string Hour(PostOutcome o, TimeZoneInfo tz) => $"{TimeZoneInfo.ConvertTimeFromUtc(o.PublishedUtc, tz).Hour:00}:00";

    /// <summary>
    /// Suggested multipliers for the Unity HighlightRules type scores: exp(shrunk mean log-ratio) of each type
    /// (shrinkage n/(n+3) towards 1×), clamped 0.5–2, normalised so Kills3 = 1.0; types without data stay 1.0. Applying them is a manual PO step.
    /// </summary>
    public static Dictionary<string, double> DetectorWeights(IEnumerable<PostOutcome> o)
    {
        var map = new Dictionary<string, string> { ["ace"] = "Ace", ["kills4"] = "Kills4", ["kills3"] = "Kills3", ["clutch"] = "Clutch",
            ["onetapstreak"] = "OneTapStreak", ["lastseconddefuse"] = "LastSecondDefuse", ["grenademultikill"] = "GrenadeMultiKill", ["ninjadefuse"] = "NinjaDefuse" };
        var raw = map.ToDictionary(kv => kv.Value, kv =>
        {
            var g = o.Where(x => x.Type == kv.Key).ToList();
            double shrunk = g.Count == 0 ? 0 : g.Average(x => x.LogRatio) * g.Count / (g.Count + 3.0);
            return Math.Clamp(Math.Exp(shrunk), 0.5, 2.0);
        });
        double k3 = raw["Kills3"];
        // types without measured posts keep their current weight (1.0)
        return raw.ToDictionary(kv => kv.Key, kv => o.Any(x => map.First(m => m.Value == kv.Key).Key == x.Type) ? Math.Round(kv.Value / k3, 2) : 1.0);
    }

    public static string MonthlyReport(IReadOnlyList<PostOutcome> o, PerformanceModel m, int inboxExcluded, DateTime nowUtc, TimeZoneInfo tz)
    {
        var sb = new StringBuilder();
        var month = o.Where(x => x.PublishedUtc >= nowUtc.AddDays(-30)).ToList();
        sb.AppendLine($"# VRLink social media — monthly report ({nowUtc.AddDays(-30):yyyy-MM-dd} – {nowUtc:yyyy-MM-dd})");
        sb.AppendLine();
        sb.AppendLine($"- Measured posts (48 h views available): **{month.Count}** in the last 30 days, {o.Count} in total.");
        sb.AppendLine($"- Not measurable: {inboxExcluded} post(s) (TikTok inbox/private posts have no stats; learning uses Instagram + direct TikTok only).");
        sb.AppendLine($"- Score = log views at 48 h vs the platform's rolling 30-day median (0 = typical, +1 ≈ one spread better). ×median = typical multiple of your median views.");
        sb.AppendLine($"- Recommended daily mix: {DailyMix.Recommend(m)}");
        sb.AppendLine();
        void Table(string title, List<InsightRow> rows)
        {
            sb.AppendLine($"## {title}");
            sb.AppendLine("| | posts | score | ×median | engagement | median views 48 h |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var r in rows) sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Key} | {r.N} | {r.MeanScore:+0.00;-0.00} | {r.TimesMedian:0.00}× | {r.Engagement * 100:0.0} % | {r.MedianViews48h:0} |"));
            sb.AppendLine();
        }
        Table("By format", By(month, x => PerformanceModel.Label(x.Format)));
        Table("By highlight type", By(month, x => x.Type));
        Table("By format + type", By(month, x => $"{PerformanceModel.Label(x.Format)} {x.Type}"));
        Table("By caption variant", By(month, x => x.CaptionVariant ?? "(none)"));
        Table("By posting hour (local)", By(month, x => Hour(x, tz)).OrderBy(r => r.Key).ToList());
        Table("By platform", By(month, x => x.Platform.ToString()));
        sb.AppendLine("## Suggested detector score weights (Unity HighlightRules)");
        sb.AppendLine("Multipliers for the per-type highlight score, from all measured posts (shrunk towards 1×, Kills3 = 1.0).");
        sb.AppendLine("Applying them is a separate manual step by the PO in the Unity project.");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(new { typeScoreMultipliers = DetectorWeights(o), basedOnPosts = o.Count, generatedUtc = nowUtc.ToString("O") },
                                               new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        return sb.ToString();
    }
}
