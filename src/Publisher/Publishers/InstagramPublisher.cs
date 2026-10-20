using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Publishers;

/// <summary>
/// Instagram Graph API (Instagram API with Facebook Login), Reels.
/// Docs checked 2026-10-19 (latest version listed: v26.0):
///  - https://developers.facebook.com/docs/instagram-platform/content-publishing
///  - https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights
/// Flow: POST /{ig-user-id}/media (media_type=REELS, upload_type=resumable) → POST rupload.facebook.com/ig-api-upload/{ver}/{container}
///       → poll GET /{container}?fields=status_code until FINISHED → POST /{ig-user-id}/media_publish (creation_id).
/// Limit: 100 API-published posts per 24 h (content_publishing_limit). Token: Facebook Page access token.
/// </summary>
public class InstagramPublisher(IHttpClientFactory http, AccountService accounts, IOptions<PublisherOptions> options, ILogger<InstagramPublisher> logger) : IPlatformPublisher
{
    public Platform Platform => Platform.Instagram;
    public string Name => "Instagram";

    public static TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan StatusPollTimeout { get; set; } = TimeSpan.FromMinutes(10);

    private string Ver => options.Value.GraphApiVersion;
    private string Graph => $"https://graph.facebook.com/{Ver}";
    private HttpClient Client() => http.CreateClient("instagram");

    private async Task<(string Token, string IgUserId)> AuthAsync(CancellationToken ct)
    {
        var conn = await accounts.GetConnectionAsync(Platform.Instagram, ct);
        var token = await accounts.GetAccessTokenAsync(Platform.Instagram, ct);
        if (conn?.AccountId == null || token == null)
            throw new PlatformApiException("Instagram not connected or token expired", retryable: true);
        return (token, conn.AccountId);
    }

    private static FormUrlEncodedContent Form(params (string, string?)[] kv) =>
        new(kv.Where(x => x.Item2 != null).Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2!)));

    public async Task<PublishResult> PublishAsync(PublishRequest r, CancellationToken ct)
    {
        string? containerId = null;
        try
        {
            var (token, ig) = await AuthAsync(ct);
            var c = Client();
            var useUrl = r.Settings.InstagramUseVideoUrl && !string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl);

            // 1) container
            var createForm = Form(
                ("media_type", "REELS"),
                ("caption", r.Post.Caption ?? ""),
                ("share_to_feed", r.Settings.InstagramShareToFeed ? "true" : "false"), // VERIFY: share_to_feed still accepted for REELS
                useUrl ? ("video_url", PublicUrlFor(r.Clip)) : ("upload_type", "resumable"),
                ("access_token", token));
            var created = await ApiJson.ReadAsync(await c.PostAsync($"{Graph}/{ig}/media", createForm, ct), "IG create container", ct);
            containerId = created["id"]?.ToString() ?? throw new PlatformApiException("IG container: no id", true);

            // 2) resumable upload of the local file
            if (!useUrl)
            {
                var size = new FileInfo(r.Clip.VideoPath).Length;
                await using var fs = new FileStream(r.Clip.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var up = new HttpRequestMessage(HttpMethod.Post, $"https://rupload.facebook.com/ig-api-upload/{Ver}/{containerId}")
                {
                    Content = new StreamContent(fs),
                };
                up.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
                up.Headers.TryAddWithoutValidation("offset", "0");
                up.Headers.TryAddWithoutValidation("file_size", size.ToString());
                up.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); // VERIFY: docs use curl --data-binary without explicit type
                var upRes = await ApiJson.ReadAsync(await c.SendAsync(up, ct), "IG rupload", ct);
                if (upRes["success"]?.GetValue<bool>() != true)
                    throw new PlatformApiException("IG rupload not successful: " + upRes.ToJsonString(), true);
            }

            // 3) wait for processing
            var deadline = DateTime.UtcNow + StatusPollTimeout;
            while (true)
            {
                await Task.Delay(StatusPollInterval, ct);
                var st = await ApiJson.ReadAsync(await c.GetAsync($"{Graph}/{containerId}?fields=status_code,status&access_token={Uri.EscapeDataString(token)}", ct), "IG container status", ct);
                var code = st["status_code"]?.GetValue<string>();
                if (code == "FINISHED") break;
                if (code is "ERROR" or "EXPIRED")
                    return PublishResult.Fail($"IG container {code}: {st["status"]}", retryable: code == "EXPIRED", containerId);
                if (DateTime.UtcNow > deadline)
                    return PublishResult.Fail($"IG container still {code} after {StatusPollTimeout.TotalMinutes:0} min", retryable: true, containerId);
            }

            // 4) publish
            var pub = await ApiJson.ReadAsync(await c.PostAsync($"{Graph}/{ig}/media_publish",
                Form(("creation_id", containerId), ("access_token", token)), ct), "IG media_publish", ct);
            var mediaId = pub["id"]?.ToString() ?? throw new PlatformApiException("IG media_publish: no id", true);

            string? permalink = null;
            try
            {
                var m = await ApiJson.ReadAsync(await c.GetAsync($"{Graph}/{mediaId}?fields=permalink,media_product_type&access_token={Uri.EscapeDataString(token)}", ct), "IG media", ct);
                permalink = m["permalink"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is PlatformApiException or HttpRequestException) { logger.LogWarning(ex, "IG permalink lookup failed"); }

            return new PublishResult(true, false, containerId, mediaId, permalink);
        }
        catch (PlatformApiException ex) { return PublishResult.Fail(ex.Message, ex.Retryable, containerId); }
        catch (HttpRequestException ex) { return PublishResult.Fail("Instagram network: " + ex.Message, true, containerId); }
    }

    private string PublicUrlFor(Clip clip)
    {
        // Relative path under ClipsRoot, served by the owner's public host (e.g. a CDN sync) — fallback only.
        var rel = Path.GetRelativePath(options.Value.ClipsRoot, clip.VideoPath).Replace('\\', '/');
        return options.Value.PublicBaseUrl.TrimEnd('/') + "/" + string.Join('/', rel.Split('/').Select(Uri.EscapeDataString));
    }

    public async Task<IReadOnlyList<PostStats>> GetStatsAsync(IReadOnlyList<PlatformPost> posts, RuntimeSettings settings, CancellationToken ct)
    {
        var result = new List<PostStats>();
        var (token, _) = await AuthAsync(ct);
        var c = Client();
        foreach (var p in posts.Where(p => p.PostId != null))
        {
            try
            {
                // VERIFY: "views" is marked "in development" for REELS in the docs; drop it if the API rejects it.
                var n = await ApiJson.ReadAsync(await c.GetAsync(
                    $"{Graph}/{p.PostId}/insights?metric=views,reach,likes,comments,shares,saved&access_token={Uri.EscapeDataString(token)}", ct),
                    "IG insights", ct);
                long Get(string name) => ApiJson.Long((n["data"]?.AsArray() ?? new JsonArray())
                    .FirstOrDefault(d => d?["name"]?.GetValue<string>() == name)?["values"]?[0]?["value"]);
                result.Add(new PostStats(p.Id, Get("views"), Get("likes"), Get("comments"), Get("shares"), Get("saved"), Get("reach")));
            }
            catch (PlatformApiException ex) { logger.LogWarning("IG insights for {Id} failed: {Msg}", p.PostId, ex.Message); }
        }
        return result;
    }
}
