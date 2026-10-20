using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using VRLinkPublisher;
using VRLinkPublisher.Components;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Oauth;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // As a Windows service the working dir is System32: load appsettings from the exe folder.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

builder.Host.UseWindowsService(o => o.ServiceName = "VRLinkPublisher");

var opts = builder.Configuration.GetSection("Publisher").Get<PublisherOptions>() ?? new PublisherOptions();
// Bind to localhost only (owner's PC). Change "Publisher:Urls" deliberately if ever needed.
builder.WebHost.UseUrls(opts.Urls);
// Serve framework assets (blazor.web.js) also when started with dotnet run outside Development; no-op after dotnet publish.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddPublisherCore(o => builder.Configuration.GetSection("Publisher").Bind(o));
builder.Services.AddPublisherWorkers();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();
await ServiceSetup.InitDatabaseAsync(app.Services);

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapPublisherEndpoints();

app.Logger.LogInformation("VRLink Publisher on {Urls}, data dir {Dir}, clips {Clips}", opts.Urls, opts.ResolveDataDir(), opts.ClipsRoot);
app.Run();

/// <summary>Non-Blazor endpoints: thumbnails/videos of imported clips and the OAuth redirects.</summary>
public static class PublisherEndpoints
{
    public static void MapPublisherEndpoints(this WebApplication app)
    {
        // Media: only files of imported clips (path comes from the DB, never from the request).
        app.MapGet("/media/thumb/{id:int}", async (int id, IDbContextFactory<PublisherDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var c = await db.Clips.FindAsync(id);
            return c?.ThumbPath != null && File.Exists(c.ThumbPath) ? Results.File(c.ThumbPath, "image/jpeg") : Results.NotFound();
        });
        // Arena camera frame (proxied from the local Capture service so the page stays same-origin).
        app.MapGet("/arena/frame", async (string cam, DateTime utc, int? w, VRLinkPublisher.Services.CaptureClient cap, CancellationToken ct) =>
        {
            try
            {
                using var resp = await cap.FrameAsync(cam, utc.ToUniversalTime(), w ?? 1280, ct);
                if (!resp.IsSuccessStatusCode) return Results.Problem(await resp.Content.ReadAsStringAsync(ct), statusCode: (int)resp.StatusCode);
                return Results.File(await resp.Content.ReadAsByteArrayAsync(ct), "image/jpeg");
            }
            catch (HttpRequestException ex) { return Results.Problem("Capture service not reachable: " + ex.Message, statusCode: 502); }
        });
        app.MapGet("/media/video/{id:int}", async (int id, IDbContextFactory<PublisherDb> dbf) =>
        {
            await using var db = await dbf.CreateDbContextAsync();
            var c = await db.Clips.FindAsync(id);
            return c != null && File.Exists(c.VideoPath) ? Results.File(c.VideoPath, "video/mp4", enableRangeProcessing: true) : Results.NotFound();
        });

        // Local share pack (stand-in for VRLinkWeb until its API is live): /share/<token> page + files
        app.MapGet("/share/{token}", (string token, Microsoft.Extensions.Options.IOptions<PublisherOptions> o) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-f]{24}$")) return Results.NotFound();
            var dir = Path.Combine(o.Value.ResolveDataDir(), "share", token);
            if (!File.Exists(Path.Combine(dir, "complete"))) return Results.NotFound();
            var items = string.Concat(Directory.GetFiles(dir, "*.mp4").Select(f => Path.GetFileName(f)).Select(f =>
                $"<div class='c'><video src='{token}/{f}' poster='{token}/{Path.ChangeExtension(f, ".jpg")}' controls playsinline></video><a href='{token}/{f}' download>Download</a></div>"));
            var html = "<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>VRLink · your clips</title>" +
                       "<style>body{margin:0;background:#0d1016;color:#fff;font-family:Segoe UI,system-ui,sans-serif;text-align:center}h1{color:#35d0ff}.c{margin:16px auto;max-width:360px}" +
                       "video{width:100%;border-radius:8px;background:#000}a{display:inline-block;margin:8px;color:#000;background:#35d0ff;padding:8px 16px;border-radius:6px;text-decoration:none}</style></head>" +
                       $"<body><h1>VRLink</h1><p>Your session clips (local test share page)</p>{items}<p><a href='https://vrlink'>Book your squad</a></p></body></html>";
            return Results.Content(html, "text/html");
        });
        app.MapGet("/share/{token}/{file}", (string token, string file, Microsoft.Extensions.Options.IOptions<PublisherOptions> o) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-f]{24}$") || file.Contains("..") || file.Contains('/') || file.Contains(Path.DirectorySeparatorChar)) return Results.NotFound();
            var path = Path.Combine(o.Value.ResolveDataDir(), "share", token, file);
            var type = file.EndsWith(".mp4") ? "video/mp4" : file.EndsWith(".jpg") ? "image/jpeg" : null;
            return type != null && File.Exists(path) ? Results.File(path, type, enableRangeProcessing: true) : Results.NotFound();
        });

        static string BaseUrl(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        app.MapGet("/oauth/tiktok/start", async (HttpContext ctx, OAuthService o) =>
        {
            try { return Results.Redirect(await o.TikTokAuthorizeUrlAsync(BaseUrl(ctx))); }
            catch (Exception ex) { return Results.Redirect("/accounts?msg=" + Uri.EscapeDataString(ex.Message)); }
        });
        app.MapGet("/oauth/instagram/start", async (HttpContext ctx, OAuthService o) =>
        {
            try { return Results.Redirect(await o.FacebookAuthorizeUrlAsync(BaseUrl(ctx))); }
            catch (Exception ex) { return Results.Redirect("/accounts?msg=" + Uri.EscapeDataString(ex.Message)); }
        });
        // Registered redirect URIs end with a slash (see README).
        app.MapGet("/oauth/tiktok/callback/", Callback(Platform.TikTok));
        app.MapGet("/oauth/instagram/callback/", Callback(Platform.Instagram));
    }

    private static Func<HttpContext, OAuthService, CancellationToken, Task<IResult>> Callback(Platform p) =>
        async (ctx, o, ct) =>
        {
            var q = ctx.Request.Query;
            string msg;
            try
            {
                msg = p == Platform.TikTok
                    ? await o.TikTokCallbackAsync(q["code"], q["state"], q["error"], ct)
                    : await o.FacebookCallbackAsync(q["code"], q["state"], q["error"], ct);
            }
            catch (Exception ex) { msg = ex.Message; }
            return Results.Redirect("/accounts?msg=" + Uri.EscapeDataString(msg == "ok" ? $"{p} connected." : msg));
        };
}
