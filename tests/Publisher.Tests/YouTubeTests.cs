using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Oauth;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>Fake Google endpoints (OAuth token, YouTube resumable upload, videos.list, channels.list). Records every request; no network.</summary>
public sealed class FakeYouTubeHandler : HttpMessageHandler
{
    public record Req(HttpMethod Method, string Url, string? Auth, string Body, byte[] Data, long? RangeFrom, long? RangeTo, long? RangeTotal, bool UnsatisfiedRange,
                      string? UploadLength, string? UploadType);
    public List<Req> Requests { get; } = new();
    public const string Session = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=SESSION1";
    public string VideoId { get; set; } = "vid123";
    public long Received { get; set; }
    public bool Complete { get; set; }
    /// <summary>Per data PUT: "ok" (default), "503:N", "drop:N", "308:N" (N = total bytes the server holds afterwards).</summary>
    public Queue<string> DataScript { get; } = new();
    public bool AlwaysFail503 { get; set; }
    public long FailKeep { get; set; } = 300;
    public string? SessionStartError { get; set; }
    public int Unauthorized401Left { get; set; }
    public string? TokenErrorBody { get; set; }
    public string GrantedScopes { get; set; } = OAuthService.YouTubeScopes;
    public Dictionary<string, (string Views, string? Likes, string Comments)> Stats { get; } = new();

    public IEnumerable<Req> Puts => Requests.Where(r => r.Method == HttpMethod.Put);
    public IEnumerable<Req> SessionPosts => Requests.Where(r => r.Method == HttpMethod.Post && r.Url.StartsWith(YouTubePublisher.UploadEndpoint));
    public IEnumerable<Req> TokenCalls => Requests.Where(r => r.Url == OAuthService.GoogleTokenUrl);

    private static HttpResponseMessage Json(HttpStatusCode code, string s) => new(code) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    private string VideoJson => "{\"kind\":\"youtube#video\",\"id\":\"" + VideoId + "\",\"status\":{\"uploadStatus\":\"uploaded\",\"privacyStatus\":\"private\"}}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var data = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var cr = request.Content?.Headers.ContentRange;
        var isText = request.Content?.Headers.ContentType?.MediaType is "application/json" or "application/x-www-form-urlencoded";
        Requests.Add(new(request.Method, url, request.Headers.Authorization?.ToString(), isText ? Encoding.UTF8.GetString(data) : "", data,
            cr?.From, cr?.To, cr?.Length, cr != null && !cr.HasRange,
            request.Headers.TryGetValues("X-Upload-Content-Length", out var l) ? l.First() : null,
            request.Headers.TryGetValues("X-Upload-Content-Type", out var t) ? t.First() : null));

        if (url == OAuthService.GoogleTokenUrl)
        {
            if (TokenErrorBody != null) return Json(HttpStatusCode.BadRequest, TokenErrorBody);
            var form = Encoding.UTF8.GetString(data);
            var first = form.Contains("grant_type=authorization_code");
            return Json(HttpStatusCode.OK, "{\"access_token\":\"new-access\",\"expires_in\":3599,\"scope\":\"" + GrantedScopes + "\",\"token_type\":\"Bearer\"" +
                                           (first ? ",\"refresh_token\":\"new-refresh\"" : "") + "}");
        }
        if (url.StartsWith(YouTubePublisher.UploadEndpoint) && request.Method == HttpMethod.Post)
        {
            if (Unauthorized401Left > 0 && request.Headers.Authorization?.Parameter == "tok-1")
            {
                Unauthorized401Left--;
                return Json(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}");
            }
            if (SessionStartError != null) return Json(HttpStatusCode.Forbidden, SessionStartError);
            var ok = new HttpResponseMessage(HttpStatusCode.OK);
            ok.Headers.Location = new Uri(Session);
            return ok;
        }
        if (url == Session && request.Method == HttpMethod.Put)
        {
            if (cr is { HasRange: false })   // status query: Content-Range: bytes */total
            {
                if (Complete) return Json(HttpStatusCode.Created, VideoJson);
                var r308 = new HttpResponseMessage((HttpStatusCode)308);
                if (Received > 0) r308.Headers.TryAddWithoutValidation("Range", $"bytes=0-{Received - 1}");
                return r308;
            }
            long from = cr?.From ?? 0;
            var step = AlwaysFail503 ? $"503:{Math.Max(FailKeep, Received)}" : DataScript.Count > 0 ? DataScript.Dequeue() : "ok";
            if (step != "ok")
            {
                var parts = step.Split(':');
                Received = long.Parse(parts[1]);
                if (parts[0] == "503") return Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":503,\"message\":\"backend error\"}}");
                if (parts[0] == "drop") throw new HttpRequestException("connection reset by peer");
                var r308 = new HttpResponseMessage((HttpStatusCode)308);
                r308.Headers.TryAddWithoutValidation("Range", $"bytes=0-{Received - 1}");
                return r308;
            }
            if (from != Received) return Json(HttpStatusCode.BadRequest, $"{{\"error\":{{\"code\":400,\"message\":\"unexpected offset {from}, have {Received}\"}}}}");
            Received += data.Length;
            Complete = true;
            return Json(HttpStatusCode.Created, VideoJson);
        }
        if (url.StartsWith(YouTubePublisher.VideosListUrl) && request.Method == HttpMethod.Get)
        {
            var ids = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["id"]!.Split(',');
            var items = new JsonArray();
            foreach (var id in ids.Where(Stats.ContainsKey))
            {
                var s = Stats[id];
                var st = new JsonObject { ["viewCount"] = s.Views, ["commentCount"] = s.Comments, ["favoriteCount"] = "0" };
                if (s.Likes != null) st["likeCount"] = s.Likes;
                items.Add(new JsonObject { ["kind"] = "youtube#video", ["id"] = id, ["statistics"] = st });
            }
            return Json(HttpStatusCode.OK, new JsonObject { ["kind"] = "youtube#videoListResponse", ["items"] = items }.ToJsonString());
        }
        if (url.StartsWith("https://www.googleapis.com/youtube/v3/channels"))
            return Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"UC123\",\"snippet\":{\"title\":\"VRLink Arena\",\"customUrl\":\"@vrlinkarena\"}}]}");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>YouTube Shorts: publisher against a fake Google API (no network), through the real pipeline gates.</summary>
public class YouTubeTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static DateTime Local(int d, int h, int m) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, d, h, m, 0), Tz);
    private static readonly DateTime SlotTime = Local(19, 13, 1);
    private static readonly byte[] Video = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7 + 3)).ToArray();

    /// <summary>accessLeft = lifetime of the stored access token counted from the 13:00 slot time (when RunSlotAsync publishes).</summary>
    private static async Task<(TestHost Host, FakeYouTubeHandler Api)> HostAsync(Action<RuntimeSettings>? edit = null, int width = 1080, int height = 1920,
        double dur = 20, TimeSpan? accessLeft = null)
    {
        var api = new FakeYouTubeHandler();
        var h = new TestHost(Local(19, 11, 0), s => s.AddHttpClient("youtube").ConfigurePrimaryHttpMessageHandler(() => api));
        YouTubePublisher.RetryBaseDelay = TimeSpan.Zero;
        YouTubePublisher.MaxResumeAttempts = 6;
        await h.SettingsAsync(s =>
        {
            s.DryRun = false;               // real YouTubePublisher, fake HTTP
            s.TikTokEnabled = false; s.InstagramEnabled = false; s.YouTubeEnabled = true;
            s.TimeZoneId = Tz.Id;
            s.UseLearnedSlots = false;
            edit?.Invoke(s);
        });
        var acc = h.Get<AccountService>();
        await acc.SaveCredentialsAsync(Platform.YouTube, "cid", "csecret");
        await acc.SaveConnectionAsync(new AccountConnection
        {
            Platform = Platform.YouTube, AccountId = "UC-old", AccessExpiresUtc = SlotTime.Add(accessLeft ?? TimeSpan.FromMinutes(50)),
        }, "tok-1", "refresh-1");
        h.WriteClip("y1", "ace", 95, width, height, dur, mp4Bytes: Video);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        return (h, api);
    }

    /// <summary>Slot 13:00 + 1 min: schedules and publishes (returns the number published).</summary>
    private static async Task<int> RunSlotAsync(TestHost h)
    {
        h.Time.Utc = SlotTime;
        var p = h.Get<Pipeline>();
        await p.ScheduleDueAsync();
        return await p.PublishDueAsync();
    }

    private static async Task<PlatformPost> PostAsync(TestHost h)
    {
        await using var db = h.Db();
        return await db.Posts.Include(x => x.Clip).SingleAsync(x => x.Platform == Platform.YouTube);
    }

    // ------------------------------------------------------------------ defaults + title/description rules

    [Fact]
    public void Defaults_are_off_private_with_two_slots()
    {
        var s = new RuntimeSettings();
        Assert.False(s.YouTubeEnabled);                    // nothing goes to YouTube until the owner switches it on
        Assert.Equal("private", s.YouTubePrivacy);
        Assert.Equal(new[] { "13:00", "18:30" }, s.YouTubeSlots);
        Assert.Equal("private", YouTubePublisher.ChoosePrivacy(new RuntimeSettings { YouTubePrivacy = "" }));
        Assert.Equal("private", YouTubePublisher.ChoosePrivacy(new RuntimeSettings { YouTubePrivacy = "everyone" }));
        Assert.Equal("unlisted", YouTubePublisher.ChoosePrivacy(new RuntimeSettings { YouTubePrivacy = " Unlisted " }));
        Assert.Equal("public", YouTubePublisher.ChoosePrivacy(new RuntimeSettings { YouTubePrivacy = "public" }));
        Assert.Equal(new[] { "13:00", "18:30" }, SlotLearning.Best(Platform.YouTube, []).Slots);
    }

    [Fact]
    public void Title_gets_the_Shorts_tag_and_drops_hashtags_and_brackets()
    {
        Assert.Equal("Real room. Real legs. #Shorts", YouTubePublisher.BuildTitle("Real room. Real legs.\n\n#VRLink #VR #freeroamvr"));
        Assert.Equal("ACE now #Shorts", YouTubePublisher.BuildTitle("ACE <now> #VRLink"));
        Assert.Equal("VRLink highlight #Shorts", YouTubePublisher.BuildTitle("#VRLink #VR"));
        Assert.Equal("VRLink highlight #Shorts", YouTubePublisher.BuildTitle(null));
        var already = YouTubePublisher.BuildTitle("Already #shorts tagged");
        Assert.Equal("Already tagged #Shorts", already);          // exactly one tag
    }

    [Fact]
    public void Long_titles_are_cut_to_100_chars_at_a_word_and_still_end_with_Shorts()
    {
        var words = string.Join(' ', Enumerable.Range(1, 60).Select(i => "word" + i));
        var t = YouTubePublisher.BuildTitle(words + "\n\n#VRLink");
        Assert.InRange(t.Length, 90, 100);
        Assert.EndsWith(" #Shorts", t);
        Assert.Contains("… #Shorts", t);
        Assert.Matches(@"word\d+… #Shorts$", t);               // cut at a word boundary, not inside "wordNN"
        // exactly at the limit: 92 chars + " #Shorts" = 100, no ellipsis
        var fits = new string('a', 92);
        Assert.Equal(fits + " #Shorts", YouTubePublisher.BuildTitle(fits));
        var over = new string('a', 93);                                // one too long, no spaces: hard cut
        var t2 = YouTubePublisher.BuildTitle(over);
        Assert.True(t2.Length <= 100); Assert.EndsWith("… #Shorts", t2);
        // an emoji (surrogate pair) on the cut point is never split
        var t3 = YouTubePublisher.BuildTitle(new string('a', 89) + "\U0001F525\U0001F525\U0001F525 tail");
        Assert.True(t3.Length <= 100);
        for (int i = 0; i < t3.Length; i++)
        {
            if (char.IsHighSurrogate(t3[i])) Assert.True(char.IsLowSurrogate(t3[i + 1]));
            if (char.IsLowSurrogate(t3[i])) Assert.True(i > 0 && char.IsHighSurrogate(t3[i - 1]));
        }
    }

    [Fact]
    public void Description_has_caption_Shorts_hashtag_booking_link_and_at_most_5000_bytes()
    {
        var d = YouTubePublisher.BuildDescription("Ace in round 7.\n\n#VRLink #VR", "https://vrlink.example/book");
        Assert.Equal("Ace in round 7.\n\n#VRLink #VR #Shorts\n\nBook your squad: https://vrlink.example/book", d);
        Assert.Equal("Ace.\n\n#Shorts", YouTubePublisher.BuildDescription("Ace.", ""));
        Assert.Equal("#Shorts", YouTubePublisher.BuildDescription(null, "not a url"));                  // invalid links are dropped
        Assert.DoesNotContain("<", YouTubePublisher.BuildDescription("a <script> b", null));
        var big = YouTubePublisher.BuildDescription(string.Concat(Enumerable.Repeat("ä\U0001F525 groß ", 800)) + "\n#VRLink", "https://vrlink.example/book");
        Assert.True(Encoding.UTF8.GetByteCount(big) <= 5000, Encoding.UTF8.GetByteCount(big).ToString());
        Assert.EndsWith(" #Shorts\n\nBook your squad: https://vrlink.example/book", big);             // tag and link survive the cut
    }

    [Fact]
    public void Metadata_uses_the_documented_fields()
    {
        var clip = new Clip { Type = "ace", Width = 1080, Height = 1920, DurationSec = 20 };
        var post = new PlatformPost { Caption = "Hook. Text\n\n#VRLink" };
        var m = YouTubePublisher.BuildMetadata(new PublishRequest(clip, post, new RuntimeSettings { YouTubeBookingLink = "https://vrlink.example/book" }));
        Assert.Equal("Hook. Text #Shorts", m["snippet"]!["title"]!.GetValue<string>());
        Assert.Equal("20", m["snippet"]!["categoryId"]!.GetValue<string>());
        Assert.Contains("https://vrlink.example/book", m["snippet"]!["description"]!.GetValue<string>());
        Assert.Equal("private", m["status"]!["privacyStatus"]!.GetValue<string>());
        Assert.False(m["status"]!["selfDeclaredMadeForKids"]!.GetValue<bool>());
        Assert.False(m["status"]!["containsSyntheticMedia"]!.GetValue<bool>());
    }

    [Fact]
    public void Clip_validation_needs_vertical_and_at_most_180_seconds()
    {
        Assert.Null(YouTubePublisher.ValidateClip(new Clip { Width = 1080, Height = 1920, DurationSec = 180 }));
        Assert.Null(YouTubePublisher.ValidateClip(new Clip { Width = 720, Height = 1280, DurationSec = 5 }));
        Assert.Contains("not vertical", YouTubePublisher.ValidateClip(new Clip { Width = 1920, Height = 1080, DurationSec = 20 }));
        Assert.Contains("not vertical", YouTubePublisher.ValidateClip(new Clip { Width = 1080, Height = 1080, DurationSec = 20 }));
        Assert.Contains("180", YouTubePublisher.ValidateClip(new Clip { Width = 1080, Height = 1920, DurationSec = 180.5 }));
        Assert.Contains("unknown", YouTubePublisher.ValidateClip(new Clip { Width = 0, Height = 0, DurationSec = 20 }));
    }

    // ------------------------------------------------------------------ resumable upload

    [Fact]
    public async Task Resumable_upload_happy_path_through_schedule_and_publish()
    {
        var (h, api) = await HostAsync(s => s.YouTubeBookingLink = "https://vrlink.example/book");
        await using var _ = h;
        Assert.Equal(1, await RunSlotAsync(h));

        var start = Assert.Single(api.SessionPosts);
        Assert.Equal("https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status", start.Url);
        Assert.Equal("Bearer tok-1", start.Auth);
        Assert.Equal("1000", start.UploadLength);
        Assert.Equal("video/mp4", start.UploadType);
        var meta = JsonNode.Parse(start.Body)!;
        Assert.EndsWith(" #Shorts", meta["snippet"]!["title"]!.GetValue<string>());
        Assert.True(meta["snippet"]!["title"]!.GetValue<string>().Length <= 100);
        Assert.Equal("20", meta["snippet"]!["categoryId"]!.GetValue<string>());
        Assert.Contains("#Shorts", meta["snippet"]!["description"]!.GetValue<string>());
        Assert.Contains("https://vrlink.example/book", meta["snippet"]!["description"]!.GetValue<string>());
        Assert.Equal("private", meta["status"]!["privacyStatus"]!.GetValue<string>());
        Assert.False(meta["status"]!["selfDeclaredMadeForKids"]!.GetValue<bool>());
        Assert.False(meta["status"]!["containsSyntheticMedia"]!.GetValue<bool>());

        var put = Assert.Single(api.Puts);                   // one PUT with the whole file, no Content-Range
        Assert.Equal(Video, put.Data);
        Assert.Null(put.RangeFrom);
        Assert.Equal("Bearer tok-1", put.Auth);

        var post = await PostAsync(h);
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("vid123", post.PostId);
        Assert.Equal("vid123", post.ExternalId);
        Assert.Equal("private", post.PrivacyLevel);
        Assert.Equal("https://www.youtube.com/shorts/vid123", post.PostUrl);
        Assert.Equal(SlotTime, post.PublishedUtc);
    }

    [Fact]
    public async Task Privacy_setting_unlisted_is_sent()
    {
        var (h, api) = await HostAsync(s => s.YouTubePrivacy = "unlisted");
        await using var _ = h;
        Assert.Equal(1, await RunSlotAsync(h));
        Assert.Equal("unlisted", JsonNode.Parse(api.SessionPosts.Single().Body)!["status"]!["privacyStatus"]!.GetValue<string>());
        Assert.Equal("unlisted", (await PostAsync(h)).PrivacyLevel);
    }

    [Theory]
    [InlineData("503:400", 400)]      // server error after 400 bytes arrived
    [InlineData("drop:250", 250)]     // connection reset after 250 bytes
    [InlineData("503:0", 0)]          // nothing arrived: status says no Range, start from 0 again
    [InlineData("308:600", 600)]      // 308 straight on the data PUT
    public async Task Upload_resumes_from_the_byte_the_server_has(string script, int expectedOffset)
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        api.DataScript.Enqueue(script);
        Assert.Equal(1, await RunSlotAsync(h));

        Assert.Single(api.SessionPosts);                                     // the same session, no second upload
        var puts = api.Puts.ToList();
        Assert.Equal(script.StartsWith("308") ? 2 : 3, puts.Count);          // data, status query, rest (a 308 needs no query)
        var query = puts.FirstOrDefault(p => p.UnsatisfiedRange);
        if (!script.StartsWith("308")) { Assert.NotNull(query); Assert.Equal(1000, query!.RangeTotal); Assert.Empty(query.Data); }
        var last = puts[^1];
        Assert.Equal(Video[expectedOffset..], last.Data);                    // only the missing rest is sent
        if (expectedOffset > 0) { Assert.Equal(expectedOffset, last.RangeFrom); Assert.Equal(999, last.RangeTo); Assert.Equal(1000, last.RangeTotal); }
        else Assert.Null(last.RangeFrom);
        Assert.Equal(PostStatus.Published, (await PostAsync(h)).Status);
    }

    [Fact]
    public async Task Upload_that_already_finished_is_not_sent_again()
    {
        // the 201 got lost on the way back: the status query returns the finished video
        var (h, api) = await HostAsync();
        await using var _ = h;
        api.DataScript.Enqueue("drop:1000");
        api.Complete = true;
        Assert.Equal(1, await RunSlotAsync(h));
        Assert.Equal(2, api.Puts.Count());                                   // data (dropped) + status query, no third PUT
        Assert.Equal("vid123", (await PostAsync(h)).PostId);
    }

    [Fact]
    public async Task Failed_attempt_keeps_the_session_and_the_next_attempt_continues_it()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        YouTubePublisher.MaxResumeAttempts = 2;
        try
        {
            api.AlwaysFail503 = true;
            Assert.Equal(0, await RunSlotAsync(h));
            var failed = await PostAsync(h);
            Assert.Equal(PostStatus.Scheduled, failed.Status);               // retryable: back off, try again
            Assert.Contains("interrupted", failed.LastError);
            Assert.Equal(FakeYouTubeHandler.Session, failed.ExternalId);     // remembered for the next attempt

            api.AlwaysFail503 = false;
            h.Time.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(1, await h.Get<Pipeline>().PublishDueAsync());
            Assert.Single(api.SessionPosts);                                 // still ONE upload session: no duplicate video
            Assert.Equal(Video[300..], api.Puts.Last().Data);
            Assert.Equal("vid123", (await PostAsync(h)).PostId);
        }
        finally { YouTubePublisher.MaxResumeAttempts = 6; }
    }

    [Fact]
    public async Task Quota_error_fails_the_post_with_a_clear_hint()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        api.SessionStartError = "{\"error\":{\"code\":403,\"message\":\"The request cannot be completed because you have exceeded your quota.\",\"errors\":[{\"reason\":\"quotaExceeded\"}]}}";
        Assert.Equal(0, await RunSlotAsync(h));
        var post = await PostAsync(h);
        Assert.Equal(PostStatus.Failed, post.Status);                        // 403 is not retried automatically
        Assert.Contains("quotaExceeded", post.LastError);
        Assert.Contains("midnight Pacific", post.LastError);
    }

    // ------------------------------------------------------------------ tokens

    [Fact]
    public async Task Expired_access_token_is_refreshed_before_the_upload()
    {
        var (h, api) = await HostAsync(accessLeft: TimeSpan.FromMinutes(1));   // < 2 min margin
        await using var _ = h;
        Assert.Equal(1, await RunSlotAsync(h));

        var refresh = Assert.Single(api.TokenCalls);
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=refresh-1", refresh.Body);
        Assert.Contains("client_id=cid", refresh.Body);
        Assert.Contains("client_secret=csecret", refresh.Body);
        Assert.Equal("Bearer new-access", api.SessionPosts.Single().Auth);
        Assert.Equal("Bearer new-access", api.Puts.Single().Auth);

        var acc = h.Get<AccountService>();
        Assert.Equal("new-access", await acc.GetAccessTokenAsync(Platform.YouTube));
        Assert.Equal("refresh-1", await acc.GetRefreshTokenAsync(Platform.YouTube));   // Google sends no new refresh token on refresh: kept
        await using var db = h.Db();
        var conn = await db.Accounts.SingleAsync(a => a.Platform == Platform.YouTube);
        Assert.DoesNotContain("new-access", Encoding.UTF8.GetString(conn.AccessTokenEnc!));   // DPAPI-encrypted at rest
        Assert.DoesNotContain("refresh-1", Encoding.UTF8.GetString(conn.RefreshTokenEnc!));
    }

    [Fact]
    public async Task A_401_triggers_one_refresh_and_a_retry()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        api.Unauthorized401Left = 1;
        Assert.Equal(1, await RunSlotAsync(h));
        Assert.Equal(new[] { "Bearer tok-1", "Bearer new-access" }, api.SessionPosts.Select(r => r.Auth));
        Assert.Single(api.TokenCalls);
    }

    [Fact]
    public async Task Revoked_login_fails_without_retries_and_explains_the_7_day_testing_limit()
    {
        var (h, api) = await HostAsync(accessLeft: TimeSpan.FromMinutes(-5));
        await using var _ = h;
        api.TokenErrorBody = "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}";
        Assert.Equal(0, await RunSlotAsync(h));
        var post = await PostAsync(h);
        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("invalid_grant", post.LastError);
        Assert.Contains("7 days", post.LastError);
        Assert.Empty(api.SessionPosts);
    }

    [Fact]
    public async Task Token_refresher_renews_a_youtube_token_that_is_about_to_expire()
    {
        var (h, api) = await HostAsync(accessLeft: TimeSpan.FromMinutes(20));
        await using var _ = h;
        h.Time.Utc = SlotTime;
        await h.Get<Pipeline>().RefreshTokensAsync();
        Assert.Single(api.TokenCalls);
        Assert.Equal("new-access", await h.Get<AccountService>().GetAccessTokenAsync(Platform.YouTube));
        await h.Get<Pipeline>().RefreshTokensAsync();                        // fresh now: no second call
        Assert.Single(api.TokenCalls);
    }

    [Fact]
    public async Task Connect_flow_uses_offline_consent_pkce_and_stores_the_tokens_encrypted()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        var oauth = h.Get<OAuthService>();
        var url = new Uri(await oauth.YouTubeAuthorizeUrlAsync("http://localhost:5080"));
        Assert.Equal("accounts.google.com", url.Host);
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("http://localhost:5080/oauth/youtube/callback/", q["redirect_uri"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.Equal("consent", q["prompt"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Contains("https://www.googleapis.com/auth/youtube.upload", q["scope"]);
        Assert.Contains("https://www.googleapis.com/auth/youtube.readonly", q["scope"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", q["code_challenge"]!);

        Assert.Equal("ok", await oauth.YouTubeCallbackAsync("the-code", q["state"], null, default));
        var token = Assert.Single(api.TokenCalls);
        Assert.Contains("grant_type=authorization_code", token.Body);
        Assert.Contains("code=the-code", token.Body);
        var verifier = System.Web.HttpUtility.ParseQueryString(token.Body)["code_verifier"]!;
        Assert.Equal(q["code_challenge"], OAuthService.S256Challenge(verifier));       // PKCE verifier matches the challenge sent first

        var acc = h.Get<AccountService>();
        Assert.Equal("new-refresh", await acc.GetRefreshTokenAsync(Platform.YouTube));
        var conn = (await acc.GetConnectionAsync(Platform.YouTube))!;
        Assert.Equal("UC123", conn.AccountId);
        Assert.Equal("VRLink Arena", conn.DisplayName);
        // state is single use
        Assert.StartsWith("Invalid or expired state", await oauth.YouTubeCallbackAsync("the-code", q["state"], null, default));
    }

    [Fact]
    public async Task Connect_without_the_upload_permission_is_refused()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        api.GrantedScopes = OAuthService.YouTubeReadonlyScope;
        var oauth = h.Get<OAuthService>();
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(await oauth.YouTubeAuthorizeUrlAsync("http://localhost:5080")).Query);
        Assert.Contains("upload permission", await oauth.YouTubeCallbackAsync("c", q["state"], null, default));
    }

    // ------------------------------------------------------------------ gates: approval, vertical, 180 s

    [Fact]
    public async Task Unapproved_clip_never_reaches_the_api()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        await using (var db = h.Db())
        {
            // tamper: status says Approved but no manual approval is recorded, and a queue row is already "due"
            var clip = await db.Clips.SingleAsync();
            clip.ApprovedAtUtc = null; clip.ApprovedBy = null;
            var post = await db.Posts.SingleAsync();
            post.Status = PostStatus.Scheduled; post.ScheduledUtc = h.Time.Utc.AddMinutes(-1); post.SlotKey = "x";
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await h.Get<Pipeline>().PublishDueAsync());
        h.Time.Utc = SlotTime;
        Assert.Equal(0, await h.Get<Pipeline>().ScheduleDueAsync());
        Assert.Empty(api.Requests);
        var p = await PostAsync(h);
        Assert.Equal(PostStatus.Cancelled, p.Status);
        Assert.Contains("no manual approval", p.LastError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Get<Pipeline>().PublishNowAsync(p.Id));
    }

    [Fact]
    public async Task Nothing_is_queued_for_youtube_before_the_owner_approves()
    {
        await using var h = new TestHost(Local(19, 11, 0));
        await h.SettingsAsync(s => { s.DryRun = false; s.YouTubeEnabled = true; });
        h.WriteClip("y1", "ace", 95, mp4Bytes: Video);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await using var db = h.Db();
        Assert.Equal(ClipStatus.AwaitingApproval, (await db.Clips.SingleAsync()).Status);
        Assert.Equal(0, await db.Posts.CountAsync());
    }

    [Fact]
    public async Task Publisher_itself_refuses_a_clip_without_manual_approval()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        var pub = h.Get<IEnumerable<IPlatformPublisher>>().OfType<YouTubePublisher>().Single();
        var path = Path.Combine(h.Dir, "x.mp4"); await File.WriteAllBytesAsync(path, Video);
        var clip = new Clip { HighlightId = "z", VideoPath = path, Width = 1080, Height = 1920, DurationSec = 20, Status = ClipStatus.AwaitingApproval };
        var r = await pub.PublishAsync(new PublishRequest(clip, new PlatformPost { Platform = Platform.YouTube }, await h.Get<SettingsStore>().GetAsync()), default);
        Assert.False(r.Success); Assert.False(r.Retryable);
        Assert.Contains("not approved", r.Error);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Horizontal_clip_is_refused_before_any_upload()
    {
        var (h, api) = await HostAsync(s => { s.RequiredWidth = 1920; s.RequiredHeight = 1080; }, width: 1920, height: 1080);
        await using var _ = h;
        Assert.Equal(0, await RunSlotAsync(h));
        var post = await PostAsync(h);
        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("not vertical", post.LastError);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Clip_longer_than_180_seconds_is_refused_before_any_upload()
    {
        var (h, api) = await HostAsync(s => s.MaxDurationSec = 600, dur: 200);
        await using var _ = h;
        Assert.Equal(0, await RunSlotAsync(h));
        var post = await PostAsync(h);
        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("180", post.LastError);
        Assert.Empty(api.Requests);
    }

    // ------------------------------------------------------------------ stats + learning

    [Fact]
    public async Task Stats_are_polled_with_videos_list_and_stored_like_the_other_platforms()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        Assert.Equal(1, await RunSlotAsync(h));
        api.Stats["vid123"] = ("1234", "56", "7");
        h.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await h.Get<Pipeline>().PollStatsAsync());

        var get = Assert.Single(api.Requests, r => r.Method == HttpMethod.Get && r.Url.StartsWith(YouTubePublisher.VideosListUrl));
        Assert.Contains("part=statistics", get.Url);
        Assert.Contains("id=vid123", get.Url);
        Assert.Equal("Bearer tok-1", get.Auth);
        await using var db = h.Db();
        var snap = await db.Stats.SingleAsync();
        Assert.Equal((1234L, 56L, 7L), (snap.Views, snap.Likes, snap.Comments));
        var post = await db.Posts.SingleAsync();
        Assert.Equal(1234, post.LastViews);
        Assert.Equal(56, post.LastLikes);
        Assert.NotNull(post.NextStatsPollUtc);
    }

    [Fact]
    public async Task Stats_parser_handles_batches_hidden_likes_and_unknown_ids()
    {
        var (h, api) = await HostAsync();
        await using var _ = h;
        var posts = Enumerable.Range(1, 120).Select(i => new PlatformPost { Id = i, Platform = Platform.YouTube, PostId = "v" + i }).ToList();
        foreach (var p in posts.Take(100)) api.Stats[p.PostId!] = ((p.Id * 10).ToString(), p.Id == 5 ? null : "3", "1");   // v5: likes hidden; v101-v120 unknown to the API
        var pub = h.Get<IEnumerable<IPlatformPublisher>>().OfType<YouTubePublisher>().Single();
        var stats = await pub.GetStatsAsync(posts, new RuntimeSettings(), default);
        Assert.Equal(3, api.Requests.Count(r => r.Url.StartsWith(YouTubePublisher.VideosListUrl)));    // 50 + 50 + 20 ids
        Assert.Equal(100, stats.Count);
        Assert.Equal(new PostStats(5, 50, 0, 1, 0, 0, 0), stats.Single(s => s.PlatformPostId == 5));
        Assert.Equal(3, stats.Single(s => s.PlatformPostId == 6).Likes);
    }

    [Fact]
    public void Youtube_posts_are_measurable_and_normalised_per_platform_but_private_ones_are_not_learned()
    {
        var pub = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        PlatformPost P(int id, Platform pl, string? privacy, long views48) => new()
        {
            Id = id, Platform = pl, Status = PostStatus.Published, PublishedUtc = pub.AddHours(id), PostId = "p" + id, PrivacyLevel = privacy, Clip = new Clip { Type = "ace" },
            Stats = [new StatSnapshot { TakenUtc = pub.AddHours(id + 50), Views = views48, Likes = views48 / 10 }],
        };
        var posts = new List<PlatformPost>();
        for (int i = 1; i <= 6; i++) posts.Add(P(i, Platform.YouTube, "unlisted", 100 * i));       // small channel
        for (int i = 11; i <= 16; i++) posts.Add(P(i, Platform.TikTok, null, 10000 * (i - 10)));  // big account, same pattern
        posts.Add(P(21, Platform.YouTube, "private", 5));                                         // owner-only views: not an audience signal
        Assert.False(Performance.Learnable(posts[^1]));
        Assert.All(posts.Take(12), p => Assert.True(Performance.Learnable(p)));
        var o = Performance.Outcomes(posts);
        Assert.Equal(6, o.Count(x => x.Platform == Platform.YouTube));
        Assert.DoesNotContain(o, x => x.PostId == 21);
        double Top(Platform pl) => o.Where(x => x.Platform == pl).OrderBy(x => x.PostId).Last().Score;
        Assert.InRange(Math.Abs(Top(Platform.YouTube) - Top(Platform.TikTok)), 0, 0.5);          // 100 vs 10 000 views: same normalised score
    }

    // ------------------------------------------------------------------ DryRun + disabled by default

    [Fact]
    public async Task DryRun_fake_publishes_a_private_youtube_post_without_any_network()
    {
        var api = new FakeYouTubeHandler();
        await using var h = new TestHost(Local(19, 11, 0), s => s.AddHttpClient("youtube").ConfigurePrimaryHttpMessageHandler(() => api));
        await h.SettingsAsync(s => { s.TikTokEnabled = false; s.InstagramEnabled = false; s.YouTubeEnabled = true; s.TimeZoneId = Tz.Id; s.UseLearnedSlots = false; });
        h.WriteClip("y1", "ace", 95);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        Assert.Equal(1, await RunSlotAsync(h));
        var post = await PostAsync(h);
        Assert.Equal("private", post.PrivacyLevel);
        Assert.Contains("youtube.com/shorts/", post.PostUrl);
        Assert.Empty(api.Requests);
        h.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await h.Get<Pipeline>().PollStatsAsync());
    }

    [Fact]
    public async Task Youtube_stays_off_until_enabled()
    {
        await using var h = new TestHost(Local(19, 11, 0));
        h.WriteClip("y1", "ace", 95);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        await h.OwnerApprovesAllAwaitingAsync();
        await using var db = h.Db();
        Assert.DoesNotContain(await db.Posts.ToListAsync(), p => p.Platform == Platform.YouTube);
        Assert.Equal(2, await db.Posts.CountAsync());                       // TikTok + Instagram as before
    }
}
