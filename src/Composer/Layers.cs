using System.Globalization;
using System.Text.Json;
using VRLink.Shared.Media;

namespace VRLink.Composer;

public sealed record LayerRequest(string DemoPath, string HighlightId, string CalibrationPath, DateTime UtcFrom, DateTime UtcTo, double Fps,
                                  string OutDir, double Scale = 1.0, bool FakeFootage = false, string? Hero = null, bool LightOverlay = false);

public sealed record LayerSet(string Dir, LayerManifest Manifest)
{
    public string Overlay => Path.Combine(Dir, "overlay.mkv");
    public string Pov => Path.Combine(Dir, "pov.mp4");
    public string GameWav => Path.Combine(Dir, "game.wav");
    public string Plate => Path.Combine(Dir, "plate.mp4");

    public static LayerSet Load(string dir)
    {
        var m = JsonSerializer.Deserialize<LayerManifest>(File.ReadAllText(Path.Combine(dir, "layers.json")), HighlightRecord.Json)
                ?? throw new InvalidDataException("layers.json empty");
        return new LayerSet(dir, m);
    }
}

public interface ILayerRenderer
{
    Task<LayerSet> RenderAsync(LayerRequest r, CancellationToken ct);
}

/// <summary>
/// The Unity server exe in -renderlayers mode (built by the Unity Highlights dev):
/// VRCsGoServer.exe -renderlayers &lt;demo&gt; &lt;highlightId&gt; -cam &lt;calib.json&gt; -utcfrom &lt;ISO&gt; -utcto &lt;ISO&gt; -fps &lt;fps&gt; -out &lt;dir&gt; [-scale s] [-fakefootage] -logFile &lt;f&gt;
/// Exit 0 = ok, 2/3 = errors. Runs at BelowNormal priority, only when the ServerGate allows it.
/// </summary>
public sealed class UnityLayerRenderer(string serverExe, TimeSpan timeout) : ILayerRenderer
{
    public static List<string> Args(LayerRequest r, string logFile)
    {
        var a = new List<string>
        {
            "-renderlayers", r.DemoPath, r.HighlightId,
            "-cam", r.CalibrationPath,
            "-utcfrom", r.UtcFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            "-utcto", r.UtcTo.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            "-fps", r.Fps.ToString("0.###", CultureInfo.InvariantCulture),
            "-out", r.OutDir,
        };
        if (Math.Abs(r.Scale - 1) > 1e-6) a.AddRange(["-scale", r.Scale.ToString("0.###", CultureInfo.InvariantCulture)]);
        if (r.FakeFootage) a.Add("-fakefootage");
        if (!string.IsNullOrEmpty(r.Hero)) a.AddRange(["-hero", r.Hero]);   // V3 reactions: client id or "none" (centroid track, no POV)
        if (r.LightOverlay) a.AddRange(["-overlay", "light"]);
        a.AddRange(["-logFile", logFile]);
        return a;
    }

    public async Task<LayerSet> RenderAsync(LayerRequest r, CancellationToken ct)
    {
        Directory.CreateDirectory(r.OutDir);
        var log = Path.Combine(r.OutDir, "renderlayers.log");
        var (code, _, err) = await FfmpegLocator.RunAsync(serverExe, Args(r, log), ct, timeout);
        if (code != 0) throw new InvalidOperationException($"renderlayers exit {code} (see {log}) {err.Trim()}");
        return LayerSet.Load(r.OutDir);
    }
}

/// <summary>
/// Stand-in for the Unity renderer (tests, until -renderlayers exists): same files and layers.json contract, made with ffmpeg lavfi.
/// The "hero" walks an ellipse; the overlay draws a marker on him plus a kill banner at each kill; pov = test pattern; game.wav = shots.
/// </summary>
public sealed class FakeLayerRenderer(string ffmpeg, int cameraW, int cameraH, IReadOnlyList<DateTime> killUtc, string heroLabel = "TU 1") : ILayerRenderer
{
    private static string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Hero position in overlay pixels at time t (seconds from the window start).</summary>
    public static (double X, double Y) Hero(double t, int w, int h) =>
        (w * (0.5 + 0.28 * Math.Cos(0.35 * t)), h * (0.42 + 0.12 * Math.Sin(0.35 * t)));

    public async Task<LayerSet> RenderAsync(LayerRequest r, CancellationToken ct)
    {
        Directory.CreateDirectory(r.OutDir);
        int w = (int)Math.Round(cameraW * r.Scale / 2) * 2, h = (int)Math.Round(cameraH * r.Scale / 2) * 2;
        double dur = (r.UtcTo - r.UtcFrom).TotalSeconds;
        int frames = (int)Math.Round(dur * r.Fps);
        var kills = killUtc.Select(k => (k - r.UtcFrom).TotalSeconds).ToList();
        string font = Fonts.Resolve(null, null).Bold.Replace("\\", "/").Replace(":", "\\:");
        int box = Math.Max(8, h / 20);

        // overlay: transparent canvas, a cyan ring box on the hero, red "KILL" banners at the kills
        var ov = $"color=c=black@0.0:s={w}x{h}:r={N(r.Fps)}:d={N(dur)},format=bgra," +
                 $"drawbox=x='{N(w)}*(0.5+0.28*cos(0.35*t))-{box}':y='{N(h)}*(0.42+0.12*sin(0.35*t))-{box}':w={2 * box}:h={2 * box}:color=0x35d0ff@0.9:t={Math.Max(2, box / 5)}:replace=1";
        foreach (var k in kills)
            ov += $",drawtext=fontfile='{font}':text='KILL':fontsize={h / 14}:fontcolor=0xff4f7b:borderw=3:bordercolor=black:" +
                  $"x='{N(w)}*(0.5+0.28*cos(0.35*t))-text_w/2':y='{N(h)}*(0.42+0.12*sin(0.35*t))-{2 * box}-text_h':enable='between(t,{N(k)},{N(k + 0.8)})'";
        await Run(["-f", "lavfi", "-i", ov, "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-c:v", "ffv1", "-pix_fmt", "bgra", Path.Combine(r.OutDir, "overlay.mkv")], ct);

        // pov (hero first person stand-in)
        await Run(["-f", "lavfi", "-i", $"testsrc2=size=1080x960:rate={N(r.Fps)}:d={N(dur)},drawtext=fontfile='{font}':text='POV':fontsize=120:fontcolor=white:x=(w-text_w)/2:y=(h-text_h)/2",
                   "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Path.Combine(r.OutDir, "pov.mp4")], ct);

        // game audio: quiet ambience + a gunshot-like burst at each kill
        var shots = kills.Count == 0 ? "0" : string.Join("+", kills.Select(k => $"between(t,{N(k - 0.12)},{N(k)})*0.9*sin(2*PI*180*t)"));
        var game = $"aevalsrc='0.03*sin(2*PI*110*t)+({shots})':s=48000:c=stereo:d={N(dur)}";
        await Run(["-f", "lavfi", "-i", game, "-c:a", "pcm_s16le", Path.Combine(r.OutDir, "game.wav")], ct);

        var m = new LayerManifest
        {
            HighlightId = r.HighlightId, UtcStart = r.UtcFrom, Fps = r.Fps, Width = w, Height = h, Frames = frames,
            Hero = new LayerHero { ClientId = 1, Label = heroLabel, Team = "TU" },
            HeroTrack = Enumerable.Range(0, frames).Select(i => { var (x, y) = Hero(i / r.Fps, w, h); return new TrackPoint { X = x, Y = y, Depth = 4, Visible = true }; }).ToList(),
            Moments = killUtc.Select((k, i) => { var (x, y) = Hero((k - r.UtcFrom).TotalSeconds, w, h); return new LayerMoment { Utc = k, Kind = "Kill", VictimLabel = $"SYN {i + 1}", X = x, Y = y }; }).ToList(),
            Players = [new LayerPlayer { Label = heroLabel, Team = "TU" }, new LayerPlayer { Label = "SYN 1", Team = "SYN", IsBot = true }],
        };
        await File.WriteAllTextAsync(Path.Combine(r.OutDir, "layers.json"), JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }), ct);
        return new LayerSet(r.OutDir, m);
    }

    private async Task Run(List<string> args, CancellationToken ct)
    {
        args.InsertRange(0, ["-hide_banner", "-loglevel", "error", "-y"]);
        var (code, _, err) = await FfmpegLocator.RunAsync(ffmpeg, args, ct);
        if (code != 0) throw new InvalidOperationException("fake layers: " + err.Trim());
    }
}
