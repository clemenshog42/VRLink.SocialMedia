using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace VRLinkPublisher.Tests;

/// <summary>Fake VRLinkWeb share-pack API + Vercel Blob upload endpoint (docs/SHARE_PACKS.md).</summary>
public sealed class FakeShareApi : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Auth, string? ApiVersion, string? XContentType, string? ContentType, long Bytes)> Calls = new();
    public JsonElement? CreateBody;
    public bool Revoked;
    public int ExpireFirstUploads;      // the first N blob PUTs answer "Token expired"
    public bool MissingThumbOnFirstComplete;
    public int Completes, Refreshes;

    private static object Grant(string token, string id, string ext, string type, int gen) => new
    {
        url = $"https://vercel.com/api/blob/?pathname=share%2F{token}%2F{id}.{ext}",
        method = "PUT",
        headers = new Dictionary<string, string> { ["authorization"] = $"Bearer vercel_blob_client_store_{gen}", ["x-api-version"] = "11", ["x-content-type"] = type },
        pathname = $"share/{token}/{id}.{ext}", contentType = type,
    };

    private object Uploads(int gen) => CreateBody!.Value.GetProperty("clips").EnumerateArray().Select(c => c.GetProperty("clipId").GetString()!)
        .Select(id => new
        {
            clipId = id, videoUploadUrl = $"https://vercel.com/api/blob/?pathname=share%2Ftok456%2F{id}.mp4", thumbUploadUrl = $"https://vercel.com/api/blob/?pathname=share%2Ftok456%2F{id}.jpg",
            video = Grant("tok456", id, "mp4", "video/mp4", gen), thumb = Grant("tok456", id, "jpg", "image/jpeg", gen),
        }).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var bytes = r.Content == null ? [] : await r.Content.ReadAsByteArrayAsync(ct);
        var path = r.RequestUri!.AbsolutePath;
        string? H(string n) => r.Headers.TryGetValues(n, out var v) ? v.First() : null;
        Calls.Add((r.Method, r.RequestUri.ToString(), r.Headers.Authorization?.ToString() ?? H("authorization"), H("x-api-version"), H("x-content-type"),
                   r.Content?.Headers.ContentType?.MediaType, bytes.Length));
        if (r.RequestUri.Host == "vercel.com" && r.Method == HttpMethod.Put)
        {
            if (ExpireFirstUploads > 0) { ExpireFirstUploads--; return Json(HttpStatusCode.Forbidden, new { error = new { code = "forbidden", message = "Token expired" } }); }
            if (H("x-api-version") != "11" || H("x-content-type") == null || r.Headers.Authorization?.ToString().StartsWith("Bearer vercel_blob_client_") != true)
                return Json(HttpStatusCode.BadRequest, new { error = new { code = "bad_request", message = "missing headers" } });
            return Json(HttpStatusCode.OK, new { url = "https://store.public.blob.vercel-storage.com/x", pathname = "x", contentType = H("x-content-type") });
        }
        if (r.Headers.Authorization?.ToString() != "Bearer test-key") return Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" });
        if (r.Method == HttpMethod.Post && path == "/api/share-packs")
        {
            CreateBody = JsonDocument.Parse(bytes).RootElement.Clone();
            return Json(HttpStatusCode.Created, new { packId = "pk_123", token = "tok456", shareUrl = "https://web.test/clip/tok456", expiresUtc = "2026-11-19T10:00:00Z",
                                                      uploadsExpireUtc = "2026-10-20T12:00:00Z", uploads = Uploads(1) });
        }
        if (r.Method == HttpMethod.Post && path == "/api/share-packs/pk_123/uploads") { Refreshes++; return Json(HttpStatusCode.OK, new { packId = "pk_123", uploads = Uploads(2) }); }
        if (r.Method == HttpMethod.Post && path == "/api/share-packs/pk_123/complete")
        {
            Completes++;
            if (MissingThumbOnFirstComplete && Completes == 1)
                return Json(HttpStatusCode.Conflict, new { error = "missing_blobs", details = new { missing = new[] { "m1-r01-1.jpg" } } });
            return Json(HttpStatusCode.OK, new { shareUrl = "https://web.test/clip/tok456" });
        }
        if (r.Method == HttpMethod.Delete && path == "/api/share-packs/pk_123") { Revoked = true; return Json(HttpStatusCode.OK, new { packId = "pk_123", status = "revoked" }); }
        return Json(HttpStatusCode.NotFound, new { error = "not_found" });
    }

    private static HttpResponseMessage Json(HttpStatusCode code, object o) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
}

public class SharePackTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<TestHost> HostWithClipsAsync()
    {
        var h = new TestHost(T0);
        h.WriteClip("m1-r01-1-irl", "Ace", 90, mp4Bytes: Encoding.UTF8.GetBytes("video-a"));
        h.WriteClip("m1-r01-1", "Ace", 90, mp4Bytes: Encoding.UTF8.GetBytes("video-b"));
        h.WriteClip("m1-r02-5", "Kills3", 70, mp4Bytes: Encoding.UTF8.GetBytes("video-bot"), heroIsBot: true);
        h.WriteClip("m1-r03-2", "Kills3", 10, mp4Bytes: Encoding.UTF8.GetBytes("video-low")); // rejected by the checker (score)
        await h.SettingsAsync(s => s.AllowBotHighlights = true);
        await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        await h.Get<Pipeline>().CheckNewAsync();
        return h;
    }

    [Fact]
    public async Task Sessions_group_by_match_and_skip_bot_heroes_and_rejected_clips()
    {
        await using var h = await HostWithClipsAsync();
        var groups = await h.Get<SharePackService>().SessionsAsync();
        var g = Assert.Single(groups);
        Assert.Equal("m1", g.MatchId);
        Assert.Equal(["m1-r01-1", "m1-r01-1-irl"], g.Clips.Select(c => c.HighlightId).Order());
        Assert.Null(g.Pack);
    }

    [Fact]
    public async Task Nothing_is_shared_without_an_explicit_release()
    {
        await using var h = await HostWithClipsAsync();
        // the whole pipeline (import, checks, schedule, publish) never creates a pack
        await h.Get<Pipeline>().ScheduleDueAsync();
        await h.Get<Pipeline>().PublishDueAsync();
        await using (var db = h.Db()) Assert.Empty(await db.SharePacks.ToListAsync());
        var svc = h.Get<SharePackService>();
        var ids = (await svc.SessionsAsync())[0].Clips.Select(c => c.Id).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReleaseAsync("m1", ids, releasedBy: ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReleaseAsync("m1", [], "owner"));
        await using var db2 = h.Db();
        var bot = await db2.Clips.SingleAsync(c => c.HeroIsBot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReleaseAsync("m1", [bot.Id], "owner"));
        Assert.Empty(await db2.SharePacks.ToListAsync());
    }

    [Fact]
    public async Task Release_follows_the_web_contract_then_revoke()
    {
        await using var h = await HostWithClipsAsync();
        var api = new FakeShareApi();
        var svc = h.Get<SharePackService>();
        svc.UploaderOverride = new HttpShareUploader(new HttpClient(api), "https://web.test", "test-key");
        var clips = (await svc.SessionsAsync())[0].Clips;
        var pack = await svc.ReleaseAsync("m1", clips.Select(c => c.Id).ToList(), "owner");

        Assert.Equal(SharePackStatus.Released, pack.Status);
        Assert.Equal("https://web.test/clip/tok456", pack.ShareUrl);
        Assert.Equal("pk_123", pack.PackId);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc), pack.ExpiresUtc!.Value.ToUniversalTime());
        Assert.Equal("owner", pack.ReleasedBy);

        var body = api.CreateBody!.Value;
        Assert.Equal("m1", body.GetProperty("matchId").GetString());
        var c0 = body.GetProperty("clips").EnumerateArray().Single(c => c.GetProperty("clipId").GetString() == "m1-r01-1");
        Assert.Equal(SharePackService.Sha256(clips.Single(c => c.HighlightId == "m1-r01-1").VideoPath), c0.GetProperty("sha256").GetString());
        Assert.Equal("video-b".Length, c0.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("game", c0.GetProperty("format").GetString());
        Assert.All(api.Calls.Where(c => c.Url.StartsWith("https://web.test/api/")), c => Assert.Equal("Bearer test-key", c.Auth));
        Assert.EndsWith("Z", body.GetProperty("createdUtc").GetString()); // ISO 8601 with offset
        var puts = api.Calls.Where(c => c.Method == HttpMethod.Put).ToList();
        Assert.Equal(4, puts.Count); // 2 videos + 2 thumbnails, straight to Vercel Blob
        var vid = puts.Single(p => p.Url.Contains("share%2Ftok456%2Fm1-r01-1.mp4"));
        Assert.Equal("video/mp4", vid.XContentType);      // request header, as the website documents
        Assert.Equal("11", vid.ApiVersion);
        Assert.StartsWith("Bearer vercel_blob_client_", vid.Auth);
        Assert.Null(vid.ContentType);                      // no Content-Type content header
        Assert.Equal("video-b".Length, vid.Bytes);
        Assert.Contains(puts, p => p.Url.EndsWith("m1-r01-1.jpg") && p.XContentType == "image/jpeg");
        Assert.Equal(1, api.Completes);

        var g = Assert.Single(await svc.SessionsAsync());
        Assert.Equal(pack.Id, g.Pack!.Id);

        await svc.RevokeAsync(pack.Id);
        Assert.True(api.Revoked);
        Assert.Null((await svc.SessionsAsync())[0].Pack);
        await using var db = h.Db();
        Assert.Equal(SharePackStatus.Revoked, (await db.SharePacks.SingleAsync()).Status);
    }

    [Fact]
    public async Task Expired_upload_tokens_are_refreshed_and_missing_blobs_reuploaded()
    {
        await using var h = await HostWithClipsAsync();
        HttpShareUploader.RetryDelay = TimeSpan.Zero;
        var api = new FakeShareApi { ExpireFirstUploads = 1, MissingThumbOnFirstComplete = true };
        var svc = h.Get<SharePackService>();
        svc.UploaderOverride = new HttpShareUploader(new HttpClient(api), "https://web.test", "test-key");
        var pack = await svc.ReleaseAsync("m1", (await svc.SessionsAsync())[0].Clips.Select(c => c.Id).ToList(), "owner");
        Assert.Equal(SharePackStatus.Released, pack.Status);
        Assert.Equal(1, api.Refreshes);   // POST /uploads after "Token expired"
        Assert.Equal(2, api.Completes);   // 409 missing_blobs → re-upload → complete again
        Assert.Contains(api.Calls, c => c.Auth == "Bearer vercel_blob_client_store_2"); // fresh grant used
        Assert.Equal(2, api.Calls.Count(c => c.Method == HttpMethod.Put && c.Url.EndsWith("m1-r01-1.jpg")));
    }

    [Fact]
    public async Task Failed_upload_marks_the_pack_failed()
    {
        await using var h = await HostWithClipsAsync();
        var svc = h.Get<SharePackService>();
        svc.UploaderOverride = new HttpShareUploader(new HttpClient(new FakeShareApi()), "https://web.test", "wrong-key");
        var ids = (await svc.SessionsAsync())[0].Clips.Select(c => c.Id).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReleaseAsync("m1", ids, "owner"));
        await using var db = h.Db();
        var p = await db.SharePacks.SingleAsync();
        Assert.Equal(SharePackStatus.Failed, p.Status);
        Assert.Contains("401", p.Error);
    }

    [Fact]
    public async Task Local_uploader_writes_the_pack_folder_until_the_web_api_is_live()
    {
        await using var h = await HostWithClipsAsync();
        var svc = h.Get<SharePackService>();
        Assert.Equal("local", (await svc.UploaderAsync()).Name); // no base URL / key configured
        var ids = (await svc.SessionsAsync())[0].Clips.Select(c => c.Id).ToList();
        var pack = await svc.ReleaseAsync("m1", ids, "owner");
        Assert.StartsWith("http://localhost:5080/share/", pack.ShareUrl);
        var dir = Path.Combine(h.Dir, "share", pack.Token!);
        Assert.True(File.Exists(Path.Combine(dir, "m1-r01-1.mp4")));
        Assert.True(File.Exists(Path.Combine(dir, "complete")));
        await svc.RevokeAsync(pack.Id);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Api_key_is_stored_encrypted()
    {
        await using var h = new TestHost(T0);
        var sec = h.Get<SharePackSecrets>();
        await sec.SetAsync("super-secret-share-key");
        Assert.Equal("super-secret-share-key", await sec.GetAsync());
        await using var db = h.Db();
        Assert.DoesNotContain("super-secret-share-key", (await db.Settings.SingleAsync(s => s.Key == "sharePackApiKey")).Value);
    }

    [Theory]
    [InlineData("https://vrlink.example/clip/9f8e7d6c5b4a")]
    [InlineData("http://localhost:5080/share/0123456789abcdef01234567")]
    public void Qr_code_decodes_back_to_the_share_url(string url)
    {
        var m = SharePackService.QrMatrix(url);
        int scale = 6, n = m.Length, size = n * scale;
        var lum = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                lum[y * size + x] = m[y / scale][x / scale] ? (byte)0 : (byte)255;
        var bmp = new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(lum, size, size, RGBLuminanceSource.BitmapFormat.Gray8)));
        var result = new QRCodeReader().decode(bmp, new Dictionary<DecodeHintType, object> { [DecodeHintType.PURE_BARCODE] = false });
        Assert.NotNull(result);
        Assert.Equal(url, result.Text);
        Assert.StartsWith("<svg", SharePackService.QrSvg(url).TrimStart());
    }
}
