using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

// ------------------------------------------------------------------ contract with the VRLinkWeb share-pack API
public sealed record SharePackClip(
    [property: JsonPropertyName("clipId")] string ClipId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("durationSec")] double DurationSec,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record SharePackRequest(
    [property: JsonPropertyName("venue")] string Venue,
    [property: JsonPropertyName("matchId")] string MatchId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("createdUtc")] DateTime CreatedUtc,
    [property: JsonPropertyName("clips")] List<SharePackClip> Clips);

/// <summary>Exact upload call for one file (Vercel Blob client upload): PUT url with these request headers, raw bytes.</summary>
public sealed record UploadGrant(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers,
    [property: JsonPropertyName("pathname")] string? Pathname,
    [property: JsonPropertyName("contentType")] string? ContentType);

public sealed record SharePackUploadTarget(
    [property: JsonPropertyName("clipId")] string ClipId,
    [property: JsonPropertyName("videoUploadUrl")] string VideoUploadUrl,
    [property: JsonPropertyName("thumbUploadUrl")] string? ThumbUploadUrl,
    [property: JsonPropertyName("video")] UploadGrant? Video = null,
    [property: JsonPropertyName("thumb")] UploadGrant? Thumb = null);

public sealed record SharePackCreated(
    [property: JsonPropertyName("packId")] string PackId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("shareUrl")] string ShareUrl,
    [property: JsonPropertyName("expiresUtc")] DateTime ExpiresUtc,
    [property: JsonPropertyName("uploads")] List<SharePackUploadTarget> Uploads,
    [property: JsonPropertyName("uploadsExpireUtc")] DateTime? UploadsExpireUtc = null);

/// <summary>Where share packs go. Web = VRLinkWeb API (Vercel Blob behind it); Local = a folder + a local share page (tests, until the web side is live).</summary>
public interface IShareUploader
{
    string Name { get; }
    Task<SharePackCreated> CreateAsync(SharePackRequest req, CancellationToken ct);
    Task UploadAsync(SharePackCreated pack, SharePackUploadTarget target, string videoPath, string? thumbPath, CancellationToken ct);
    Task<string> CompleteAsync(SharePackCreated pack, CancellationToken ct);
    Task RevokeAsync(string packId, CancellationToken ct);
}

/// <summary>
/// VRLinkWeb share-pack API (C:\vr\VRLinkWeb docs/SHARE_PACKS.md):
/// POST {base}/api/share-packs (Bearer key) → 201 {packId, token, shareUrl, expiresUtc, uploadsExpireUtc, uploads[{clipId, video:{url,method,headers}, thumb:{…}}]};
/// each file: PUT grant.url with exactly grant.headers (authorization: Bearer vercel_blob_client_…, x-api-version: 11, x-content-type) as
/// REQUEST headers and the raw bytes → Vercel Blob; retries on 5xx/429; "Token expired" → POST {packId}/uploads for fresh grants;
/// POST {packId}/complete → 200 {shareUrl} | 409 missing_blobs/size_mismatch (re-upload, retry) | 410 gone; DELETE {packId} revokes.
/// </summary>
public sealed class HttpShareUploader(HttpClient http, string baseUrl, string apiKey) : IShareUploader
{
    public string Name => "web";
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    private string Url(string path) => baseUrl.TrimEnd('/') + path;
    private readonly Dictionary<string, (string Video, string? Thumb)> _files = new();

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, Url(path));
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }

    public async Task<SharePackCreated> CreateAsync(SharePackRequest req, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Req(HttpMethod.Post, "/api/share-packs", req), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"share-packs: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadFromJsonAsync<SharePackCreated>(ct) ?? throw new InvalidOperationException("share-packs: empty response");
    }

    public async Task UploadAsync(SharePackCreated pack, SharePackUploadTarget t, string videoPath, string? thumbPath, CancellationToken ct)
    {
        _files[t.ClipId] = (videoPath, thumbPath);
        await UploadFileAsync(pack, t.ClipId, video: true, videoPath, ct);
        if (thumbPath != null && File.Exists(thumbPath) && (t.Thumb != null || t.ThumbUploadUrl != null)) await UploadFileAsync(pack, t.ClipId, video: false, thumbPath, ct);
    }

    private Dictionary<string, SharePackUploadTarget> _grants = new(StringComparer.OrdinalIgnoreCase);

    private async Task UploadFileAsync(SharePackCreated pack, string clipId, bool video, string path, CancellationToken ct)
    {
        if (_grants.Count == 0) foreach (var u in pack.Uploads) _grants[u.ClipId] = u;
        bool refreshed = false;
        for (int attempt = 1; ; attempt++)
        {
            var t = _grants[clipId];
            var grant = (video ? t.Video : t.Thumb) ?? new UploadGrant(video ? t.VideoUploadUrl : t.ThumbUploadUrl!, "PUT", null, null, video ? "video/mp4" : "image/jpeg");
            await using var fs = File.OpenRead(path);
            using var req = new HttpRequestMessage(new HttpMethod(grant.Method ?? "PUT"), grant.Url) { Content = new StreamContent(fs) };
            foreach (var h in grant.Headers ?? new()) req.Headers.TryAddWithoutValidation(h.Key, h.Value); // request headers, not content headers
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!refreshed && body.Contains("expired", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshGrantsAsync(pack, ct); refreshed = true; continue;
            }
            bool transient = (int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
            if (!transient || attempt >= 5)
                throw new InvalidOperationException($"upload {Path.GetFileName(path)}: HTTP {(int)resp.StatusCode} {body}");
            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromTicks(RetryDelay.Ticks * attempt);
            await Task.Delay(wait, ct);
        }
    }

    private async Task RefreshGrantsAsync(SharePackCreated pack, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Req(HttpMethod.Post, $"/api/share-packs/{pack.PackId}/uploads"), ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"fresh upload tokens: HTTP {(int)resp.StatusCode}");
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        foreach (var u in j.GetProperty("uploads").Deserialize<List<SharePackUploadTarget>>() ?? new()) _grants[u.ClipId] = u;
    }

    public async Task<string> CompleteAsync(SharePackCreated pack, CancellationToken ct)
    {
        for (int round = 0; ; round++)
        {
            using var resp = await http.SendAsync(Req(HttpMethod.Post, $"/api/share-packs/{pack.PackId}/complete"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                var j = JsonDocument.Parse(body).RootElement;
                return j.TryGetProperty("shareUrl", out var u) ? u.GetString() ?? pack.ShareUrl : pack.ShareUrl;
            }
            if (resp.StatusCode == HttpStatusCode.Conflict && round < 2)
            {
                // missing_blobs / size_mismatch → upload those files again, then complete again
                var d = JsonDocument.Parse(body).RootElement.GetProperty("details");
                var redo = new List<(string clip, bool video)>();
                if (d.TryGetProperty("missing", out var miss))
                    foreach (var m in miss.EnumerateArray()) { var f = m.GetString()!; redo.Add((Path.GetFileNameWithoutExtension(f), f.EndsWith(".mp4"))); }
                if (d.TryGetProperty("sizeMismatch", out var sm))
                    foreach (var m in sm.EnumerateArray()) redo.Add((m.GetProperty("clipId").GetString()!, true));
                foreach (var (clip, video) in redo)
                    if (_files.TryGetValue(clip, out var f) && (video || f.Thumb != null)) await UploadFileAsync(pack, clip, video, video ? f.Video : f.Thumb!, ct);
                continue;
            }
            throw new InvalidOperationException($"complete: HTTP {(int)resp.StatusCode} {body}");
        }
    }

    public async Task RevokeAsync(string packId, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Req(HttpMethod.Delete, $"/api/share-packs/{packId}"), ct);
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 404) throw new InvalidOperationException($"revoke: HTTP {(int)resp.StatusCode}");
    }
}

/// <summary>Local stand-in: copies the clips to &lt;DataDir&gt;/share/&lt;token&gt;/ and serves a simple share page from the Publisher.</summary>
public sealed class LocalShareUploader(string root, string publicBaseUrl, TimeProvider time) : IShareUploader
{
    public string Name => "local";

    public Task<SharePackCreated> CreateAsync(SharePackRequest req, CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var packId = "local-" + token[..8];
        var dir = Path.Combine(root, token);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pack.json"), JsonSerializer.Serialize(req));
        var uploads = req.Clips.Select(c => new SharePackUploadTarget(c.ClipId, $"local:{c.ClipId}.mp4", $"local:{c.ClipId}.jpg")).ToList();
        return Task.FromResult(new SharePackCreated(packId, token, publicBaseUrl.TrimEnd('/') + "/share/" + token, time.GetUtcNow().UtcDateTime.AddDays(30), uploads));
    }

    public Task UploadAsync(SharePackCreated pack, SharePackUploadTarget t, string videoPath, string? thumbPath, CancellationToken ct)
    {
        var dir = Path.Combine(root, pack.Token);
        File.Copy(videoPath, Path.Combine(dir, t.VideoUploadUrl["local:".Length..]), true);
        if (thumbPath != null && File.Exists(thumbPath) && t.ThumbUploadUrl != null) File.Copy(thumbPath, Path.Combine(dir, t.ThumbUploadUrl["local:".Length..]), true);
        return Task.CompletedTask;
    }

    public Task<string> CompleteAsync(SharePackCreated pack, CancellationToken ct)
    {
        File.WriteAllText(Path.Combine(root, pack.Token, "complete"), pack.ExpiresUtc.ToString("O"));
        return Task.FromResult(pack.ShareUrl);
    }

    public Task RevokeAsync(string packId, CancellationToken ct)
    {
        foreach (var d in Directory.Exists(root) ? Directory.GetDirectories(root) : [])
            if ("local-" + Path.GetFileName(d)[..8] == packId) Directory.Delete(d, true);
        return Task.CompletedTask;
    }
}

// ------------------------------------------------------------------ Publisher side
public sealed record SessionGroup(string MatchId, DateTime FirstUtc, string? Venue, List<Clip> Clips, SharePack? Pack);

/// <summary>DPAPI-protected share-pack API key (stored in the settings table, never in plain text).</summary>
public sealed class SharePackSecrets(IDbContextFactory<PublisherDb> dbf, ISecretProtector protector)
{
    private const string Key = "sharePackApiKey";

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync([Key], ct);
        return row == null ? null : protector.Unprotect(Convert.FromBase64String(row.Value));
    }

    public async Task SetAsync(string apiKey, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync([Key], ct);
        var v = Convert.ToBase64String(protector.Protect(apiKey.Trim()));
        if (row == null) db.Settings.Add(new KeyValueSetting { Key = Key, Value = v }); else row.Value = v;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsSetAsync(CancellationToken ct = default) => !string.IsNullOrEmpty(await GetAsync(ct));
}

/// <summary>
/// V4 customer share pack: one pack per match, released ONLY by an explicit click on the Sessions page ("Release share pack"),
/// never automatically. Bot-hero clips are never shared. Link expires after 30 days (web side deletes the blobs).
/// </summary>
public sealed class SharePackService(IDbContextFactory<PublisherDb> dbf, SettingsStore settings, SharePackSecrets secrets,
                                     IHttpClientFactory http, IOptions<PublisherOptions> options, EventLog log, TimeProvider time)
{
    /// <summary>Test hook / override; null = from settings (Web when a base URL + key are set, else Local).</summary>
    public IShareUploader? UploaderOverride { get; set; }

    public async Task<IShareUploader> UploaderAsync(CancellationToken ct = default)
    {
        if (UploaderOverride != null) return UploaderOverride;
        var s = await settings.GetAsync(ct);
        var key = await secrets.GetAsync(ct);
        if (!string.IsNullOrWhiteSpace(s.ShareWebBaseUrl) && !string.IsNullOrEmpty(key))
            return new HttpShareUploader(http.CreateClient("share"), s.ShareWebBaseUrl, key);
        return new LocalShareUploader(Path.Combine(options.Value.ResolveDataDir(), "share"), options.Value.Urls.Split(';')[0].Replace("0.0.0.0", "localhost"), time);
    }

    /// <summary>Website limits (docs/SHARE_PACKS.md): 1–30 clips, ≤ 50 MB per mp4.</summary>
    public const int MaxClips = 30;
    public const long MaxBytes = 52_428_800;

    public static bool Shareable(Clip c) => !c.HeroIsBot && c.Status != ClipStatus.Rejected && !string.IsNullOrEmpty(c.MatchId);

    public async Task<List<SessionGroup>> SessionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var clips = (await db.Clips.AsNoTracking().Where(c => c.MatchId != null && c.MatchId != "").ToListAsync(ct)).Where(Shareable).ToList();
        var packs = await db.SharePacks.AsNoTracking().ToListAsync(ct);
        return clips.GroupBy(c => c.MatchId!)
            .Select(g => new SessionGroup(g.Key, g.Min(c => c.CreatedUtc), g.Select(c => c.Venue).FirstOrDefault(v => v != null),
                g.OrderBy(c => c.CreatedUtc).ThenBy(c => c.HighlightId).ToList(),
                packs.Where(p => p.MatchId == g.Key && p.Status != SharePackStatus.Revoked).OrderByDescending(p => p.Id).FirstOrDefault()))
            .OrderByDescending(s => s.FirstUtc).ToList();
    }

    /// <summary>Called only from the "Release share pack" button. releasedBy identifies the human (e.g. "owner").</summary>
    public async Task<SharePack> ReleaseAsync(string matchId, IReadOnlyCollection<int> clipIds, string releasedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(releasedBy)) throw new InvalidOperationException("a share pack needs an explicit release by a person");
        if (clipIds.Count == 0) throw new InvalidOperationException("select at least one clip");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var clips = await db.Clips.Where(c => clipIds.Contains(c.Id)).ToListAsync(ct);
        if (clips.Count != clipIds.Count || clips.Any(c => c.MatchId != matchId)) throw new InvalidOperationException("clips must belong to this match");
        var bad = clips.FirstOrDefault(c => !Shareable(c));
        if (bad != null) throw new InvalidOperationException($"{bad.HighlightId} cannot be shared (bot hero or rejected)");
        if (clips.Count > MaxClips) throw new InvalidOperationException($"at most {MaxClips} clips per pack");
        var big = clips.FirstOrDefault(c => new FileInfo(c.VideoPath).Length > MaxBytes);
        if (big != null) throw new InvalidOperationException($"{big.HighlightId} is larger than 50 MB");

        var uploader = await UploaderAsync(ct);
        var pack = new SharePack
        {
            MatchId = matchId, Status = SharePackStatus.Uploading, Uploader = uploader.Name, ReleasedBy = releasedBy,
            CreatedUtc = time.GetUtcNow().UtcDateTime, ClipIds = string.Join(",", clips.Select(c => c.Id)),
        };
        db.SharePacks.Add(pack);
        await db.SaveChangesAsync(ct);
        try
        {
            var req = new SharePackRequest(clips.Select(c => c.Venue).FirstOrDefault(v => v != null) ?? "VRLink", matchId,
                $"VRLink session {clips.Min(c => c.CreatedUtc).ToLocalTime():dd.MM.yyyy HH:mm}", pack.CreatedUtc,
                clips.Select(c => new SharePackClip(c.HighlightId, c.Format ?? "game", c.Type, Title(c), c.DurationSec, c.Width, c.Height,
                                                    new FileInfo(c.VideoPath).Length, Sha256(c.VideoPath))).ToList());
            var created = await uploader.CreateAsync(req, ct);
            pack.PackId = created.PackId; pack.Token = created.Token; pack.ShareUrl = created.ShareUrl; pack.ExpiresUtc = created.ExpiresUtc;
            await db.SaveChangesAsync(ct);
            foreach (var t in created.Uploads)
            {
                var c = clips.FirstOrDefault(x => x.HighlightId == t.ClipId) ?? throw new InvalidOperationException($"upload target for unknown clip {t.ClipId}");
                await uploader.UploadAsync(created, t, c.VideoPath, c.ThumbPath, ct);
            }
            pack.ShareUrl = await uploader.CompleteAsync(created, ct);
            pack.Status = SharePackStatus.Released;
            pack.ReleasedUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await log.InfoAsync("SharePack", $"match {matchId}: {clips.Count} clip(s) released by {releasedBy} → {pack.ShareUrl}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pack.Status = SharePackStatus.Failed; pack.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            await log.ErrorAsync("SharePack", $"match {matchId}: release failed", ex.Message);
            throw;
        }
        return pack;
    }

    public async Task RevokeAsync(int packDbId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var p = await db.SharePacks.FindAsync([packDbId], ct) ?? throw new InvalidOperationException("pack not found");
        if (p.PackId != null) await (await UploaderAsync(ct)).RevokeAsync(p.PackId, ct);
        p.Status = SharePackStatus.Revoked; p.RevokedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await log.InfoAsync("SharePack", $"match {p.MatchId}: pack revoked");
    }

    public async Task<SharePack?> PackAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.SharePacks.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    private static string Title(Clip c) => c.Format switch
    {
        "irl" => $"{c.Type}: real life + game",
        "split" => $"{c.Type}: real vs in game",
        "reaction" => "Reaction",
        _ => $"{c.Type} replay",
    };

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>QR code as an SVG (no CDN, QRCoder MIT); ECC level Q survives TV glare/moiré.</summary>
    public static string QrSvg(string text, int pixelsPerModule = 10, string dark = "#000000", string light = "#ffffff")
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        return new SvgQRCode(data).GetGraphic(pixelsPerModule, dark, light, drawQuietZones: true);
    }

    /// <summary>The QR module matrix (true = dark), for tests/decoders.</summary>
    public static bool[][] QrMatrix(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        return data.ModuleMatrix.Select(row => Enumerable.Range(0, row.Length).Select(i => row[i]).ToArray()).ToArray();
    }
}
