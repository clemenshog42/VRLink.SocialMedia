using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>Automatic weekly montage: due logic, selection of approved clips, one run per ISO week (no ffmpeg: the runner is faked).</summary>
public class WeeklyMontageTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    // 2026-11-08 is a Sunday (ISO week 45), 2026-11-09 a Monday (week 46)
    private static DateTime LocalToUtc(int month, int day, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, month, day, h, m, 0), Tz);
    private static DateTime L(int month, int day, int h = 0, int m = 0) => new(2026, month, day, h, m, 0);
    private static readonly TimeOnly Ten = new(10, 0);

    // ---------- pure rules ----------

    [Fact]
    public void Week_key_follows_iso_weeks_Monday_to_Sunday()
    {
        Assert.Equal("2026-W45", WeeklyMontageRules.WeekKey(L(11, 2)));          // Monday
        Assert.Equal("2026-W45", WeeklyMontageRules.WeekKey(L(11, 8, 23, 59)));  // Sunday night
        Assert.Equal("2026-W46", WeeklyMontageRules.WeekKey(L(11, 9)));          // next Monday
        Assert.Equal(45, ISOWeek.GetWeekOfYear(L(11, 8)));
        Assert.Equal("2026-W53", WeeklyMontageRules.WeekKey(L(12, 31)));         // ISO year boundary (2026 has 53 weeks)
        Assert.Equal("2026-W53", WeeklyMontageRules.WeekKey(new DateTime(2027, 1, 3)));
        Assert.Equal("2027-W01", WeeklyMontageRules.WeekKey(new DateTime(2027, 1, 4)));
    }

    [Fact]
    public void Scheduled_slot_lies_inside_the_current_iso_week()
    {
        Assert.Equal(L(11, 8, 10), WeeklyMontageRules.ScheduledLocal(L(11, 4, 15), DayOfWeek.Sunday, Ten));   // Wed -> Sunday of the same week
        Assert.Equal(L(11, 2, 10), WeeklyMontageRules.ScheduledLocal(L(11, 4, 15), DayOfWeek.Monday, Ten));
        Assert.Equal(L(11, 8, 10), WeeklyMontageRules.ScheduledLocal(L(11, 8, 23), DayOfWeek.Sunday, Ten));   // Sunday belongs to the week that ends there
        Assert.Equal(L(11, 15, 10), WeeklyMontageRules.ScheduledLocal(L(11, 9, 0), DayOfWeek.Sunday, Ten));
    }

    [Fact]
    public void Due_only_after_the_slot_and_once_per_week()
    {
        Assert.False(WeeklyMontageRules.IsDue(L(11, 4, 15), DayOfWeek.Sunday, Ten, null));        // Wednesday
        Assert.False(WeeklyMontageRules.IsDue(L(11, 8, 9, 59), DayOfWeek.Sunday, Ten, null));     // a minute early
        Assert.True(WeeklyMontageRules.IsDue(L(11, 8, 10, 0), DayOfWeek.Sunday, Ten, null));
        Assert.True(WeeklyMontageRules.IsDue(L(11, 8, 23, 0), DayOfWeek.Sunday, Ten, "2026-W44")); // app was off at 10:00, started later the same day
        Assert.False(WeeklyMontageRules.IsDue(L(11, 8, 10, 30), DayOfWeek.Sunday, Ten, "2026-W45"));
        Assert.False(WeeklyMontageRules.IsDue(L(11, 9, 12, 0), DayOfWeek.Sunday, Ten, "2026-W45")); // new week, its Sunday is not reached
        Assert.True(WeeklyMontageRules.IsDue(L(11, 15, 10, 0), DayOfWeek.Sunday, Ten, "2026-W45"));
        Assert.True(WeeklyMontageRules.IsDue(L(11, 9, 10, 0), DayOfWeek.Monday, Ten, "2026-W45")); // other configured day
    }

    [Fact]
    public void Time_text_is_parsed_with_a_safe_default()
    {
        Assert.Equal(new TimeOnly(9, 5), WeeklyMontageRules.ParseTime("9:05"));
        Assert.Equal(new TimeOnly(18, 30), WeeklyMontageRules.ParseTime(" 18:30 "));
        Assert.Equal(Ten, WeeklyMontageRules.ParseTime("soon"));
        Assert.Equal(Ten, WeeklyMontageRules.ParseTime(null));
    }

    private static readonly DateTime Now = new(2026, 11, 8, 9, 30, 0, DateTimeKind.Utc);

    private static Clip C(string id, double score = 80, DateTime? approved = null, bool bot = false, string? format = null, string type = "ace",
                          ClipStatus status = ClipStatus.Approved, string? by = "owner") =>
        new() { Id = id.GetHashCode(), HighlightId = id, Type = type, Score = score, Format = format, HeroIsBot = bot, Status = status,
                ApprovedAtUtc = approved ?? Now.AddDays(-1), ApprovedBy = by, VideoPath = Path.Combine(Path.GetTempPath(), "nonexistent-vrlink", id + ".mp4") };

    private static List<string> Ids(IEnumerable<Clip> c) => c.Select(x => x.HighlightId).ToList();

    [Fact]
    public void Only_clips_approved_by_the_owner_in_the_last_7_days_are_eligible()
    {
        var clips = new[]
        {
            C("ok-high", 90), C("ok-low", 70), C("ok-6d", 75, Now.AddDays(-6.9)),
            C("old", 99, Now.AddDays(-7.1)),                                     // approved too long ago
            C("awaiting", 99, status: ClipStatus.AwaitingApproval),              // never approved
            C("rejected", 99, status: ClipStatus.Rejected),
            C("noOwner", 99, by: null),                                          // Approved without a human record
            C("future", 99, Now.AddHours(2)),                                    // clock nonsense
        };
        Assert.Equal(["ok-high", "ok-6d", "ok-low"], Ids(WeeklyMontageRules.Eligible(clips, Now, _ => true)));
    }

    [Fact]
    public void Bot_heroes_and_montages_never_go_into_the_montage()
    {
        var clips = new[]
        {
            C("a"), C("bot", bot: true), C("m1", format: "montage"), C("m2", type: "weeklytop"), C("m3", format: "MONTAGE"), C("irl", format: "irl"),
        };
        Assert.Equal(["a", "irl"], Ids(WeeklyMontageRules.Eligible(clips, Now, _ => true)).OrderBy(x => x));
    }

    [Fact]
    public void Clips_without_a_video_file_and_operator_vetoes_are_skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlink-wm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = C("good"); good.VideoPath = Path.Combine(dir, "good.mp4"); File.WriteAllText(good.VideoPath, "x");
            var vetoed = C("vetoed"); vetoed.VideoPath = Path.Combine(dir, "vetoed.mp4"); File.WriteAllText(vetoed.VideoPath, "x");
            File.WriteAllText(ClipImporter.RejectMarkerPath(vetoed.VideoPath), "");
            var gone = C("gone");
            Assert.Equal(["good"], Ids(WeeklyMontageRules.Eligible([good, vetoed, gone], Now)));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- worker logic with the real DI graph and a fake montage runner ----------

    private sealed class FakeRunner : IMontageRunner
    {
        public List<(string[] Ids, int Top, string OutDir)> Calls { get; } = [];
        public Exception? Fail { get; set; }

        public Task<MontageRunResult> RunAsync(string clipsDir, IReadOnlyCollection<string> ids, int top, string outDir, CancellationToken ct)
        {
            Calls.Add((ids.ToArray(), top, outDir));
            if (Fail != null) throw Fail;
            var id = $"weekly-top{ids.Count}-test-{Calls.Count}";
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, id + ".mp4"), System.Text.Encoding.UTF8.GetBytes("fake-montage-" + id));
            File.WriteAllBytes(Path.Combine(outDir, id + ".jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
            File.WriteAllText(Path.Combine(outDir, id + ".json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["highlightId"] = id, ["type"] = "WeeklyTop", ["format"] = "montage", ["score"] = 80, ["round"] = 0, ["durationSec"] = 35,
                ["width"] = 1080, ["height"] = 1920, ["caption"] = "Top plays", ["hashtags"] = new[] { "#VRLink", "#top5" }, ["musicFree"] = true,
                ["createdUtc"] = "2026-11-08T09:35:00Z",
            }));
            return Task.FromResult(new MontageRunResult(id, Path.Combine(outDir, id + ".json"), Math.Min(ids.Count, top)));
        }
    }

    private static async Task<(TestHost Host, FakeRunner Runner)> HostAsync(Action<RuntimeSettings>? edit = null)
    {
        var runner = new FakeRunner();
        var h = new TestHost(LocalToUtc(11, 3, 12, 0), sc => sc.AddSingleton<IMontageRunner>(runner));
        await h.SettingsAsync(s => { s.TimeZoneId = Tz.Id; s.DryRun = true; edit?.Invoke(s); });
        return (h, runner);
    }

    private static async Task ImportAndApproveAsync(TestHost h, params string[] ids)
    {
        foreach (var id in ids) h.WriteClip(id, "ace", 80 + id.Length, day: "2026-11-03");
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await using var db = h.Db();
        foreach (var id in ids)
        {
            var c = await db.Clips.SingleAsync(x => x.HighlightId == id);
            await h.Get<Pipeline>().ApproveManuallyAsync(c.Id);
        }
    }

    private static async Task<int> LogCountAsync(TestHost h, string contains)
    {
        await using var db = h.Db();
        return await db.Log.CountAsync(l => l.Source == "WeeklyMontage" && l.Message.Contains(contains));
    }

    [Fact]
    public async Task Builds_one_montage_per_week_from_approved_clips_and_it_waits_for_approval()
    {
        var (h, runner) = await HostAsync();
        await using var _ = h;
        await ImportAndApproveAsync(h, "p1", "p2", "p3");
        // an approved bot-hero clip and an unapproved clip must stay out
        h.WriteClip("bot1", "ace", 95, day: "2026-11-03", heroIsBot: true);
        h.WriteClip("pending", "clutch", 95, day: "2026-11-03");
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await using (var db = h.Db())
        {
            var bot = await db.Clips.SingleAsync(c => c.HighlightId == "bot1");
            await h.Get<Pipeline>().ApproveManuallyAsync(bot.Id);   // even when approved by hand, a bot hero never goes in
        }
        var svc = h.Get<WeeklyMontageService>();

        h.Time.Utc = LocalToUtc(11, 8, 9, 59);
        Assert.Equal(WeeklyMontageOutcome.NotDue, await svc.RunIfDueAsync());
        Assert.Empty(runner.Calls);

        h.Time.Utc = LocalToUtc(11, 8, 10, 15);
        Assert.Equal(WeeklyMontageOutcome.Created, await svc.RunIfDueAsync());
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["p1", "p2", "p3"], call.Ids.OrderBy(x => x));
        Assert.Equal(5, call.Top);
        Assert.StartsWith(h.ClipsRoot, call.OutDir);

        await using (var db = h.Db())
        {
            var m = await db.Clips.SingleAsync(c => c.Type == "weeklytop");
            Assert.Equal("montage", m.Format);
            Assert.Null(m.ApprovedAtUtc);                      // only the own click of the owner approves it
            Assert.NotEqual(ClipStatus.Approved, m.Status);
            Assert.Equal(0, await db.Posts.CountAsync(p => p.ClipId == m.Id));
        }
        Assert.True(File.Exists(Path.Combine(h.Dir, "Montage", "weekly-2026-W45.ids.txt")));

        // later the same week, and after a restart (new service instance, same DB): no second montage
        h.Time.Utc = LocalToUtc(11, 8, 18, 0);
        Assert.Equal(WeeklyMontageOutcome.NotDue, await svc.RunIfDueAsync());
        var restarted = ActivatorUtilities.CreateInstance<WeeklyMontageService>(h.Sp);
        Assert.Equal(WeeklyMontageOutcome.NotDue, await restarted.RunIfDueAsync());
        Assert.Single(runner.Calls);

        // next week: the clips of this week are older than 7 days, the earlier montage is ignored -> skipped, and a skipped week stays skipped
        h.Time.Utc = LocalToUtc(11, 15, 10, 30);
        Assert.Equal(WeeklyMontageOutcome.Skipped, await svc.RunIfDueAsync());
        await ImportAndApproveAsync(h, "q1", "q2", "q3", "q4");
        Assert.Equal(WeeklyMontageOutcome.NotDue, await svc.RunIfDueAsync());
        // the week after, it builds again from the new clips only
        h.Time.Utc = LocalToUtc(11, 22, 10, 30);
        Assert.Equal(WeeklyMontageOutcome.Created, await svc.RunIfDueAsync());
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["q1", "q2", "q3", "q4"], runner.Calls[1].Ids.OrderBy(x => x));
    }

    [Fact]
    public async Task Fewer_than_three_approved_clips_skips_the_week_and_logs_once()
    {
        var (h, runner) = await HostAsync();
        await using var _ = h;
        await ImportAndApproveAsync(h, "p1", "p2");
        var svc = h.Get<WeeklyMontageService>();
        h.Time.Utc = LocalToUtc(11, 8, 10, 5);
        Assert.Equal(WeeklyMontageOutcome.Skipped, await svc.RunIfDueAsync());
        h.Time.Utc = LocalToUtc(11, 8, 10, 20);
        await ImportAndApproveAsync(h, "p3");   // too late for this week
        Assert.Equal(WeeklyMontageOutcome.NotDue, await svc.RunIfDueAsync());
        Assert.Equal(WeeklyMontageOutcome.NotDue, await ActivatorUtilities.CreateInstance<WeeklyMontageService>(h.Sp).RunIfDueAsync());
        Assert.Empty(runner.Calls);
        Assert.Equal(1, await LogCountAsync(h, "weekly montage skipped: only 2 approved clips"));
    }

    [Fact]
    public async Task Disabled_does_nothing()
    {
        var (h, runner) = await HostAsync(s => s.WeeklyMontageEnabled = false);
        await using var _ = h;
        await ImportAndApproveAsync(h, "p1", "p2", "p3");
        h.Time.Utc = LocalToUtc(11, 8, 11, 0);
        Assert.Equal(WeeklyMontageOutcome.Disabled, await h.Get<WeeklyMontageService>().RunIfDueAsync());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Configured_day_time_and_top_are_used()
    {
        var (h, runner) = await HostAsync(s => { s.WeeklyMontageDay = DayOfWeek.Friday; s.WeeklyMontageTime = "18:30"; s.WeeklyMontageTop = 99; });
        await using var _ = h;
        await ImportAndApproveAsync(h, "p1", "p2", "p3");
        var svc = h.Get<WeeklyMontageService>();
        h.Time.Utc = LocalToUtc(11, 6, 18, 29);   // Friday
        Assert.Equal(WeeklyMontageOutcome.NotDue, await svc.RunIfDueAsync());
        h.Time.Utc = LocalToUtc(11, 6, 18, 31);
        Assert.Equal(WeeklyMontageOutcome.Created, await svc.RunIfDueAsync());
        Assert.Equal(7, runner.Calls[0].Top);     // clamped to the composer limit
    }

    [Fact]
    public async Task A_failing_montage_never_crashes_and_is_retried_later()
    {
        var (h, runner) = await HostAsync();
        await using var _ = h;
        await ImportAndApproveAsync(h, "p1", "p2", "p3");
        var svc = h.Get<WeeklyMontageService>();
        runner.Fail = new InvalidOperationException("ffmpeg exploded");
        h.Time.Utc = LocalToUtc(11, 8, 10, 0);
        Assert.Equal(WeeklyMontageOutcome.Failed, await svc.RunIfDueAsync());
        h.Time.Utc = LocalToUtc(11, 8, 10, 30);
        Assert.Equal(WeeklyMontageOutcome.Waiting, await svc.RunIfDueAsync());      // retry pause
        Assert.Single(runner.Calls);
        Assert.Equal(1, await LogCountAsync(h, "weekly montage failed"));
        runner.Fail = null;
        h.Time.Utc = LocalToUtc(11, 8, 11, 5);
        Assert.Equal(WeeklyMontageOutcome.Created, await svc.RunIfDueAsync());
        Assert.Equal(2, runner.Calls.Count);
    }

    // ---------- learning / reports / settings ----------

    [Fact]
    public void Montage_format_has_its_own_prior_and_labels()
    {
        Assert.Equal(0.0, PerformanceModel.PriorMean("montage", "weeklytop"));
        Assert.True(PerformanceModel.PriorMean("split", "weeklytop") > PerformanceModel.PriorMean("montage", "weeklytop"));
        Assert.True(PerformanceModel.PriorMean("montage", "weeklytop") > PerformanceModel.PriorMean("game", "weeklytop"));
        Assert.Equal("Montage", PerformanceModel.Label("montage"));
        Assert.Equal("Top 5", PerformanceModel.TypeLabel("weeklytop"));
        Assert.Equal("ace", PerformanceModel.TypeLabel("ace"));
        var (arrow, _, reason, score) = new PerformanceModel([]).Predict("montage", "weeklytop");
        Assert.Equal("→", arrow);
        Assert.Equal(0.0, score);
        Assert.Contains("Montage Top 5", reason);
    }

    [Fact]
    public void Insights_report_handles_montage_outcomes()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var outcomes = new List<PostOutcome>
        {
            new(1, Platform.TikTok, "montage", "weeklytop", "montage#0", now.AddDays(-2), 1200, 3000, 0.05, 1.2, 0.5),
            new(2, Platform.Instagram, "game", "ace", "ace#0", now.AddDays(-3), 300, 800, 0.04, 0.0, 0.0),
        };
        var report = Insights.MonthlyReport(outcomes, new PerformanceModel(outcomes), 0, now, Tz);
        Assert.Contains("Montage", report);
        Assert.Contains("Top 5", report);
    }

    [Fact]
    public void Weekly_montage_settings_have_defaults_and_a_reset_section()
    {
        var d = new RuntimeSettings();
        Assert.True(d.WeeklyMontageEnabled);
        Assert.Equal(DayOfWeek.Sunday, d.WeeklyMontageDay);
        Assert.Equal("10:00", d.WeeklyMontageTime);
        Assert.Equal(5, d.WeeklyMontageTop);
        var s = new RuntimeSettings { WeeklyMontageEnabled = false, WeeklyMontageDay = DayOfWeek.Friday, WeeklyMontageTime = "20:15", WeeklyMontageTop = 3, DefaultHashtags = "#keep" };
        SettingsUpgrade.Reset(s, "weeklymontage");
        Assert.True(s.WeeklyMontageEnabled);
        Assert.Equal(DayOfWeek.Sunday, s.WeeklyMontageDay);
        Assert.Equal("10:00", s.WeeklyMontageTime);
        Assert.Equal(5, s.WeeklyMontageTop);
        Assert.Equal("#keep", s.DefaultHashtags);
        // stored settings without the new fields (older DB) load with the defaults
        var old = JsonSerializer.Deserialize<RuntimeSettings>("""{"SettingsVersion":3,"DryRun":true}""")!;
        Assert.True(old.WeeklyMontageEnabled);
        Assert.Equal(5, old.WeeklyMontageTop);
    }
}
