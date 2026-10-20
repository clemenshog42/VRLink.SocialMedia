using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Publishers;

public record TikTokCreatorInfo(
    string? Nickname, string? Username, string? AvatarUrl, List<string> PrivacyLevelOptions,
    bool CommentDisabled, bool DuetDisabled, bool StitchDisabled, int MaxVideoPostDurationSec);

/// <summary>
/// TikTok Content Posting API, Direct Post with FILE_UPLOAD.
/// Docs checked 2026-10-19:
///  - https://developers.tiktok.com/doc/content-posting-api-reference-direct-post
///  - https://developers.tiktok.com/doc/content-posting-api-reference-query-creator-info
///  - https://developers.tiktok.com/doc/content-posting-api-media-transfer-guide
///  - https://developers.tiktok.com/doc/content-posting-api-reference-get-video-status
///  - https://developers.tiktok.com/doc/tiktok-api-v2-video-query
/// Rate limits: init 6/min, creator_info 20/min, status 30/min per user token.
/// Unaudited clients can only post SELF_ONLY (error unaudited_client_can_only_post_to_private_accounts).
/// </summary>
public class TikTokPublisher(IHttpClientFactory http, AccountService accounts, ILogger<TikTokPublisher> logger) : IPlatformPublisher
{
    public const string ApiBase = "https://open.tiktokapis.com";
    public const long MinChunk = 5L * 1024 * 1024;
    public const long MaxChunk = 64L * 1024 * 1024;
    public const long DefaultChunk = 10L * 1024 * 1024;

    public Platform Platform => Platform.TikTok;
    public string Name => "TikTok";

    public static TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public static TimeSpan StatusPollTimeout { get; set; } = TimeSpan.FromMinutes(10);

    private HttpClient Client() => http.CreateClient("tiktok");

    private async Task<string> TokenAsync(CancellationToken ct) =>
        await accounts.GetAccessTokenAsync(Platform.TikTok, ct)
        ?? throw new PlatformApiException("TikTok not connected or access token expired (TokenRefresher will retry)", retryable: true);

    private static HttpRequestMessage Json(HttpMethod m, string path, string token, object? body)
    {
        var req = new HttpRequestMessage(m, ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body ?? new { });
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=UTF-8");
        return req;
    }

    /// <summary>TikTok returns HTTP 200 with error.code != "ok" for some failures; map both.</summary>
    private static async Task<JsonNode> SendAsync(HttpClient c, HttpRequestMessage req, string what, CancellationToken ct)
    {
        using var resp = await c.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? node = null;
        try { node = JsonNode.Parse(body); } catch { }
        var code = node?["error"]?["code"]?.GetValue<string>();
        if (!resp.IsSuccessStatusCode || (code != null && code != "ok"))
        {
            var msg = node?["error"]?["message"]?.GetValue<string>() ?? ApiJson.Trunc(body);
            var retry = ApiJson.IsRetryable(resp.StatusCode) || code == "rate_limit_exceeded";
            throw new PlatformApiException($"TikTok {what}: HTTP {(int)resp.StatusCode} {code}: {msg}", retry, (int)resp.StatusCode);
        }
        return node ?? new JsonObject();
    }

    public async Task<TikTokCreatorInfo> QueryCreatorInfoAsync(CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        var n = await SendAsync(Client(), Json(HttpMethod.Post, "/v2/post/publish/creator_info/query/", token, null), "creator_info", ct);
        var d = n["data"]!;
        return new TikTokCreatorInfo(
            d["creator_nickname"]?.GetValue<string>(),
            d["creator_username"]?.GetValue<string>(),
            d["creator_avatar_url"]?.GetValue<string>(),
            d["privacy_level_options"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? new(),
            d["comment_disabled"]?.GetValue<bool>() ?? false,
            d["duet_disabled"]?.GetValue<bool>() ?? false,
            d["stitch_disabled"]?.GetValue<bool>() ?? false,
            d["max_video_post_duration_sec"]?.GetValue<int>() ?? 0);
    }

    /// <summary>Chunk plan per the Media Transfer Guide: count = floor(size/chunk), last chunk takes the remainder.</summary>
    public static (long ChunkSize, int ChunkCount) PlanChunks(long fileSize, long preferredChunk = DefaultChunk)
    {
        if (fileSize <= 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (fileSize < MinChunk) return (fileSize, 1);               // < 5 MB: whole file, chunk_size = size
        var chunk = Math.Clamp(preferredChunk, MinChunk, MaxChunk);
        var count = (int)Math.Max(1, fileSize / chunk);
        return (chunk, count);
    }

    /// <summary>Picks the privacy level: SELF_ONLY until audited; otherwise the setting, if offered by creator_info.</summary>
    public static string ChoosePrivacy(RuntimeSettings s, IReadOnlyList<string> options)
    {
        var wanted = s.TikTokAppAudited ? s.TikTokPrivacyLevel : "SELF_ONLY";
        if (string.IsNullOrWhiteSpace(wanted))
            throw new PlatformApiException("TikTok privacy level not selected on the Accounts page", retryable: false);
        if (options.Count == 0 || options.Contains(wanted)) return wanted;
        throw new PlatformApiException($"TikTok privacy level {wanted} not offered by creator_info ({string.Join(",", options)})", retryable: false);
    }

    public const string InboxPrivacyMarker = "INBOX";

    public Task<PublishResult> PublishAsync(PublishRequest r, CancellationToken ct) =>
        r.Settings.TikTokMode == TikTokPostMode.Inbox ? PublishToInboxAsync(r, ct) : DirectPostAsync(r, ct);

    /// <summary>
    /// Upload mode ("Send to TikTok inbox"): POST /v2/post/publish/inbox/video/init/ (scope video.upload, source_info only,
    /// no post_info → caption/privacy are set by the creator in the TikTok app), chunked PUT, then status/fetch until
    /// SEND_TO_USER_INBOX. Docs checked 2026-10-19: https://developers.tiktok.com/doc/content-posting-api-reference-upload-video
    /// Limit: at most 5 pending shares per 24 h (403 spam_risk_too_many_pending_share).
    /// </summary>
    public async Task<PublishResult> PublishToInboxAsync(PublishRequest r, CancellationToken ct)
    {
        string? publishId = null;
        try
        {
            var size = new FileInfo(r.Clip.VideoPath).Length;
            var (chunk, count) = PlanChunks(size);
            var token = await TokenAsync(ct);
            var initBody = new { source_info = new { source = "FILE_UPLOAD", video_size = size, chunk_size = chunk, total_chunk_count = count } };
            var init = await SendAsync(Client(), Json(HttpMethod.Post, "/v2/post/publish/inbox/video/init/", token, initBody), "inbox/video/init", ct);
            publishId = init["data"]?["publish_id"]?.GetValue<string>() ?? throw new PlatformApiException("TikTok inbox init: no publish_id", true);
            var uploadUrl = init["data"]?["upload_url"]?.GetValue<string>() ?? throw new PlatformApiException("TikTok inbox init: no upload_url", true);

            await UploadChunksAsync(uploadUrl, r.Clip.VideoPath, size, chunk, count, ct);

            var deadline = DateTime.UtcNow + StatusPollTimeout;
            while (true)
            {
                await Task.Delay(StatusPollInterval, ct);
                var st = await SendAsync(Client(), Json(HttpMethod.Post, "/v2/post/publish/status/fetch/", token, new { publish_id = publishId }), "status/fetch", ct);
                var status = st["data"]?["status"]?.GetValue<string>();
                if (status is "SEND_TO_USER_INBOX" or "PUBLISH_COMPLETE")
                    // In the inbox: the owner opens the TikTok notification, pastes the caption (Queue page) and posts.
                    // No post id is returned here → no stats for inbox posts. VERIFY: whether status later turns PUBLISH_COMPLETE with an id.
                    return new PublishResult(true, false, publishId, null, null, InboxPrivacyMarker,
                        Error: "sent to TikTok inbox: open the TikTok app notification and finish the post (caption on the Queue/Clips page)");
                if (status == "FAILED")
                    return PublishResult.Fail("TikTok inbox upload failed: " + st["data"]?["fail_reason"], retryable: false, publishId);
                if (DateTime.UtcNow > deadline)
                    return PublishResult.Fail($"TikTok inbox upload still {status} after timeout", retryable: true, publishId);
            }
        }
        catch (PlatformApiException ex) { return PublishResult.Fail(ex.Message, ex.Retryable, publishId); }
        catch (HttpRequestException ex) { return PublishResult.Fail("TikTok network: " + ex.Message, true, publishId); }
    }

    /// <summary>Direct Post (needs the TikTok audit for anything but SELF_ONLY).</summary>
    public async Task<PublishResult> DirectPostAsync(PublishRequest r, CancellationToken ct)
    {
        string? publishId = null;
        try
        {
            if (!r.Settings.TikTokConsentGiven)
                return PublishResult.Fail("TikTok posting consent not confirmed on the Accounts page (TikTok UX guideline)", retryable: false);

            var info = await QueryCreatorInfoAsync(ct);   // must be fetched before each post (UX guideline)
            var privacy = ChoosePrivacy(r.Settings, info.PrivacyLevelOptions);
            if (info.MaxVideoPostDurationSec > 0 && r.Clip.DurationSec > info.MaxVideoPostDurationSec)
                return PublishResult.Fail($"clip {r.Clip.DurationSec:0.#}s longer than creator max {info.MaxVideoPostDurationSec}s", retryable: false);

            var size = new FileInfo(r.Clip.VideoPath).Length;
            var (chunk, count) = PlanChunks(size);
            var token = await TokenAsync(ct);
            var initBody = new
            {
                post_info = new
                {
                    title = r.Post.Caption ?? "",
                    privacy_level = privacy,
                    // Respect creator settings: if the creator disabled an interaction it must stay disabled.
                    disable_comment = info.CommentDisabled || !r.Settings.TikTokAllowComments,
                    disable_duet = info.DuetDisabled || !r.Settings.TikTokAllowDuet,
                    disable_stitch = info.StitchDisabled || !r.Settings.TikTokAllowStitch,
                    video_cover_timestamp_ms = 1000,
                    brand_content_toggle = false,
                    brand_organic_toggle = false, // VERIFY: owner decides whether VRLink clips need the "promotional content" disclosure
                },
                source_info = new { source = "FILE_UPLOAD", video_size = size, chunk_size = chunk, total_chunk_count = count },
            };
            var init = await SendAsync(Client(), Json(HttpMethod.Post, "/v2/post/publish/video/init/", token, initBody), "video/init", ct);
            publishId = init["data"]?["publish_id"]?.GetValue<string>() ?? throw new PlatformApiException("TikTok init: no publish_id", true);
            var uploadUrl = init["data"]?["upload_url"]?.GetValue<string>() ?? throw new PlatformApiException("TikTok init: no upload_url", true);

            await UploadChunksAsync(uploadUrl, r.Clip.VideoPath, size, chunk, count, ct);

            var deadline = DateTime.UtcNow + StatusPollTimeout;
            while (true)
            {
                await Task.Delay(StatusPollInterval, ct);
                var st = await SendAsync(Client(), Json(HttpMethod.Post, "/v2/post/publish/status/fetch/", token, new { publish_id = publishId }), "status/fetch", ct);
                var status = st["data"]?["status"]?.GetValue<string>();
                if (status == "PUBLISH_COMPLETE")
                {
                    // Only returned for public posts that passed moderation. SELF_ONLY posts have no id → no stats.
                    var ids = st["data"]?["publicaly_available_post_id"]?.AsArray();
                    var postId = ids is { Count: > 0 } ? ids[0]!.ToString() : null;
                    var url = postId != null && info.Username != null ? $"https://www.tiktok.com/@{info.Username}/video/{postId}" : null;
                    return new PublishResult(true, false, publishId, postId, url, privacy);
                }
                if (status == "FAILED")
                    return PublishResult.Fail("TikTok publish failed: " + st["data"]?["fail_reason"], retryable: false, publishId);
                if (DateTime.UtcNow > deadline)
                    // Upload done but moderation still running: treat as published-pending; stats poller will retry the id later.
                    return new PublishResult(true, false, publishId, null, null, privacy, Error: $"status still {status} after timeout");
            }
        }
        catch (PlatformApiException ex) { return PublishResult.Fail(ex.Message, ex.Retryable, publishId); }
        catch (HttpRequestException ex) { return PublishResult.Fail("TikTok network: " + ex.Message, true, publishId); }
    }

    private async Task UploadChunksAsync(string uploadUrl, string path, long size, long chunk, int count, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var c = Client();
        for (int i = 0; i < count; i++)
        {
            long first = i * chunk;
            long last = i == count - 1 ? size - 1 : first + chunk - 1;   // last chunk absorbs the remainder
            var len = (int)(last - first + 1);
            var buf = new byte[len];
            fs.Position = first;
            await fs.ReadExactlyAsync(buf, ct);
            for (int attempt = 1; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buf) };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                req.Content.Headers.ContentLength = len;
                req.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, size);
                using var resp = await c.SendAsync(req, ct);
                if (resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.PartialContent or HttpStatusCode.OK) break;
                if ((int)resp.StatusCode >= 500 && attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); continue; }
                throw new PlatformApiException($"TikTok chunk {i + 1}/{count} upload: HTTP {(int)resp.StatusCode}", (int)resp.StatusCode >= 500);
            }
            logger.LogInformation("TikTok upload chunk {I}/{N} ok", i + 1, count);
        }
    }

    public async Task<IReadOnlyList<PostStats>> GetStatsAsync(IReadOnlyList<PlatformPost> posts, RuntimeSettings settings, CancellationToken ct)
    {
        var result = new List<PostStats>();
        var withId = posts.Where(p => p.PostId != null).ToList();
        if (withId.Count == 0) return result;
        var token = await TokenAsync(ct);
        foreach (var batch in withId.Chunk(20)) // max 20 ids per request
        {
            var req = Json(HttpMethod.Post, "/v2/video/query/?fields=id,view_count,like_count,comment_count,share_count,share_url", token,
                new { filters = new { video_ids = batch.Select(p => p.PostId).ToArray() } });
            var n = await SendAsync(Client(), req, "video/query", ct);
            // VERIFY: response list is data.videos (per v2 video/list docs)
            foreach (var v in n["data"]?["videos"]?.AsArray() ?? new JsonArray())
            {
                var id = v?["id"]?.ToString();
                var post = batch.FirstOrDefault(p => p.PostId == id);
                if (post == null) continue;
                result.Add(new PostStats(post.Id, ApiJson.Long(v!["view_count"]), ApiJson.Long(v["like_count"]),
                    ApiJson.Long(v["comment_count"]), ApiJson.Long(v["share_count"]), 0, 0, v["share_url"]?.GetValue<string>()));
            }
        }
        return result;
    }
}
