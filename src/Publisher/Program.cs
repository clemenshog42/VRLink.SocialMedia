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
