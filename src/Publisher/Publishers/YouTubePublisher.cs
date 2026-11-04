using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Oauth;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Publishers;

/// <summary>
/// YouTube Shorts via the YouTube Data API v3 (resumable upload). Docs checked 2026-11-04:
///  - https://developers.google.com/youtube/v3/docs/videos/insert   (part=snippet,status; scope youtube.upload; 100 uploads/day bucket, 1 unit each)
///  - https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol
///      POST .../upload/youtube/v3/videos?uploadType=resumable&amp;part=... (X-Upload-Content-Length/-Type) -> 200 + Location (session URI)
///      -> PUT the bytes (201 + video resource); after a dropped connection or 500/502/503/504: PUT "Content-Range: bytes */total" ->
///      308 + "Range: bytes=0-N" (none = nothing received) -> PUT the rest "bytes N+1-(total-1)/total"; exponential backoff; 404 = session expired.
///  - https://developers.google.com/youtube/v3/docs/videos           (title max 100 chars, description max 5000 bytes, privacyStatus private|unlisted|public,
///      selfDeclaredMadeForKids, containsSyntheticMedia, statistics.viewCount/likeCount/commentCount are strings)
///  - https://developers.google.com/youtube/v3/docs/videos/list     (part=statistics, up to 50 ids, 1 unit)
/// A Short is a vertical/square video up to 3 minutes (YouTube Help); "#Shorts" in the title marks it.
/// The privacy status comes from the setting (default private). Every upload still goes through the same approval + schedule gates as the other platforms.
/// </summary>
public class YouTubePublisher(IHttpClientFactory http, AccountService accounts, OAuthService oauth, TimeProvider time, ILogger<YouTubePublisher> logger) : IPlatformPublisher
{
    public const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos";
    public const string SessionUrl = UploadEndpoint + "?uploadType=resumable&part=snippet,status";
    public const string VideosListUrl = "https://www.googleapis.com/youtube/v3/videos";
    public const string CategoryGaming = "20";
    public const int MaxTitleChars = 100;
    public const int MaxDescriptionBytes = 5000;
    public const double MaxShortSeconds = 180;
    public const string ShortsTag = "#Shorts";
    /// <summary>The access token is renewed when it has less than this left (a long upload must not start with a dying token).</summary>
    public static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(2);

    public Platform Platform => Platform.YouTube;
    public string Name => "YouTube";

    /// <summary>First backoff step between resume attempts (doubles each time, max 5 min). Tests set it to zero.</summary>
    public static TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public static int MaxResumeAttempts { get; set; } = 6;

    private HttpClient Client() => http.CreateClient("youtube");

    // ------------------------------------------------------------------------------------------ pure rules (unit tested)

    /// <summary>"private" unless the owner chose unlisted or public. Anything unknown stays private.</summary>
    public static string ChoosePrivacy(RuntimeSettings s) => (s.YouTubePrivacy ?? "").Trim().ToLowerInvariant() switch
    {
        "public" => "public",
        "unlisted" => "unlisted",
        _ => "private",
    };

    /// <summary>Shorts need a vertical (or square) video of at most 3 minutes; we require vertical. Null = ok.</summary>
    public static string? ValidateClip(Clip c)
    {
        if (c.Width <= 0 || c.Height <= 0) return $"clip dimensions unknown ({c.Width}x{c.Height}); cannot confirm it is vertical";
        if (c.Height <= c.Width) return $"clip is not vertical ({c.Width}x{c.Height}); YouTube Shorts need a vertical video";
        if (c.DurationSec <= 0) return "clip duration unknown";
        if (c.DurationSec > MaxShortSeconds) return $"clip {c.DurationSec:0.#}s is longer than the {MaxShortSeconds:0} s Shorts limit";
        return null;
    }

    /// <summary>Title: first caption line without hashtags/angle brackets, shortened so that " #Shorts" fits into 100 characters.</summary>
    public static string BuildTitle(string? caption, string fallback = "VRLink highlight")
    {
        var line = (caption ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        var words = line.Replace('<', ' ').Replace('>', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith('#'));
        var text = string.Join(' ', words);
        if (text.Length == 0) text = fallback;
        int max = MaxTitleChars - ShortsTag.Length - 1;
        if (text.Length > max)
        {
            int cut = max - 1;                                          // room for the ellipsis
            if (char.IsHighSurrogate(text[cut - 1])) cut--;             // never split an emoji
            var head = text[..cut];
            int sp = head.LastIndexOf(' ');
            if (sp > cut / 2) head = head[..sp];                        // prefer a word boundary
            text = head.TrimEnd(' ', '.', ',', ';', ':', '-', '!', '?') + "\u2026";
        }
        return text + " " + ShortsTag;
    }

    /// <summary>Description: composed caption (+ hashtags) with #Shorts, then the optional booking link; at most 5000 bytes (UTF-8), no angle brackets.</summary>
    public static string BuildDescription(string? caption, string? bookingLink)
    {
        var body = (caption ?? "").Replace("\r", "").Replace('<', ' ').Replace('>', ' ').Trim();
        string tag = "";                                         // appended after the (possibly shortened) caption so it always survives
        if (!body.Contains(ShortsTag, StringComparison.OrdinalIgnoreCase))
        {
            if (body.Length == 0) body = ShortsTag;
            else tag = body.Split('\n')[^1].TrimStart().StartsWith('#') ? " " + ShortsTag : "\n\n" + ShortsTag;
        }
        var link = (bookingLink ?? "").Trim();
        var suffix = Uri.TryCreate(link, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? "\n\nBook your squad: " + u.AbsoluteUri : "";
        int budget = MaxDescriptionBytes - Encoding.UTF8.GetByteCount(suffix) - Encoding.UTF8.GetByteCount(tag);
        while (Encoding.UTF8.GetByteCount(body) > budget)
        {
            int drop = Math.Max(1, (Encoding.UTF8.GetByteCount(body) - budget) / 4);
            int keep = Math.Max(0, body.Length - drop);
            if (keep > 0 && char.IsHighSurrogate(body[keep - 1])) keep--;
            body = body[..keep].TrimEnd();
        }
        return body + tag + suffix;
    }

    /// <summary>Request body of videos.insert (part=snippet,status).</summary>
    public static JsonObject BuildMetadata(PublishRequest r) => new()
    {
        ["snippet"] = new JsonObject
        {
            ["title"] = BuildTitle(r.Post.Caption),
            ["description"] = BuildDescription(r.Post.Caption, r.Settings.YouTubeBookingLink),
            ["categoryId"] = CategoryGaming,
        },
        ["status"] = new JsonObject
        {
            ["privacyStatus"] = ChoosePrivacy(r.Settings),
            ["selfDeclaredMadeForKids"] = false,
            ["containsSyntheticMedia"] = false,
        },
    };

    /// <summary>videos.list?part=statistics items -> stats (values are strings in the JSON; likeCount is missing when hidden).</summary>
    public static List<PostStats> ParseStats(JsonNode response, IEnumerable<PlatformPost> posts)
    {
        var result = new List<PostStats>();
        var list = posts.ToList();
        foreach (var item in response["items"]?.AsArray() ?? new JsonArray())
        {
            var post = list.FirstOrDefault(p => p.PostId == item?["id"]?.ToString());
            if (post == null) continue;
            var st = item!["statistics"];
            result.Add(new PostStats(post.Id, ApiJson.Long(st?["viewCount"]), ApiJson.Long(st?["likeCount"]), ApiJson.Long(st?["commentCount"]), 0, 0, 0));
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------ auth

    private async Task<string> TokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var conn = await accounts.GetConnectionAsync(Platform.YouTube, ct)
                   ?? throw new PlatformApiException("YouTube not connected (Accounts page)", retryable: false);
        var expiring = conn.AccessExpiresUtc is { } exp && exp - time.GetUtcNow().UtcDateTime < TokenMargin;
        if (forceRefresh || expiring || conn.AccessTokenEnc == null)
            await oauth.RefreshYouTubeAsync(ct);
        return await accounts.GetAccessTokenAsync(Platform.YouTube, ct)
               ?? throw new PlatformApiException("YouTube access token unavailable after refresh", retryable: true);
    }

    /// <summary>Sends a freshly built request with a valid token; one 401 -> refresh the token and send again.</summary>
    private async Task<HttpResponseMessage> SendAuthedAsync(Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        var token = await TokenAsync(false, ct);
        using (var req = build(token))
        {
            var resp = await Client().SendAsync(req, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;
            resp.Dispose();
        }
        token = await TokenAsync(true, ct);
        using var again = build(token);
        return await Client().SendAsync(again, ct);
    }

    // ------------------------------------------------------------------------------------------ publish

    public async Task<PublishResult> PublishAsync(PublishRequest r, CancellationToken ct)
    {
        string? sessionUri = null;   // stored in Post.ExternalId on a failure so the next attempt resumes instead of uploading twice
        try
        {
            // defence in depth: the pipeline already refuses unapproved clips before calling a publisher
            if (!Pipeline.IsApprovedForUpload(r.Clip, out var why))
                return PublishResult.Fail("YouTube: clip is not approved for upload: " + why, retryable: false);
            var invalid = ValidateClip(r.Clip);
            if (invalid != null) return PublishResult.Fail("YouTube: " + invalid, retryable: false);
            if (!File.Exists(r.Clip.VideoPath)) return PublishResult.Fail("YouTube: video file missing: " + r.Clip.VideoPath, retryable: false);
            var size = new FileInfo(r.Clip.VideoPath).Length;
            if (size <= 0) return PublishResult.Fail("YouTube: empty video file", retryable: false);

            long offset = 0;
            JsonNode? video = null;
            // 1a) an unfinished upload of this post from an earlier attempt: ask the server how far it got
            if (r.Post.ExternalId is { } old && old.StartsWith(UploadEndpoint, StringComparison.Ordinal))
            {
                var q = await QueryStatusAsync(old, size, ct);
                if (q.Expired) logger.LogInformation("YouTube upload session of post {Id} expired, starting a new one", r.Post.Id);
                else { sessionUri = old; video = q.Video; offset = q.Offset; }
            }
            // 1b) new session
            if (sessionUri == null)
            {
                sessionUri = await StartSessionAsync(r, size, ct);
                offset = 0;
            }
            // 2) bytes
            video ??= await UploadAsync(sessionUri, r.Clip.VideoPath, size, offset, ct);

            var id = video["id"]?.GetValue<string>()
                     ?? throw new PlatformApiException("YouTube upload finished without a video id: " + ApiJson.Trunc(video.ToJsonString(), 200), retryable: false);
            var uploadStatus = video["status"]?["uploadStatus"]?.GetValue<string>();
            if (uploadStatus is "rejected" or "failed")
                return PublishResult.Fail($"YouTube {uploadStatus}: {video["status"]?["rejectionReason"] ?? video["status"]?["failureReason"]}", retryable: false, id);

            return new PublishResult(true, false, ExternalId: id, PostId: id, PostUrl: $"https://www.youtube.com/shorts/{id}", PrivacyLevel: ChoosePrivacy(r.Settings));
        }
        catch (PlatformApiException ex)
        {
            var msg = ex.Message.Contains("quotaExceeded") ? ex.Message + " (daily API quota used up; it resets at midnight Pacific Time, press Retry later)" : ex.Message;
            return PublishResult.Fail(msg, ex.Retryable, sessionUri);
        }
        catch (HttpRequestException ex) { return PublishResult.Fail("YouTube network: " + ex.Message, true, sessionUri); }
    }

    private async Task<string> StartSessionAsync(PublishRequest r, long size, CancellationToken ct)
    {
        var meta = BuildMetadata(r).ToJsonString();
        using var resp = await SendAuthedAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, SessionUrl) { Content = new StringContent(meta, Encoding.UTF8, "application/json") };
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=UTF-8");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-Upload-Content-Length", size.ToString());
            req.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/mp4");
            return req;
        }, ct);
        if (!resp.IsSuccessStatusCode) await ApiJson.ReadAsync(resp, "YouTube videos.insert (start session)", ct);   // throws with Google's error reason
        return resp.Headers.Location?.ToString()
               ?? throw new PlatformApiException("YouTube start session: no Location header", retryable: true);
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(5).Ticks, RetryBaseDelay.Ticks * (1L << Math.Min(attempt - 1, 20))));

    /// <summary>Uploads from <paramref name="offset"/> with a single PUT; on a dropped connection or 500/502/503/504 asks the server how much it has and continues.</summary>
    private async Task<JsonNode> UploadAsync(string sessionUri, string path, long size, long offset, CancellationToken ct)
    {
        bool query = false;
        for (int attempt = 1; ; attempt++)
        {
            string fail;
            try
            {
                if (query)
                {
                    var q = await QueryStatusAsync(sessionUri, size, ct);
                    if (q.Expired) throw new PlatformApiException("YouTube upload session expired (404); the next attempt starts a new upload", retryable: true);
                    if (q.Video != null) return q.Video;
                    offset = q.Offset;
                    query = false;
                }
                using var resp = await SendAuthedAsync(token => DataRequest(sessionUri, token, path, size, offset), ct);
                var code = (int)resp.StatusCode;
                if (code is 200 or 201) return await ApiJson.ReadAsync(resp, "YouTube upload", ct);
                if (code == 308) { offset = OffsetFrom(resp); fail = "308 resume incomplete"; }
                else if (code is 500 or 502 or 503 or 504) { fail = "HTTP " + code; query = true; }
                else if (code == 404) throw new PlatformApiException("YouTube upload session expired (404); the next attempt starts a new upload", retryable: true, 404);
                else { await ApiJson.ReadAsync(resp, "YouTube upload", ct); throw new PlatformApiException($"YouTube upload: HTTP {code}", false, code); }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                fail = ex.Message;
                query = true;
            }
            if (attempt >= MaxResumeAttempts)
                throw new PlatformApiException($"YouTube upload interrupted ({fail}) after {attempt} attempts at byte {offset} of {size}", retryable: true);
            logger.LogWarning("YouTube upload interrupted ({Why}), attempt {N}/{Max}, resuming", fail, attempt, MaxResumeAttempts);
            await Task.Delay(Backoff(attempt), ct);
        }
    }

    private static HttpRequestMessage DataRequest(string sessionUri, string token, string path, long size, long offset)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) { Position = offset };
        var req = new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = new StreamContent(fs) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        req.Content.Headers.ContentLength = size - offset;
        if (offset > 0) req.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, size - 1, size);   // the first PUT is a plain full-body PUT
        return req;
    }

    private record UploadState(long Offset, JsonNode? Video, bool Expired);

    /// <summary>PUT with "Content-Range: bytes */total" and no body: 200/201 = finished (video resource), 308 = incomplete (Range header = received bytes), 404 = session gone.</summary>
    private async Task<UploadState> QueryStatusAsync(string sessionUri, long size, CancellationToken ct)
    {
        using var resp = await SendAuthedAsync(token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = new ByteArrayContent([]) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(size);   // "bytes */size"
            req.Content.Headers.ContentLength = 0;
            return req;
        }, ct);
        var code = (int)resp.StatusCode;
        if (code is 200 or 201) return new UploadState(size, await ApiJson.ReadAsync(resp, "YouTube upload status", ct), false);
        if (code == 308) return new UploadState(OffsetFrom(resp), null, false);
        if (code == 404 || code == 410) return new UploadState(0, null, true);
        if (code is 500 or 502 or 503 or 504) throw new HttpRequestException("upload status query: HTTP " + code);
        await ApiJson.ReadAsync(resp, "YouTube upload status", ct);
        throw new PlatformApiException($"YouTube upload status: HTTP {code}", false, code);
    }

    /// <summary>"Range: bytes=0-999999" -> next byte to send is 1000000; no Range header = nothing received yet.</summary>
    private static long OffsetFrom(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Range", out var v)) return 0;
        var s = v.FirstOrDefault() ?? "";
        var dash = s.LastIndexOf('-');
        return dash >= 0 && long.TryParse(s[(dash + 1)..], out var last) ? last + 1 : 0;
    }

    // ------------------------------------------------------------------------------------------ stats

    public async Task<IReadOnlyList<PostStats>> GetStatsAsync(IReadOnlyList<PlatformPost> posts, RuntimeSettings settings, CancellationToken ct)
    {
        var result = new List<PostStats>();
        var withId = posts.Where(p => !string.IsNullOrEmpty(p.PostId)).ToList();
        foreach (var batch in withId.Chunk(50))   // videos.list: up to 50 ids per call, 1 quota unit
        {
            var url = $"{VideosListUrl}?part=statistics&maxResults=50&id={Uri.EscapeDataString(string.Join(",", batch.Select(p => p.PostId)))}";
            using var resp = await SendAuthedAsync(token =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return req;
            }, ct);
            result.AddRange(ParseStats(await ApiJson.ReadAsync(resp, "YouTube videos.list", ct), batch));
        }
        return result;
    }
}