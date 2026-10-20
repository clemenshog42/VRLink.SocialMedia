using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>Hard rule: every video is approved by the owner before upload (no automatic path, defense in depth).</summary>
public class ManualApprovalTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static DateTime Local(int d, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, d, h, m, 0), Tz);

    private static async Task<TestHost> HostAsync()
    {
        var h = new TestHost(Local(19, 11, 0));
        await h.SettingsAsync(s => { s.TimeZoneId = Tz.Id; s.InstagramEnabled = false; s.DryRun = true; });
        h.WriteClip("a1", "ace", 95);
        h.WriteClip("c1", "clutch", 90);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        return h;
    }

    [Fact]
    public async Task Checker_never_approves_and_nothing_is_published_without_owner()
    {
        await using var h = await HostAsync();
        var p = h.Get<Pipeline>();
        for (int i = 0; i < 3; i++) await p.CheckNewAsync();          // repeated checker runs
        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(0, await p.ScheduleDueAsync());
        Assert.Equal(0, await p.PublishDueAsync());
        await using var db = h.Db();
        Assert.All(await db.Clips.ToListAsync(), c =>
        {
            Assert.Equal(ClipStatus.AwaitingApproval, c.Status);
            Assert.Null(c.ApprovedAtUtc);
        });
        Assert.Equal(0, await db.Posts.CountAsync());
    }

    [Fact]
    public async Task Tampered_status_in_db_is_refused_by_scheduler_and_publisher()
    {
        await using var h = await HostAsync();
        var p = h.Get<Pipeline>();
        await using (var db = h.Db())
        {
            // Someone sets Approved directly in the DB (no ApprovedAtUtc) and even injects queue rows.
            var a1 = await db.Clips.SingleAsync(c => c.HighlightId == "a1");
            var c1 = await db.Clips.SingleAsync(c => c.HighlightId == "c1");
            a1.Status = ClipStatus.Approved;
            c1.Status = ClipStatus.Approved;
            db.Posts.Add(new PlatformPost { ClipId = a1.Id, Platform = Platform.TikTok, Status = PostStatus.Queued });
            db.Posts.Add(new PlatformPost { ClipId = c1.Id, Platform = Platform.TikTok, Status = PostStatus.Scheduled, ScheduledUtc = h.Time.Utc.AddMinutes(-1), SlotKey = "x" });
            await db.SaveChangesAsync();
        }

        // Publisher first: the already-"scheduled" row must be refused right before upload.
        Assert.Equal(0, await p.PublishDueAsync());
        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(0, await p.ScheduleDueAsync());
        Assert.Equal(0, await p.PublishDueAsync());

        await using (var db = h.Db())
        {
            Assert.All(await db.Posts.ToListAsync(), x =>
            {
                Assert.Equal(PostStatus.Cancelled, x.Status);
                Assert.Contains("no manual approval", x.LastError);
            });
            Assert.True(await db.Log.CountAsync(l => l.Level == LogLevelKind.Error && l.Message.Contains("Refused")) >= 2);
            var postId = (await db.Posts.FirstAsync()).Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => p.PublishNowAsync(postId));
        }
    }

    [Fact]
    public async Task Approve_then_scheduled_then_dryrun_publish_records_who_and_when()
    {
        await using var h = await HostAsync();
        var p = h.Get<Pipeline>();
        int a1Id;
        await using (var db = h.Db()) a1Id = (await db.Clips.SingleAsync(c => c.HighlightId == "a1")).Id;

        h.Time.Utc = Local(19, 11, 30);
        await p.ApproveManuallyAsync(a1Id, Pipeline.Owner, caption: "Edited by owner", hashtags: "vrlink, #ace");

        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(1, await p.ScheduleDueAsync());
        Assert.Equal(1, await p.PublishDueAsync());

        await using var db2 = h.Db();
        var clip = await db2.Clips.Include(c => c.Posts).SingleAsync(c => c.Id == a1Id);
        Assert.Equal(ClipStatus.Approved, clip.Status);
        Assert.Equal("owner", clip.ApprovedBy);
        Assert.Equal(Local(19, 11, 30), clip.ApprovedAtUtc);
        Assert.Equal("#vrlink #ace", clip.Hashtags);
        var post = Assert.Single(clip.Posts);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Contains("Edited by owner", post.Caption);
        // c1 was never approved → never queued
        Assert.Equal(ClipStatus.AwaitingApproval, (await db2.Clips.SingleAsync(c => c.HighlightId == "c1")).Status);
    }

    [Fact]
    public async Task Rejected_marker_after_approval_blocks_upload()
    {
        await using var h = await HostAsync();
        var p = h.Get<Pipeline>();
        await h.OwnerApprovesAllAwaitingAsync();
        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(1, await p.ScheduleDueAsync());

        await using (var db = h.Db())
        {
            var scheduled = await db.Posts.Include(x => x.Clip).SingleAsync(x => x.Status == PostStatus.Scheduled);
            // Operator vetoes between scheduling and upload (no importer scan in between).
            await File.WriteAllTextAsync(ClipImporter.RejectMarkerPath(scheduled.Clip!.VideoPath), "");
        }
        Assert.Equal(0, await p.PublishDueAsync());

        await using var db2 = h.Db();
        var post = await db2.Posts.SingleAsync(x => x.SlotKey != null);
        Assert.Equal(PostStatus.Cancelled, post.Status);
        Assert.Contains(".rejected", post.LastError);
        Assert.Null(post.PublishedUtc);
    }

    [Fact]
    public async Task Bulk_approve_only_touches_the_selected_clips()
    {
        await using var h = await HostAsync();
        int a1Id;
        await using (var db = h.Db()) a1Id = (await db.Clips.SingleAsync(c => c.HighlightId == "a1")).Id;
        Assert.Equal(1, await h.Get<Pipeline>().ApproveSelectedAsync(new[] { a1Id }));
        await using var db2 = h.Db();
        Assert.Equal(ClipStatus.Approved, (await db2.Clips.SingleAsync(c => c.HighlightId == "a1")).Status);
        Assert.Equal(ClipStatus.AwaitingApproval, (await db2.Clips.SingleAsync(c => c.HighlightId == "c1")).Status);
    }

    [Fact]
    public async Task Migration_sends_previously_auto_approved_clips_back_to_owner_and_keeps_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkpub-tests", "mig2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "publisher.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddPublisherCore(o => { o.DataDir = dir; o.ClipsRoot = dir; }, dbPath);
            await using var sp = services.BuildServiceProvider();
            await using (var db = await sp.GetRequiredService<IDbContextFactory<PublisherDb>>().CreateDbContextAsync())
            {
                // DB as it was before this change: only InitialCreate, one auto-approved clip with a queued post,
                // one published clip, one in old review mode.
                await db.GetService<IMigrator>().MigrateAsync("InitialCreate");
                const string cols = "HighlightId,Type,Score,Round,DurationSec,Width,Height,Hashtags,HeroIsBot,CreatedUtc,ImportedUtc,VideoPath,JsonPath,FileSizeBytes,Status";
                await db.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO Clips (Id,{cols}) VALUES (1,'auto','ace',90,1,20,1080,1920,'',0,'2026-10-19','2026-10-19','a.mp4','a.json',5,'Approved');" +
                    $"INSERT INTO Clips (Id,{cols}) VALUES (2,'posted','ace',90,1,20,1080,1920,'',0,'2026-10-19','2026-10-19','b.mp4','b.json',5,'Approved');" +
                    $"INSERT INTO Clips (Id,{cols}) VALUES (3,'review','ace',90,1,20,1080,1920,'',0,'2026-10-19','2026-10-19','c.mp4','c.json',5,'PendingReview');" +
                    "INSERT INTO Posts (ClipId,Platform,Status,Attempts) VALUES (1,'TikTok','Queued',0);" +
                    "INSERT INTO Posts (ClipId,Platform,Status,Attempts) VALUES (2,'TikTok','Published',1);");
            }
            await ServiceSetup.InitDatabaseAsync(sp); // applies ManualApproval

            await using var db2 = await sp.GetRequiredService<IDbContextFactory<PublisherDb>>().CreateDbContextAsync();
            var clips = await db2.Clips.Include(c => c.Posts).ToDictionaryAsync(c => c.HighlightId);
            Assert.Equal(3, clips.Count);
            Assert.Equal(ClipStatus.AwaitingApproval, clips["auto"].Status);
            Assert.Equal(PostStatus.Cancelled, clips["auto"].Posts.Single().Status);
            Assert.Equal(ClipStatus.Approved, clips["posted"].Status);              // history kept
            Assert.Equal(PostStatus.Published, clips["posted"].Posts.Single().Status);
            Assert.Equal(ClipStatus.AwaitingApproval, clips["review"].Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
