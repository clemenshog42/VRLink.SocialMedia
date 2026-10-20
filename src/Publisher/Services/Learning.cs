using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

// =====================================================================================================================
// V5 learning loop. Predictions only SORT and RECOMMEND: they never approve, schedule-without-approval or post anything.
// =====================================================================================================================

/// <summary>One post's measured outcome.</summary>
public sealed record PostOutcome(
    int PostId, Platform Platform, string Format, string Type, string? CaptionVariant, DateTime PublishedUtc,
    double Views48h, double? Views7d, double Engagement, double Score, double LogRatio);

/// <summary>Views at 48 h / 7 d (interpolated from the StatsPoller series), engagement rate and the per-platform normalised score (pure).</summary>
public static class Performance
{
    public static readonly TimeSpan At48h = TimeSpan.FromHours(48), At7d = TimeSpan.FromDays(7);

    /// <summary>Views at a given age; linear interpolation between snapshots (0 views at publish time). Null if the series doesn't reach that age.</summary>
    public static double? ViewsAt(DateTime publishedUtc, IReadOnlyList<StatSnapshot> series, TimeSpan age)
    {
        var target = publishedUtc + age;
        var pts = series.OrderBy(s => s.TakenUtc).Select(s => (t: s.TakenUtc, v: (double)s.Views)).Prepend((publishedUtc, 0)).ToList();
        if (pts[^1].t < target) return null;
        for (int i = 1; i < pts.Count; i++)
            if (pts[i].t >= target)
            {
                var (t0, v0) = pts[i - 1]; var (t1, v1) = pts[i];
                double u = (t1 - t0).TotalSeconds <= 0 ? 1 : (target - t0).TotalSeconds / (t1 - t0).TotalSeconds;
                return v0 + (v1 - v0) * u;
            }
        return null;
    }

    public static double Engagement(StatSnapshot s) => s.Views <= 0 ? 0 : (double)(s.Likes + s.Comments + s.Shares + s.Saves) / s.Views;

    /// <summary>Posts that can be learned from: published, with a post id and stats (TikTok inbox/private posts have none).</summary>
    public static bool Learnable(PlatformPost p) =>
        p.Status == PostStatus.Published && p.PublishedUtc != null && p.PrivacyLevel != "INBOX" && !string.IsNullOrEmpty(p.PostId) && p.Stats.Count > 0;

    /// <summary>
    /// Normalised score per post: log(1+views48h) relative to the platform's rolling 30-day median (posts published in the
    /// 30 days before it; all earlier posts when fewer than 3), divided by a robust spread (1.4826·MAD of log views, ≥ 0.35).
    /// 0 = typical for that account, +1 ≈ one "standard deviation" better. TikTok and Instagram become comparable.
    /// </summary>
    public static List<PostOutcome> Outcomes(IEnumerable<PlatformPost> posts)
    {
        var raw = posts.Where(Learnable).Select(p =>
        {
            var v48 = ViewsAt(p.PublishedUtc!.Value, p.Stats, At48h);
            return v48 == null ? null : new { P = p, V48 = v48.Value, V7 = ViewsAt(p.PublishedUtc!.Value, p.Stats, At7d), Eng = Engagement(p.Stats.OrderBy(s => s.TakenUtc).Last()) };
        }).Where(x => x != null).Select(x => x!).ToList();

        var result = new List<PostOutcome>();
        foreach (var grp in raw.GroupBy(x => x.P.Platform))
        {
            var all = grp.OrderBy(x => x.P.PublishedUtc).ToList();
            var logs = all.Select(x => Math.Log(1 + x.V48)).ToList();
            double spread = Math.Max(0.35, 1.4826 * Mad(logs));
            foreach (var x in all)
            {
                var window = all.Where(o => o != x && o.P.PublishedUtc < x.P.PublishedUtc && o.P.PublishedUtc >= x.P.PublishedUtc!.Value.AddDays(-30)).Select(o => Math.Log(1 + o.V48)).ToList();
                if (window.Count < 3) window = all.Where(o => o != x).Select(o => Math.Log(1 + o.V48)).ToList();
                double median = window.Count == 0 ? Math.Log(1 + x.V48) : Median(window);
                double lr = Math.Log(1 + x.V48) - median;
                result.Add(new PostOutcome(x.P.Id, x.P.Platform, Norm(x.P.Clip?.Format), Norm(x.P.Clip?.Type), x.P.CaptionVariant, x.P.PublishedUtc!.Value,
                                           x.V48, x.V7, x.Eng, lr / spread, lr));
            }
        }
        return result;
    }

    public static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? "game" : s.Trim().ToLowerInvariant();
    public static double Median(IReadOnlyList<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
    public static double Mad(IReadOnlyList<double> v) { if (v.Count < 2) return 0; var m = Median(v); return Median(v.Select(x => Math.Abs(x - m)).ToList()); }
}

/// <summary>Posterior of a normal-normal model (known observation noise).</summary>
public readonly record struct Belief(double Mean, double Var, int N, double MeanLogRatio)
{
    public double Sd => Math.Sqrt(Var);
    public double Sample(Random r) => Mean + Sd * Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}

/// <summary>
/// Hierarchical normal-normal model on the normalised score: (format,type) ← cold-start prior; (format,type,platform) uses the
/// (format,type) posterior as prior; (format,type,platform,captionVariant) uses that as prior. Partial pooling keeps sparse
/// arms sensible. Thompson sampling (+ ε-random exploration) picks caption variants.
/// </summary>
public sealed class PerformanceModel
{
    public const double Noise = 1.0, PriorVar = 0.5, ChildVar = 0.25;

    /// <summary>Cold-start strategy prior: reaction ≥ irl &gt; split &gt; game; Ace/Clutch &gt; Kills4 &gt; Kills3 &gt; others.</summary>
    public static double PriorMean(string format, string type)
    {
        double f = format switch { "reaction" => 0.45, "irl" => 0.40, "split" => 0.15, _ => -0.25 };
        double t = type switch { "ace" or "clutch" => 0.30, "kills4" => 0.15, "kills3" => 0.05, "reaction" => 0.0, _ => -0.05 };
        return f + t;
    }

    private readonly List<PostOutcome> _data;
    public PerformanceModel(IEnumerable<PostOutcome> outcomes) => _data = outcomes.ToList();
    public IReadOnlyList<PostOutcome> Data => _data;

    private static Belief Update(double priorMean, double priorVar, IReadOnlyList<PostOutcome> obs)
    {
        double prec = 1 / priorVar + obs.Count / Noise;
        double mean = (priorMean / priorVar + obs.Sum(o => o.Score) / Noise) / prec;
        return new Belief(mean, 1 / prec, obs.Count, obs.Count == 0 ? 0 : obs.Average(o => o.LogRatio));
    }

    public Belief FormatType(string format, string type)
    {
        format = Performance.Norm(format); type = Performance.Norm(type);
        return Update(PriorMean(format, type), PriorVar, _data.Where(o => o.Format == format && o.Type == type).ToList());
    }

    public Belief Platform(string format, string type, Platform p)
    {
        var parent = FormatType(format, type);
        format = Performance.Norm(format); type = Performance.Norm(type);
        return Update(parent.Mean, parent.Var + ChildVar, _data.Where(o => o.Format == format && o.Type == type && o.Platform == p).ToList());
    }

    public Belief Variant(string format, string type, Platform p, string variant)
    {
        var parent = Platform(format, type, p);
        format = Performance.Norm(format); type = Performance.Norm(type);
        return Update(parent.Mean, parent.Var + ChildVar, _data.Where(o => o.Format == format && o.Type == type && o.Platform == p && o.CaptionVariant == variant).ToList());
    }

    /// <summary>Thompson sampling over caption variants, with an ε share of uniform exploration.</summary>
    public string PickVariant(string format, string type, Platform p, IReadOnlyList<string> variants, Random rnd, double exploration = 0.2)
    {
        if (variants.Count == 1) return variants[0];
        if (rnd.NextDouble() < exploration) return variants[rnd.Next(variants.Count)];
        return variants.MaxBy(v => Variant(format, type, p, v).Sample(rnd))!;
    }

    /// <summary>Badge for the approval queue: ↑/→/↓, confidence and a human reason.</summary>
    public (string Arrow, string Confidence, string Reason, double Score) Predict(string? format, string type)
    {
        var f = Performance.Norm(format); var t = Performance.Norm(type);
        var b = FormatType(f, t);
        string arrow = b.Mean > 0.25 ? "↑" : b.Mean < -0.25 ? "↓" : "→";
        string conf = b.N >= 8 ? "high" : b.N >= 3 ? "medium" : "low";
        string label = $"{Label(f)} {t switch { "ace" => "Ace", "clutch" => "Clutch", "kills4" => "4K", "kills3" => "3K", "reaction" => "", _ => t }}".Trim();
        string reason = b.N == 0
            ? $"{label}: no data yet — strategy prior ({(b.Mean > 0 ? "expected above" : "expected below")} your median)"
            : $"{label} clips average {Math.Exp(b.MeanLogRatio):0.0}× your median ({b.N} post{(b.N == 1 ? "" : "s")})";
        return (arrow, conf, reason, b.Mean);
    }

    public static string Label(string format) => format switch { "irl" => "IRL", "split" => "Split", "reaction" => "Reaction", _ => "Game" };
}

/// <summary>Best posting hours per platform from own data (circular smoothing over 24 h; ≥ 10 posts before overriding the defaults).</summary>
public static class SlotLearning
{
    public const int MinPosts = 10;
    public static readonly Dictionary<Platform, string[]> Defaults = new()
    {
        [Platform.TikTok] = ["12:00", "19:00"],
        [Platform.Instagram] = ["11:30", "20:00"],
    };

    /// <summary>Smoothed mean score per local hour (24 values; Gaussian kernel σ = 1.5 h on the circle; shrunk towards 0 by a weak prior).</summary>
    public static double[] HourCurve(IEnumerable<(int Hour, double Score)> obs, double sigma = 1.5)
    {
        var o = obs.ToList();
        var curve = new double[24];
        for (int h = 0; h < 24; h++)
        {
            double w = 0.5, s = 0; // prior weight 0.5 at score 0
            foreach (var (hour, score) in o)
            {
                int d = Math.Abs(hour - h); d = Math.Min(d, 24 - d);
                double k = Math.Exp(-d * d / (2 * sigma * sigma));
                w += k; s += k * score;
            }
            curve[h] = s / w;
        }
        return curve;
    }

    /// <summary>Posting hours considered at all (local): 07:00–23:00.</summary>
    public const int EarliestHour = 7, LatestHour = 23;

    /// <summary>
    /// Two best hours ≥ 4 h apart, chosen only among hours that have own data nearby (kernel support ≥ 1 post) inside 07–23 h;
    /// a missing second hour is filled with the default slot. Fewer than MinPosts posts → the defaults.
    /// </summary>
    public static (string[] Slots, bool Learned, double[] Curve) Best(Platform p, IEnumerable<(int Hour, double Score)> obs)
    {
        var o = obs.ToList();
        var curve = HourCurve(o);
        if (o.Count < MinPosts) return (Defaults[p], false, curve);
        double Support(int h) => o.Sum(x => { int d = Math.Abs(x.Hour - h); d = Math.Min(d, 24 - d); return Math.Exp(-d * d / (2 * 1.5 * 1.5)); });
        var cands = Enumerable.Range(EarliestHour, LatestHour - EarliestHour + 1).Where(h => Support(h) >= 1.0).OrderByDescending(h => curve[h]).ToList();
        if (cands.Count == 0) return (Defaults[p], false, curve);
        int first = cands[0];
        static int Dist(int a, int b) => Math.Min(Math.Abs(a - b), 24 - Math.Abs(a - b));
        var slots = new List<string> { $"{first:00}:00" };
        int second = cands.FirstOrDefault(h => Dist(h, first) >= 4, -1);
        if (second >= 0) slots.Add($"{second:00}:00");
        else slots.Add(Defaults[p].First(d => Dist(int.Parse(d[..2]), first) >= 4));
        return (slots.OrderBy(x => x).ToArray(), true, curve);
    }
}

/// <summary>DB-backed access to the model (built fresh from the stats; small data).</summary>
public sealed class LearningService(IDbContextFactory<PublisherDb> dbf)
{
    private readonly Random _rnd = new();

    public async Task<(PerformanceModel Model, int InboxExcluded, int Immature)> ModelAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var posts = await db.Posts.AsNoTracking().Include(p => p.Clip).Include(p => p.Stats)
            .Where(p => p.Status == PostStatus.Published).ToListAsync(ct);
        var outcomes = Performance.Outcomes(posts);
        int inbox = posts.Count(p => p.PrivacyLevel == "INBOX" || string.IsNullOrEmpty(p.PostId));
        return (new PerformanceModel(outcomes), inbox, posts.Count(Performance.Learnable) - outcomes.Count);
    }

    /// <summary>Learned or default slots for a platform (local times), per the "Use learned slots" setting.</summary>
    public async Task<(List<string> Slots, bool Learned, int Posts)> SlotsAsync(Platform p, RuntimeSettings s, CancellationToken ct = default)
    {
        var configured = p == Platform.TikTok ? s.TikTokSlots : s.InstagramSlots;
        if (s.UseLearnedSlots == false) return (configured, false, 0);
        var (m, _, _) = await ModelAsync(ct);
        var tz = SlotPlanner.ResolveTimeZone(s.TimeZoneId);
        var obs = m.Data.Where(o => o.Platform == p).Select(o => (TimeZoneInfo.ConvertTimeFromUtc(o.PublishedUtc, tz).Hour, o.Score)).ToList();
        var (slots, learned, _) = SlotLearning.Best(p, obs);
        // default ON once the threshold is reached; before that the configured slots stay in charge
        return learned ? (slots.ToList(), true, obs.Count) : (configured, false, obs.Count);
    }

    public async Task<string> PickVariantAsync(Clip clip, Platform p, IReadOnlyList<string> variants, RuntimeSettings s, CancellationToken ct = default)
    {
        var (m, _, _) = await ModelAsync(ct);
        lock (_rnd) return m.PickVariant(Performance.Norm(clip.Format), Performance.Norm(clip.Type), p, variants, _rnd, s.ExplorationShare);
    }
}

/// <summary>Approval queue order (pure, read-only): predicted performance first, then the detector score, then age.</summary>
public static class ApprovalRanking
{
    public static List<ClipRow> Sort(IEnumerable<ClipRow> rows, PerformanceModel model, IDictionary<int, (string Arrow, string Confidence, string Reason, double Score)>? predictions = null)
    {
        var list = rows.Select(r => (r, p: model.Predict(r.Clip.Format, r.Clip.Type))).ToList();
        if (predictions != null) foreach (var (r, p) in list) predictions[r.Clip.Id] = p;
        return list.OrderByDescending(x => x.p.Score).ThenByDescending(x => x.r.Clip.Score).ThenBy(x => x.r.Clip.CreatedUtc).Select(x => x.r).ToList();
    }
}

/// <summary>Recommended daily mix from the model (pure): one reaction-or-IRL clip + one other (best predicted combinations).</summary>
public static class DailyMix
{
    public static readonly string[] Formats = ["reaction", "irl", "split", "game"];
    public static readonly string[] Types = ["ace", "clutch", "kills4", "kills3", "reaction"];

    public static List<(string Format, string Type, double Mean)> Ranked(PerformanceModel m) =>
        // conservative: mean − ½·sd, so well-measured combinations beat untested ones with an optimistic prior
        Formats.SelectMany(f => (f == "reaction" ? new[] { "reaction" } : Types.Where(t => t != "reaction")).Select(t => { var b = m.FormatType(f, t); return (f, t, b.Mean - 0.5 * b.Sd); }))
               .OrderByDescending(x => x.Item3).ToList();

    public static string Recommend(PerformanceModel m)
    {
        var r = Ranked(m);
        var first = r.First(x => x.Format is "reaction" or "irl");
        var second = r.First(x => x.Format != first.Format);
        string Name((string Format, string Type, double Mean) x) => x.Format == "reaction" ? "Reaction" : $"{PerformanceModel.Label(x.Format)} {x.Type}";
        return $"1 × {Name(first)} (or another reaction/IRL clip) + 1 × {Name(second)}";
    }
}
