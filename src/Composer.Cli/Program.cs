using System.Globalization;
using Microsoft.Extensions.Logging;
using VRLink.Composer;
using VRLink.Shared.Demo;
using VRLink.Shared.Media;

// VRLink Composer CLI (manual runs). Examples:
//   vrlink-compose gate
//   vrlink-compose edl --id b1dcee9d-r04-1
//   vrlink-compose compose --id <highlightId> [--capture http://localhost:5090] [--fake-layers | --layers <dir>] [--out <clipsDir>] [--force]
//   vrlink-compose compose --id <highlightId> --layers <dir> --footage <mp4|plate> --out <dir>    (layer pack + footage file, no capture/gate)
//   vrlink-compose fake-layers --out <dir> --from <ISO> --to <ISO> --kills <ISO,ISO,..> [--w 1920 --h 1080 --fps 30 --scale 1]
var a = new Args(args);
using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }).SetMinimumLevel(LogLevel.Information));
var log = lf.CreateLogger("compose");
var opt = new ComposerOptions
{
    HighlightsDir = a.Get("highlights") ?? @"C:\vr\VRCsGo\Builds\Highlights",
    CalibrationDir = a.Get("calib-dir") ?? @"C:\ProgramData\VRLinkPublisher\arena-camera",
    Camera = a.Get("cam") ?? "cam1",
    ClipsDir = a.Get("out") ?? "",
    WorkDir = a.Get("work") ?? "",
    UnityServerExe = a.Get("unity") ?? @"C:\vr\VRCsGo\Builds\Server\VRCsGoServer.exe",
    FontBold = a.Get("font-bold") ?? "", FontRegular = a.Get("font") ?? "",
};

try
{
    switch (a.Command)
    {
        case "gate":
        {
            var s = ServerGate.Read(opt.StateFile);
            var (ok, why) = ServerGate.Evaluate(s, DateTime.UtcNow);
            Console.WriteLine($"{(ok ? "ALLOWED" : "WAIT")}: {why}");
            return ok ? 0 : 1;
        }
        case "edl":
        {
            var h = Find(opt, a.Req("id"));
            var job = new ComposeJob(opt, new NoFootage(), new NoLayers(), log);
            var demo = DemoReader.ReadFile(job.FindDemo(h) ?? throw new FileNotFoundException(h.DemoFileName));
            var (from, to) = job.Window(h, demo);
            var kills = ComposeJob.KillTimes(h).Select(t => (demo.UtcAt(t) - from).TotalSeconds).ToList();
            var edl = EdlBuilder.Build(kills, (to - from).TotalSeconds, 30);
            Console.WriteLine($"window {from:O} .. {to:O}  ({(to - from).TotalSeconds:0.00} s), total {edl.Duration:0.00} s");
            foreach (var s in edl.Segments) Console.WriteLine($"{s.OutStart,7:0.000} +{s.OutDuration,6:0.000}  {s.Kind,-8} src {s.SrcStart,7:0.000}→{s.SrcEnd,7:0.000}  x{s.Speed,4:0.00}  {s.Label}");
            foreach (var k in edl.Kills) Console.WriteLine($"kill @ {k.OutTime:0.000} → {k.CountAfter}");
            return 0;
        }
        case "fake-layers":
        {
            var from = Utc(a.Req("from")); var to = Utc(a.Req("to"));
            var kills = a.Req("kills").Split(',').Select(Utc).ToList();
            var ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null)!;
            var r = new FakeLayerRenderer(ffmpeg, a.Int("w", 1920), a.Int("h", 1080), kills);
            var ls = await r.RenderAsync(new LayerRequest("", a.Get("id") ?? "fake", "", from, to, a.Dbl("fps", 30), a.Req("out"), a.Dbl("scale", 1)), CancellationToken.None);
            Console.WriteLine($"fake layers: {ls.Dir} ({ls.Manifest.Frames} frames)");
            return 0;
        }
        case "compose":
        {
            var h = Find(opt, a.Req("id"));
            if (a.Get("footage") is { } footFile)
            {
                // integration path: real layer pack + a footage file ("plate" = <layers>/plate.mp4), no capture/demo/gate
                var dir = a.Req("layers");
                var foot = footFile == "plate" ? Path.Combine(dir, "plate.mp4") : footFile;
                var out0 = a.Req("out");
                var rr = await new ComposeJob(opt, new NoFootage(), new NoLayers(), log).ComposeFromFilesAsync(h, dir, foot, out0, CancellationToken.None);
                Console.WriteLine(rr.Message);
                foreach (var c in rr.Clips) Console.WriteLine("  " + c);
                foreach (var n in rr.Notes) Console.WriteLine("  note: " + n);
                return 0;
            }
            if (!a.Has("force"))
            {
                var (ok, why) = ServerGate.Evaluate(ServerGate.Read(opt.StateFile), DateTime.UtcNow);
                if (!ok) { Console.WriteLine($"WAIT: {why} (use --force for a manual run)"); return 1; }
            }
            var http = new HttpClient { BaseAddress = new Uri((a.Get("capture") ?? "http://localhost:5090").TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
            var footage = new HttpFootageSource(http);
            ILayerRenderer layers = new UnityLayerRenderer(opt.UnityServerExe, TimeSpan.FromMinutes(opt.LayerTimeoutMinutes));
            if (a.Has("fake-layers"))
            {
                // kills (UTC) from the demo
                var tmp = new ComposeJob(opt, footage, layers, log);
                var demo = DemoReader.ReadFile(tmp.FindDemo(h) ?? throw new FileNotFoundException(h.DemoFileName));
                var cal = tmp.CalibrationPath(demo.V3?.VenueId ?? "");
                var pose = System.Text.Json.JsonSerializer.Deserialize<VRLink.Shared.Calibration.CameraCalibration>(File.ReadAllText(cal))!.Pose;
                layers = new FakeLayerRenderer(FfmpegLocator.Find("ffmpeg.exe", null)!, pose.Width, pose.Height, ComposeJob.KillTimes(h).Select(demo.UtcAt).ToList(), h.HeroLabel);
            }
            var r = await new ComposeJob(opt, footage, layers, log).RunAsync(h, CancellationToken.None, a.Get("layers"));
            Console.WriteLine(r.Message);
            foreach (var c in r.Clips) Console.WriteLine("  " + c);
            foreach (var n in r.Notes) Console.WriteLine("  note: " + n);
            return r.Ok ? 0 : 2;
        }
        default:
            Console.WriteLine("commands: gate | edl --id X | compose --id X [--capture URL] [--fake-layers|--layers DIR] [--out DIR] [--force] | fake-layers --out DIR --from ISO --to ISO --kills ISO,..");
            return 64;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 3;
}

static HighlightRecord Find(ComposerOptions opt, string id) =>
    HighlightFeed.ReadAll(opt.HighlightsDir).LastOrDefault(h => h.Id == id) ?? throw new KeyNotFoundException($"highlight {id} not in {opt.HighlightsDir}");

static DateTime Utc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

sealed class Args(string[] a)
{
    public string Command => a.Length > 0 ? a[0] : "";
    public string? Get(string k) { int i = Array.IndexOf(a, "--" + k); return i >= 0 && i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[i + 1] : null; }
    public bool Has(string k) => a.Contains("--" + k);
    public string Req(string k) => Get(k) ?? throw new ArgumentException($"--{k} missing");
    public int Int(string k, int d) => int.TryParse(Get(k), out var v) ? v : d;
    public double Dbl(string k, double d) => double.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
}

sealed class NoFootage : IFootageSource
{
    public Task GetWindowAsync(string cam, DateTime f, DateTime t, string o, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> PinAsync(string? cam, DateTime f, DateTime t, string r, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task ReleaseAsync(string id, CancellationToken ct) => Task.CompletedTask;
}

sealed class NoLayers : ILayerRenderer
{
    public Task<LayerSet> RenderAsync(LayerRequest r, CancellationToken ct) => throw new NotSupportedException();
}
