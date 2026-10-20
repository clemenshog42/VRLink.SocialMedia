using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>Fake TikTok API: records requests, answers init/upload/status/creator_info.</summary>
public sealed class FakeTikTokHandler : HttpMessageHandler
{
    public record Req(HttpMethod Method, string Url, string Body, string? ContentRange);
    public List<Req> Requests { get; } = new();
    public string FinalStatus { get; set; } = "SEND_TO_USER_INBOX";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content == null ? "" : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(ct));
        Requests.Add(new(request.Method, url, body, request.Content?.Headers.ContentRange?.ToString()));

        static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
        const string ok = "\"error\":{\"code\":\"ok\",\"message\":\"\",\"log_id\":\"x\"}";
        if (request.Method == HttpMethod.Put && url.StartsWith("https://upload.test/")) return new HttpResponseMessage(HttpStatusCode.Created);
        if (url.EndsWith("/v2/post/publish/inbox/video/init/") || url.EndsWith("/v2/post/publish/video/init/"))
            return Json("{\"data\":{\"publish_id\":\"v_pub_1\",\"upload_url\":\"https://upload.test/u?x=1\"}," + ok + "}");
        if (url.EndsWith("/v2/post/publish/status/fetch/"))
            return Json("{\"data\":{\"status\":\"" + FinalStatus + "\"}," + ok + "}");
        if (url.EndsWith("/v2/post/publish/creator_info/query/"))
            return Json("{\"data\":{\"creator_nickname\":\"VRLink\",\"creator_username\":\"vrlink\",\"privacy_level_options\":[\"PUBLIC_TO_EVERYONE\",\"SELF_ONLY\"],\"max_video_post_duration_sec\":600}," + ok + "}");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public class FollowUpTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static DateTime Local(int d, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, d, h, m, 0), Tz);

    // ---------------------------------------------------------------- 1) TikTok inbox mode
    private static async Task<(TestHost Host, FakeTikTokHandler Api)> RealTikTokHostAsync(TikTokPostMode? mode)
    {
        var api = new FakeTikTokHandler();
        var h = new TestHost(Local(19, 11, 0), s => s.AddHttpClient("tiktok").ConfigurePrimaryHttpMessageHandler(() => api));
        TikTokPublisher.StatusPollInterval = TimeSpan.Zero;
        await h.SettingsAsync(s =>
        {
            s.DryRun = false;               // real TikTokPublisher, fake HTTP
            s.InstagramEnabled = false;
            s.TimeZoneId = Tz.Id;
            if (mode != null) s.TikTokMode = mode.Value;
            s.TikTokConsentGiven = true;
            s.TikTokPrivacyLevel = "SELF_ONLY";
        });
        var acc = h.Get<AccountService>();
        await acc.SaveCredentialsAsync(Platform.TikTok, "key", "secret");
        await acc.SaveConnectionAsync(new AccountConnection
        {
            Platform = Platform.TikTok, AccountId = "open1", AccessExpiresUtc = h.Time.Utc.AddHours(20), RefreshExpiresUtc = h.Time.Utc.AddDays(300),
        }, "tok", "ref");
        h.WriteClip("a1", "ace", 95, mp4Bytes: new byte[1234]);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        return (h, api);
    }

    [Fact]
    public void Inbox_is_the_default_tiktok_mode() => Assert.Equal(TikTokPostMode.Inbox, new RuntimeSettings().TikTokMode);

    [Fact]
    public async Task Inbox_mode_uses_inbox_init_chunked_put_and_status()
    {
        var (h, api) = await RealTikTokHostAsync(mode: null); // default = Inbox
        await using var _ = h;
        var p = h.Get<Pipeline>();
        h.Time.Utc = Local(19, 12, 31);
        Assert.Equal(1, await p.ScheduleDueAsync());
        Assert.Equal(1, await p.PublishDueAsync());

        var init = Assert.Single(api.Requests, r => r.Url.Contains("/init/"));
        Assert.EndsWith("/v2/post/publish/inbox/video/init/", init.Url);
        Assert.DoesNotContain("post_info", init.Body);
        Assert.Contains("\"video_size\":1234", init.Body);
        Assert.Contains("\"total_chunk_count\":1", init.Body);
        var put = Assert.Single(api.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal("bytes 0-1233/1234", put.ContentRange);
        Assert.Contains(api.Requests, r => r.Url.EndsWith("/status/fetch/") && r.Body.Contains("v_pub_1"));
        Assert.DoesNotContain(api.Requests, r => r.Url.Contains("creator_info"));

        await using var db = h.Db();
        var post = await db.Posts.SingleAsync(x => x.Platform == Platform.TikTok);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(TikTokPublisher.InboxPrivacyMarker, post.PrivacyLevel);
        Assert.Equal("v_pub_1", post.ExternalId);
        Assert.Null(post.PostId);
    }

    [Fact]
    public async Task Direct_mode_uses_creator_info_and_direct_init()
    {
        var (h, api) = await RealTikTokHostAsync(TikTokPostMode.Direct);
        await using var _ = h;
        api.FinalStatus = "PUBLISH_COMPLETE";
        h.Time.Utc = Local(19, 12, 31);
        await h.Get<Pipeline>().ScheduleDueAsync();
        Assert.Equal(1, await h.Get<Pipeline>().PublishDueAsync());

        Assert.Contains(api.Requests, r => r.Url.EndsWith("/creator_info/query/"));
        var init = Assert.Single(api.Requests, r => r.Url.Contains("/init/"));
        Assert.EndsWith("/v2/post/publish/video/init/", init.Url);
        Assert.Contains("\"privacy_level\":\"SELF_ONLY\"", init.Body);
    }

    [Fact]
    public async Task Inbox_failure_status_marks_post_failed()
    {
        var (h, api) = await RealTikTokHostAsync(TikTokPostMode.Inbox);
        await using var _ = h;
        api.FinalStatus = "FAILED";
        h.Time.Utc = Local(19, 12, 31);
        await h.Get<Pipeline>().ScheduleDueAsync();
        Assert.Equal(0, await h.Get<Pipeline>().PublishDueAsync());
        await using var db = h.Db();
        Assert.Equal(PostStatus.Failed, (await db.Posts.SingleAsync()).Status);
    }

    // ---------------------------------------------------------------- 2) .rejected marker
    [Fact]
    public async Task Marker_present_at_import_imports_as_rejected()
    {
        await using var h = new TestHost(Local(19, 11, 0));
        var dir = h.WriteClip("a1", "ace", 95);
        h.WriteClip("a2", "ace", 90);
        await File.WriteAllTextAsync(Path.Combine(dir, "a1.rejected"), "");

        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();

        await using var db = h.Db();
        var a1 = await db.Clips.Include(c => c.Posts).SingleAsync(c => c.HighlightId == "a1");
        Assert.Equal(ClipStatus.Rejected, a1.Status);
        Assert.Equal(ClipImporter.OperatorRejectReason, a1.StatusReason);
        Assert.Empty(a1.Posts);
        Assert.Equal(ClipStatus.AwaitingApproval, (await db.Clips.SingleAsync(c => c.HighlightId == "a2")).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Get<Pipeline>().ApproveManuallyAsync(a1.Id)); // marker blocks approval
    }

    [Fact]
    public async Task Marker_written_later_rejects_and_cancels_queue_but_keeps_published()
    {
        await using var h = new TestHost(Local(19, 11, 0));
        await h.SettingsAsync(s => { s.TimeZoneId = Tz.Id; s.InstagramEnabled = false; });
        var dir = h.WriteClip("a1", "ace", 95);
        h.WriteClip("c1", "clutch", 90);
        var imp = h.Get<ClipImporter>();
        var p = h.Get<Pipeline>();
        await imp.ScanAsync(h.ClipsRoot);
        await p.CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        h.Time.Utc = Local(19, 12, 31);
        await p.ScheduleDueAsync();
        await p.PublishDueAsync(); // a1 published (DryRun)

        await File.WriteAllTextAsync(Path.Combine(dir, "a1.rejected"), "");
        await File.WriteAllTextAsync(Path.Combine(dir, "c1.rejected"), "");
        var r = await imp.ScanAsync(h.ClipsRoot);
        Assert.Equal(1, r.MarkedRejected);

        await using var db = h.Db();
        var a1 = await db.Clips.Include(c => c.Posts).SingleAsync(c => c.HighlightId == "a1");
        var c1 = await db.Clips.Include(c => c.Posts).SingleAsync(c => c.HighlightId == "c1");
        Assert.Equal(ClipStatus.Approved, a1.Status);                  // already published: untouched
        Assert.Equal(ClipStatus.Rejected, c1.Status);
        Assert.All(c1.Posts, x => Assert.Equal(PostStatus.Cancelled, x.Status));
    }

    // ---------------------------------------------------------------- 3) heroIsBot
    [Fact]
    public void Bot_hero_rule()
    {
        var c = new Clip { HighlightId = "b", Type = "ace", Score = 99, DurationSec = 20, Width = 1080, Height = 1920, FileSizeBytes = 1000, HeroIsBot = true };
        var d = ApprovalRules.Evaluate(c, null, new RuntimeSettings(), null);
        Assert.Equal(ApprovalOutcome.Reject, d.Outcome);
        Assert.Contains("bot", d.Reason);
        Assert.Equal(ApprovalOutcome.AwaitingApproval, ApprovalRules.Evaluate(c, null, new RuntimeSettings { AllowBotHighlights = true }, null).Outcome);
    }

    [Theory]
    [InlineData(false, ClipStatus.Rejected)]
    [InlineData(true, ClipStatus.AwaitingApproval)]
    public async Task Bot_hero_clips_skipped_unless_allowed(bool allowBots, ClipStatus expected)
    {
        await using var h = new TestHost(Local(19, 11, 0));
        await h.SettingsAsync(s => s.AllowBotHighlights = allowBots);
        h.WriteClip("bot1", "ace", 95, heroIsBot: true);
        h.WriteClip("human1", "ace", 95, heroIsBot: false, mp4Bytes: new byte[] { 7 });
        h.WriteClip("legacy1", "ace", 95, mp4Bytes: new byte[] { 8 }); // field absent = human
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();

        await using var db = h.Db();
        var clips = await db.Clips.ToDictionaryAsync(c => c.HighlightId);
        Assert.True(clips["bot1"].HeroIsBot);
        Assert.Equal(expected, clips["bot1"].Status);
        Assert.Equal(ClipStatus.AwaitingApproval, clips["human1"].Status);
        Assert.Equal(ClipStatus.AwaitingApproval, clips["legacy1"].Status);
    }
}
