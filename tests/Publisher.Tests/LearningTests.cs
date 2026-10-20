using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>Synthetic posting history: published posts with StatsPoller-like snapshots (6 h … 7 d).</summary>
public static class SyntheticHistory
{
    public static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    /// <param name="truth">true mean log-ratio by (format,type); views48 = base · exp(truth + noise)</param>
    public static async Task SeedAsync(PublisherDb db, DateTime nowUtc, int posts, Func<int, (string Format, string Type, Platform P, int Hour, string? Variant)> pick,
                                       Func<string, string, Platform, int, string?, double> truth, int seed = 1, bool withInbox = true)
    {
        var rnd = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        for (int i = 0; i < posts; i++)
        {
            var (format, type, platform, hour, variant) = pick(i);
            var day = nowUtc.Date.AddDays(-posts / 2 - 5 + i / 2);
            var local = new DateTime(day.Year, day.Month, day.Day, hour, 0, 0, DateTimeKind.Unspecified);
            var published = TimeZoneInfo.ConvertTimeToUtc(local, Tz);
            double baseViews = platform == Platform.TikTok ? 2000 : 300; // different account sizes
            double v48 = baseViews * Math.Exp(truth(format, type, platform, hour, variant) + 0.35 * Gauss());
            var clip = new Clip
            {
                HighlightId = $"syn-{i}", Type = type, Format = format == "game" ? null : format, Score = 50 + rnd.Next(50), MatchId = $"m{i / 4}",
                Status = ClipStatus.Approved, ApprovedAtUtc = published.AddHours(-2), ApprovedBy = "owner", VideoPath = $"x{i}.mp4", JsonPath = $"x{i}.json",
                CreatedUtc = published.AddHours(-3), ImportedUtc = published.AddHours(-3), Width = 1080, Height = 1920, DurationSec = 14,
            };
            db.Clips.Add(clip);
            var post = new PlatformPost
            {
                Clip = clip, Platform = platform, Status = PostStatus.Published, PublishedUtc = published, ScheduledUtc = published,
                PostId = $"pid{i}", PostUrl = $"https://example.invalid/{i}", CaptionVariant = variant, Caption = "c",
            };
            foreach (var h in new[] { 6, 24, 51, 168 })
            {
                if (published.AddHours(h) > nowUtc) break;
                double v = v48 * (1 - Math.Exp(-h / 20.0)) / (1 - Math.Exp(-48 / 20.0));
                post.Stats.Add(new StatSnapshot { TakenUtc = published.AddHours(h), Views = (long)v, Likes = (long)(v * 0.08), Comments = (long)(v * 0.01), Shares = (long)(v * 0.005), Saves = (long)(v * 0.004) });
            }
            post.LastViews = post.Stats.LastOrDefault()?.Views;
            db.Posts.Add(post);
        }
        if (withInbox)
        {
            var c = new Clip { HighlightId = "inbox-1", Type = "ace", Format = "irl", Status = ClipStatus.Approved, ApprovedAtUtc = nowUtc, ApprovedBy = "owner", VideoPath = "i.mp4", JsonPath = "i.json" };
            db.Clips.Add(c);
            db.Posts.Add(new PlatformPost { Clip = c, Platform = Platform.TikTok, Status = PostStatus.Published, PublishedUtc = nowUtc.AddDays(-5), PrivacyLevel = "INBOX" });
        }
        await db.SaveChangesAsync();
    }
}

public class LearningTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static StatSnapshot S(DateTime t, long v) => new() { TakenUtc = t, Views = v, Likes = v / 10, Comments = v / 100 };

    [Fact]
    public void Views_at_48h_interpolate_and_need_a_long_enough_series()
    {
        var pub = Now.AddDays(-10);
        Assert.Equal(200, Performance.ViewsAt(pub, [S(pub.AddHours(24), 100), S(pub.AddHours(72), 300)], Performance.At48h)!.Value, 6);
        Assert.Equal(50, Performance.ViewsAt(pub, [S(pub.AddHours(96), 100)], Performance.At48h)!.Value, 6); // from 0 at publish
        Assert.Null(Performance.ViewsAt(pub, [S(pub.AddHours(30), 100)], Performance.At48h));
        Assert.Equal(0.11, Performance.Engagement(S(pub, 1000)), 6);
    }

    [Fact]
    public void Inbox_and_unmeasured_posts_are_excluded()
    {
        var pub = Now.AddDays(-5);
        var ok = new PlatformPost { Id = 1, Platform = Platform.Instagram, Status = PostStatus.Published, PublishedUtc = pub, PostId = "a", Clip = new Clip { Type = "ace" }, Stats = [S(pub.AddHours(50), 500)] };
        var inbox = new PlatformPost { Id = 2, Platform = Platform.TikTok, Status = PostStatus.Published, PublishedUtc = pub, PrivacyLevel = "INBOX", PostId = null, Clip = new Clip { Type = "ace" } };
        var priv = new PlatformPost { Id = 3, Platform = Platform.TikTok, Status = PostStatus.Published, PublishedUtc = pub, PrivacyLevel = "SELF_ONLY", PostId = null, Clip = new Clip { Type = "ace" } };
        Assert.True(Performance.Learnable(ok));
        Assert.False(Performance.Learnable(inbox));
        Assert.False(Performance.Learnable(priv));
        Assert.Single(Performance.Outcomes([ok, inbox, priv]));
    }

    [Fact]
    public async Task Platforms_are_normalised_so_the_same_pattern_scores_alike()
    {
        await using var h = new TestHost(Now);
        await using (var db = h.Db())
            await SyntheticHistory.SeedAsync(db, Now, 60, i => (i % 2 == 0 ? "irl" : "game", "ace", i % 4 < 2 ? Platform.TikTok : Platform.Instagram, 19, null),
                                             (f, t, p, hr, v) => f == "irl" ? 0.8 : 0.0);
        var (m, inbox, _) = await h.Get<LearningService>().ModelAsync();
        Assert.Equal(1, inbox);
        double Avg(Platform p, string f) => m.Data.Where(o => o.Platform == p && o.Format == f).Average(o => o.Score);
        // TikTok has ~7x the views of Instagram, but the IRL advantage looks the same on both
        Assert.InRange(Avg(Platform.TikTok, "irl") - Avg(Platform.TikTok, "game"), 0.8, 3.5);
        Assert.InRange(Math.Abs((Avg(Platform.TikTok, "irl") - Avg(Platform.TikTok, "game")) - (Avg(Platform.Instagram, "irl") - Avg(Platform.Instagram, "game"))), 0, 0.8);
    }

    [Fact]
    public void Cold_start_uses_the_strategy_priors()
    {
        var m = new PerformanceModel([]);
        double P(string? f, string t) => m.Predict(f, t).Score;
        Assert.True(P("reaction", "reaction") >= P("irl", "kills3"));
        Assert.True(P("irl", "ace") > P("split", "ace"));
        Assert.True(P("split", "ace") > P(null, "ace"));            // game replay last
        Assert.True(P("irl", "ace") > P("irl", "kills4"));
        Assert.True(P("irl", "kills4") > P("irl", "kills3"));
        Assert.Equal(P("irl", "ace"), P("irl", "clutch"), 6);
        Assert.Equal("low", m.Predict("irl", "ace").Confidence);
        Assert.Contains("no data yet", m.Predict("irl", "ace").Reason);
    }

    [Fact]
    public void Model_converges_to_the_true_best_arm_against_the_prior()
    {
        // truth: split Kills3 is the best by far (the prior says the opposite)
        var rnd = new Random(5);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        var arms = new[] { ("irl", "ace", 0.0), ("split", "kills3", 1.2), ("game", "ace", -0.2), ("reaction", "reaction", 0.1) };
        var obs = new List<PostOutcome>();
        int id = 0;
        foreach (var (f, t, mu) in arms)
            for (int k = 0; k < 12; k++)
            {
                double s = mu + Gauss();
                obs.Add(new PostOutcome(id++, Platform.Instagram, f, t, null, Now.AddDays(-k), 100, null, 0.1, s, s * 0.5));
            }
        var m = new PerformanceModel(obs);
        var best = arms.MaxBy(a => m.FormatType(a.Item1, a.Item2).Mean);
        Assert.Equal(("split", "kills3"), (best.Item1, best.Item2));
        Assert.Equal("↑", m.Predict("split", "kills3").Arrow);
        Assert.Equal("high", m.Predict("split", "kills3").Confidence);
        Assert.Contains("× your median", m.Predict("split", "kills3").Reason);
    }

    [Fact]
    public void Thompson_sampling_finds_the_best_caption_variant_with_20_percent_exploration()
    {
        var rnd = new Random(11);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        var truth = new Dictionary<string, double> { ["irl.ace#0"] = 0.0, ["irl.ace#1"] = 0.9, ["irl.ace#2"] = -0.3 };
        var variants = truth.Keys.ToList();
        var obs = new List<PostOutcome>();
        var picks = new List<string>();
        for (int round = 0; round < 300; round++)
        {
            var m = new PerformanceModel(obs);
            var v = m.PickVariant("irl", "ace", Platform.Instagram, variants, rnd, exploration: 0.2);
            picks.Add(v);
            double s = truth[v] + Gauss();
            obs.Add(new PostOutcome(round, Platform.Instagram, "irl", "ace", v, Now.AddHours(-round), 100, null, 0.1, s, s));
        }
        double bestShareLate = picks.Skip(200).Count(p => p == "irl.ace#1") / 100.0;
        Assert.InRange(bestShareLate, 0.7, 0.95);                           // exploits the best variant …
        Assert.True(picks.Skip(200).Count(p => p != "irl.ace#1") >= 5);    // … but keeps exploring (~20 % · 2/3)
    }

    [Fact]
    public void Slot_learning_needs_10_posts_then_picks_the_best_hours()
    {
        var few = Enumerable.Range(0, 9).Select(i => (Hour: 21, Score: 2.0)).ToList();
        var (slots, learned, _) = SlotLearning.Best(Platform.Instagram, few);
        Assert.False(learned);
        Assert.Equal(["11:30", "20:00"], slots);
        var many = Enumerable.Range(0, 24).Select(i => (Hour: new[] { 9, 13, 18, 21 }[i % 4], Score: i % 4 == 2 ? 1.5 : i % 4 == 3 ? 1.0 : -0.5)).ToList();
        (slots, learned, var curve) = SlotLearning.Best(Platform.TikTok, many);
        Assert.True(learned);
        Assert.Equal(2, slots.Length);
        Assert.Contains(slots, s => s is "18:00" or "19:00" or "17:00");
        Assert.True(curve[18] > curve[9]);
        // circular smoothing: 23:00 and 01:00 are neighbours
        var wrap = SlotLearning.HourCurve([(23, 2.0)]);
        Assert.Equal(wrap[0], wrap[22], 6);
    }

    [Fact]
    public async Task Sorting_the_approval_queue_never_changes_approval_state()
    {
        await using var h = new TestHost(Now);
        h.WriteClip("c-low", "kills3", 90);
        h.WriteClip("c-ace", "ace", 70);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await using (var db = h.Db())
            await SyntheticHistory.SeedAsync(db, Now, 30, i => ("game", i % 2 == 0 ? "ace" : "kills3", Platform.Instagram, 19, null),
                                             (f, t, p, hr, v) => t == "ace" ? 0.9 : -0.3);
        var rows = await h.Get<DashboardQueries>().ClipsAsync(nameof(ClipStatus.AwaitingApproval));
        var before = rows.Select(r => (r.Clip.Id, r.Clip.Status, r.Clip.ApprovedAtUtc)).OrderBy(x => x.Id).ToList();
        var (m, _, _) = await h.Get<LearningService>().ModelAsync();
        var preds = new Dictionary<int, (string, string, string, double)>();
        var sorted = ApprovalRanking.Sort(rows, m, preds);
        Assert.Equal("c-ace", sorted[0].Clip.HighlightId); // predicted better although its detector score is lower
        Assert.Equal(2, preds.Count);

        // nothing changed: still awaiting approval, no posts, the scheduler/publisher do nothing for them
        var after = (await h.Get<DashboardQueries>().ClipsAsync(nameof(ClipStatus.AwaitingApproval))).Select(r => (r.Clip.Id, r.Clip.Status, r.Clip.ApprovedAtUtc)).OrderBy(x => x.Id).ToList();
        Assert.Equal(before, after);
        await using var db2 = h.Db();
        Assert.Empty(await db2.Posts.Where(p => p.Clip!.HighlightId.StartsWith("c-")).ToListAsync());
        Assert.Equal(0, await h.Get<Pipeline>().PublishDueAsync());
    }

    [Fact]
    public async Task Scheduler_uses_learned_slots_after_10_posts_keeps_alternation_and_stores_the_caption_variant()
    {
        // history: Instagram posts at 21:00 do far better than at 09:00
        var now = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 20, 21, 10, 0), SyntheticHistory.Tz);
        await using var h = new TestHost(now);
        await h.SettingsAsync(s => { s.TimeZoneId = SyntheticHistory.Tz.Id; s.TikTokEnabled = false; s.InstagramSlots = ["09:00"]; });
        await using (var db = h.Db())
            await SyntheticHistory.SeedAsync(db, now, 24, i => ("game", "ace", Platform.Instagram, i % 2 == 0 ? 21 : 9, null),
                                             (f, t, p, hr, v) => hr == 21 ? 1.0 : -0.5);
        var s0 = await h.Get<SettingsStore>().GetAsync();
        var (slots, learned, n) = await h.Get<LearningService>().SlotsAsync(Platform.Instagram, s0);
        Assert.True(learned);
        Assert.True(n >= 10);
        Assert.Contains("21:00", slots);

        h.WriteClip("k1", "kills3", 90); h.WriteClip("k2", "kills3", 85);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        // 21:10 local: the learned evening slot is due (the configured 09:00 slot would not be)
        int assigned = await h.Get<Pipeline>().ScheduleDueAsync();
        await using var db2 = h.Db();
        var sched = await db2.Posts.Include(p => p.Clip).Where(p => p.Status == PostStatus.Scheduled).ToListAsync();
        Assert.Equal(1, assigned);
        Assert.StartsWith("game.*#", sched.Single().CaptionVariant);
        // alternation still applies: the history ended with "ace", the waiting clips are kills3 → allowed
        Assert.All(sched, p => Assert.NotNull(p.Clip!.ApprovedAtUtc));

        // switched off → configured slots again
        var off = await h.SettingsAsync(s => s.UseLearnedSlots = false);
        Assert.False((await h.Get<LearningService>().SlotsAsync(Platform.Instagram, off)).Learned);
    }

    [Fact]
    public async Task Monthly_report_has_tables_inbox_note_and_detector_weights()
    {
        await using var h = new TestHost(Now);
        await using (var db = h.Db())
            await SyntheticHistory.SeedAsync(db, Now, 40, i => (new[] { "irl", "split", "game", "reaction" }[i % 4], new[] { "ace", "kills3", "clutch" }[i % 3], i % 2 == 0 ? Platform.TikTok : Platform.Instagram, 19, $"x#{i % 2}"),
                                             (f, t, p, hr, v) => (t == "ace" ? 0.6 : 0) + (f == "irl" ? 0.4 : 0));
        var (m, inbox, _) = await h.Get<LearningService>().ModelAsync();
        var md = Insights.MonthlyReport(m.Data, m, inbox, Now, SyntheticHistory.Tz);
        Assert.Contains("## By format", md);
        Assert.Contains("## By caption variant", md);
        Assert.Contains("TikTok inbox", md);
        Assert.Contains("\"typeScoreMultipliers\"", md);
        var w = Insights.DetectorWeights(m.Data);
        Assert.Equal(1.0, w["Kills3"]);
        Assert.True(w["Ace"] > 1.2);
    }
}
