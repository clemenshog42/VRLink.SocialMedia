using Microsoft.Extensions.Hosting.WindowsServices;
using VRLink.Capture;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "VRLinkCapture");
var opts = builder.Configuration.GetSection("Capture").Get<CaptureOptions>() ?? new CaptureOptions();
builder.WebHost.UseUrls(opts.Urls); // localhost only: the API has no authentication
builder.Services.AddCaptureCore(o => builder.Configuration.GetSection("Capture").Bind(o));

var app = builder.Build();
app.MapCaptureApi();
app.Run();

public static class CaptureSetup
{
    public static IServiceCollection AddCaptureCore(this IServiceCollection s, Action<CaptureOptions> configure)
    {
        s.Configure(configure);
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<CaptureHost>();
        s.AddHostedService(sp => sp.GetRequiredService<CaptureHost>());
        s.AddSingleton<FootageService>();
        s.AddSingleton<ClapSyncService>();
        return s;
    }

    /// <summary>
    /// GET  /health                         200 when every camera records (last segment fresh), else 503
    /// GET  /api/cameras                    status per camera (fps, dropped frames, last segment age, restarts)
    /// GET  /api/devices                    DirectShow devices (ffmpeg -list_devices)
    /// GET  /api/segments?cam=&amp;from=&amp;to=       index entries
    /// GET  /api/pins | POST /api/pins {camera?, fromUtc, toUtc, reason} | DELETE /api/pins/{id}
    /// GET  /api/footage?cam=&amp;from=&amp;to=        mp4 of the window (true UTC; camera offset applied)
    /// GET  /api/frame?cam=&amp;utc=[&amp;w=]           jpeg at true UTC
    /// GET  /api/offsets | PUT /api/offsets/{cam}?ms=   clap-test offset (footage − demo)
    /// POST /api/clapsync?cam=&amp;demo=&lt;path&gt;&amp;save=true   run the clap test on a demo + the raw camera audio
    /// </summary>
    public static void MapCaptureApi(this WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/health"));
        app.MapGet("/health", (CaptureHost h) =>
        {
            var body = h.Health();
            bool ok = (bool)body.GetType().GetProperty("ok")!.GetValue(body)!;
            return Results.Json(body, statusCode: ok ? 200 : 503);
        });
        app.MapGet("/api/cameras", (CaptureHost h) => h.Recorders.Select(r => r.Status()));
        app.MapGet("/api/devices", async (CaptureHost h, CancellationToken ct) => await DeviceListParser.ListAsync(h.Ffmpeg, ct));
        app.MapGet("/api/segments", (CaptureHost h, string cam, DateTime? from, DateTime? to) =>
        {
            var r = h.Recorder(cam);
            if (r == null) return Results.NotFound();
            var f = from?.ToUniversalTime() ?? DateTime.MinValue; var t = to?.ToUniversalTime() ?? DateTime.MaxValue;
            return Results.Ok(r.Index.Overlapping(f, t));
        });
        app.MapGet("/api/pins", (CaptureHost h) => h.Pins.All());
        app.MapPost("/api/pins", (CaptureHost h, PinRequest p, TimeProvider time) =>
        {
            try { return Results.Ok(h.Pins.Add(p.Camera, p.FromUtc.ToUniversalTime(), p.ToUtc.ToUniversalTime(), p.Reason ?? "", time.GetUtcNow().UtcDateTime)); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapDelete("/api/pins/{id}", (CaptureHost h, string id) => h.Pins.Release(id) ? Results.Ok() : Results.NotFound());
        app.MapGet("/api/footage", async (FootageService f, string cam, DateTime from, DateTime to, CancellationToken ct) =>
        {
            try
            {
                var w = await f.GetWindowAsync(cam, from.ToUniversalTime(), to.ToUniversalTime(), null, ct);
                return Results.File(w.Path, "video/mp4", Path.GetFileName(w.Path), enableRangeProcessing: true);
            }
            catch (FootageException ex) { return Results.Problem(ex.Message, statusCode: ex.HttpStatus); }
        });
        app.MapGet("/api/frame", async (FootageService f, string cam, DateTime utc, int? w, CancellationToken ct) =>
        {
            try { return Results.File(await f.GetFrameJpegAsync(cam, utc.ToUniversalTime(), w ?? 1920, ct), "image/jpeg"); }
            catch (FootageException ex) { return Results.Problem(ex.Message, statusCode: ex.HttpStatus); }
        });
        app.MapGet("/api/offsets", (CaptureHost h) => h.Offsets.All());
        app.MapPost("/api/clapsync", async (ClapSyncService c, string cam, string demo, bool? save, CancellationToken ct) =>
        {
            if (!File.Exists(demo) || !demo.EndsWith(".vrdemo", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest("demo must be an existing .vrdemo file");
            try { return Results.Ok(await c.RunAsync(cam, demo, save ?? false, ct)); }
            catch (FootageException ex) { return Results.Problem(ex.Message, statusCode: ex.HttpStatus); }
        });
        app.MapPut("/api/offsets/{cam}", (CaptureHost h, string cam, double ms) =>
        {
            if (h.Recorder(cam) == null) return Results.NotFound();
            h.Offsets.Set(cam, ms);
            return Results.Ok(h.Offsets.All());
        });
    }
}

public sealed record PinRequest(string? Camera, DateTime FromUtc, DateTime ToUtc, string? Reason);
