using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>import fake clips → auto approve → schedule at slots → FakePublisher → stats → dashboard queries.</summary>
public class DryRunEndToEndTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static DateTime Local(int d, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, d, h, m, 0), Tz);

    private static async Task<TestHost> SetupAsync()
    {
        var h = new TestHost(Local(19, 11, 0));
        await h.SettingsAsync(s =>
        {
            s.DryRun = true;
            s.TimeZoneId = Tz.Id;
            s.TikTokSlots = new() { "12:30", "19:00" };
            s.InstagramSlots = new() { "12:30", "19:00" };
            s.ScoreThreshold = 60;
        });
        h.WriteClip("a1", "ace", 95, mp4Bytes: "same-bytes"u8.ToArray());
        h.WriteClip("a2", "ace", 90);
        h.WriteClip("c1", "clutch", 85);
        h.WriteClip("low", "multikill", 20);
        h.WriteClip("dup", "ace", 99, mp4Bytes: "same-bytes"u8.ToArray()); // same content as a1
        h.WriteClip("wide", "ace", 99, width: 1920, height: 1080);
        return h;
    }

    [Fact]
    public async Task Full_pipeline_runs_without_accounts()
    {
        await using var h = await SetupAsync();
        var p = h.Get<Pipeline>();

        Assert.Equal(6, (await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot)).Imported);
        await p.CheckNewAsync();
        await using (var db = h.Db())
        {
            Assert.Equal(3, await db.Clips.CountAsync(c => c.Status == ClipStatus.AwaitingApproval));
            Assert.Equal(0, await db.Posts.CountAsync()); // nothing queued before the owner approves
        }
        Assert.Equal(3, await h.OwnerApprovesAllAwaitingAsync());

        await using (var db = h.Db())
        {
            var clips = await db.Clips.ToDictionaryAsync(c => c.HighlightId);
            Assert.Equal(ClipStatus.Approved, clips["a1"].Status);
            Assert.Equal(ClipStatus.Approved, clips["a2"].Status);
            Assert.Equal(ClipStatus.Approved, clips["c1"].Status);
            Assert.Equal(ClipStatus.Rejected, clips["low"].Status);
            Assert.Equal(ClipStatus.Rejected, clips["dup"].Status);
            Assert.Contains("duplicate of a1", clips["dup"].StatusReason);
            Assert.Equal(ClipStatus.Rejected, clips["wide"].Status);
            Assert.Equal(6, await db.Posts.CountAsync(x => x.Status == PostStatus.Queued));
        }

        // Before the first slot: nothing happens.
        Assert.Equal(0, await p.ScheduleDueAsync());

        // 12:31 local: both platforms get the best clip (a1).
        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(2, await p.ScheduleDueAsync());
        Assert.Equal(0, await p.ScheduleDueAsync()); // slot already filled
        Assert.Equal(2, await p.PublishDueAsync());

        await using (var db = h.Db())
        {
            var pub = await db.Posts.Include(x => x.Clip).Where(x => x.Status == PostStatus.Published).ToListAsync();
            Assert.Equal(2, pub.Count);
            Assert.All(pub, x => Assert.Equal("a1", x.Clip!.HighlightId));
            Assert.All(pub, x => Assert.NotNull(x.PostId));
            Assert.All(pub, x => Assert.Contains("#vrlink", x.Caption, StringComparison.OrdinalIgnoreCase));
            Assert.All(pub, x => Assert.Equal("2026-10-19 12:30", x.SlotKey));
        }

        // 19:02: last type was "ace" → the clutch clip wins although a2 has the higher score.
        h.Time.Utc = Local(19, 19, 2);
        Assert.Equal(2, await p.ScheduleDueAsync());
        Assert.Equal(2, await p.PublishDueAsync());
        // Next day 12:40: only a2 (ace) left, previous was clutch → allowed.
        h.Time.Utc = Local(20, 12, 40);
        Assert.Equal(2, await p.ScheduleDueAsync());
        Assert.Equal(2, await p.PublishDueAsync());

        await using (var db = h.Db())
        {
            var order = await db.Posts.Include(x => x.Clip).Where(x => x.Platform == Platform.TikTok)
                .OrderBy(x => x.PublishedUtc).Select(x => x.Clip!.HighlightId).ToListAsync();
            Assert.Equal(new[] { "a1", "c1", "a2" }, order);
        }

        // Stats: due 30 min after publish, then every 3 h.
        h.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(6, await p.PollStatsAsync());
        Assert.Equal(0, await p.PollStatsAsync());
        h.Time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(6, await p.PollStatsAsync());

        await using (var db = h.Db())
        {
            Assert.Equal(12, await db.Stats.CountAsync());
            Assert.All(await db.Posts.Where(x => x.Status == PostStatus.Published).ToListAsync(), x => Assert.True(x.LastViews > 0));
        }

        using var scope = h.Sp.CreateScope();
        var q = scope.ServiceProvider.GetRequiredService<DashboardQueries>();
        var sum = await q.SummaryAsync();
        Assert.All(sum, s => Assert.Equal(3, s.Total));
        var types = await q.ByTypeAsync();
        Assert.Equal(new[] { "ace", "clutch" }.OrderBy(x => x), types.Select(t => t.Type).OrderBy(x => x));
        var series = await q.ViewsOverTimeAsync(7);
        Assert.True(series[Platform.TikTok].Last().Value > 0);
    }

    [Fact]
    public async Task Pause_all_blocks_scheduling_and_publishing()
    {
        await using var h = await SetupAsync();
        var p = h.Get<Pipeline>();
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await p.CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        await h.SettingsAsync(s => s.PauseAll = true);
        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(0, await p.ScheduleDueAsync());
        Assert.Equal(0, await p.PublishDueAsync());
    }

    [Fact]
    public async Task Failed_publish_is_retried_with_backoff()
    {
        await using var h = await SetupAsync();
        await h.SettingsAsync(s => s.InstagramEnabled = false);
        var p = h.Get<Pipeline>();
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await p.CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        h.Get<FakePublisherFactory>().Get(Platform.TikTok).FailNext = 1;

        h.Time.Utc = Local(19, 12, 31);
        await p.ScheduleDueAsync();
        Assert.Equal(0, await p.PublishDueAsync());
        await using (var db = h.Db())
        {
            var post = await db.Posts.SingleAsync(x => x.Status == PostStatus.Scheduled);
            Assert.Equal(1, post.Attempts);
            Assert.NotNull(post.NextAttemptUtc);
            Assert.Contains("simulated", post.LastError);
        }
        Assert.Equal(0, await p.PublishDueAsync()); // backoff not over
        h.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1, await p.PublishDueAsync());
    }

    [Fact]
    public async Task Manual_reject_cancels_queued_posts()
    {
        await using var h = await SetupAsync();
        var p = h.Get<Pipeline>();
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await p.CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        await using var db = h.Db();
        var a1 = await db.Clips.SingleAsync(c => c.HighlightId == "a1");
        await p.RejectAsync(a1.Id);
        Assert.Equal(2, await db.Posts.CountAsync(x => x.ClipId == a1.Id && x.Status == PostStatus.Cancelled));

        h.Time.Utc = Local(19, 12, 31);
        await p.ScheduleDueAsync();
        var scheduled = await db.Posts.Include(x => x.Clip).Where(x => x.Status == PostStatus.Scheduled).ToListAsync();
        Assert.All(scheduled, x => Assert.Equal("a2", x.Clip!.HighlightId));
    }
}
