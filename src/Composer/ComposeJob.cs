using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRLink.Shared.Calibration;
using VRLink.Shared.Contracts;
using VRLink.Shared.Demo;
using VRLink.Shared.Media;

namespace VRLink.Composer;

public sealed class ComposerOptions
{
    public bool Enabled { get; set; } = true;
    public string HighlightsDir { get; set; } = @"C:\vr\VRCsGo\Builds\Highlights";
    /// <summary>Default: &lt;HighlightsDir&gt;\demos (searched recursively).</summary>
    public string DemosDir { get; set; } = "";
    /// <summary>Default: &lt;HighlightsDir&gt;\clips.</summary>
    public string ClipsDir { get; set; } = "";
    /// <summary>Default: &lt;HighlightsDir&gt;\server_state.json.</summary>
    public string ServerStateFile { get; set; } = "";
    /// <summary>Venue calibrations saved by the Publisher's Arena camera page.</summary>
    public string CalibrationDir { get; set; } = @"C:\ProgramData\VRLinkPublisher\arena-camera";
    public string Camera { get; set; } = "cam1";
    public string UnityServerExe { get; set; } = @"C:\vr\VRCsGo\Builds\Server\VRCsGoServer.exe";
    public int LayerTimeoutMinutes { get; set; } = 15;
    /// <summary>Overlay resolution factor passed to -renderlayers (1 = camera resolution).</summary>
    public double LayerScale { get; set; } = 1.0;
    /// <summary>Work folder (layers, intermediate files, composer_state.json). Default %ProgramData%\VRLinkComposer.</summary>
    public string WorkDir { get; set; } = "";
    public string FontBold { get; set; } = "";
    public string FontRegular { get; set; } = "";
    /// <summary>Lead-in before the first kill and tail after the last that the footage/layers window covers.</summary>
    public double WindowPreSec { get; set; } = 8;
    public double WindowPostSec { get; set; } = 5;
    public int PollSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>V3: detect + compose up to 3 reaction clips per match (room-audio peaks, jumps, dives, celebrations).</summary>
    public bool ReactionsEnabled { get; set; } = true;

    public string Demos => string.IsNullOrWhiteSpace(DemosDir) ? Path.Combine(HighlightsDir, "demos") : DemosDir;
    public string Clips => string.IsNullOrWhiteSpace(ClipsDir) ? Path.Combine(HighlightsDir, "clips") : ClipsDir;
    public string StateFile => string.IsNullOrWhiteSpace(ServerStateFile) ? Path.Combine(HighlightsDir, "server_state.json") : ServerStateFile;
    public string Work => string.IsNullOrWhiteSpace(WorkDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRLinkComposer") : WorkDir;
}

/// <summary>Footage access (Capture service in-process or over HTTP).</summary>
public interface IFootageSource
{
    /// <summary>Cuts [from, to) (true UTC; camera offset applied) into outPath. Throws when there is no (complete) footage.</summary>
    Task GetWindowAsync(string cam, DateTime fromUtc, DateTime toUtc, string outPath, CancellationToken ct);
    Task<string?> PinAsync(string? cam, DateTime fromUtc, DateTime toUtc, string reason, CancellationToken ct);
    Task ReleaseAsync(string pinId, CancellationToken ct);
    /// <summary>Mono float audio of the AVAILABLE part of [from, to) (V3 reactions); StartUtc = UTC of sample 0.</summary>
    Task<(float[] Samples, DateTime StartUtc)> GetAudioAsync(string cam, DateTime fromUtc, DateTime toUtc, int rate, CancellationToken ct) =>
        throw new NotSupportedException("audio not available from this footage source");
}

/// <summary>Capture service over HTTP (CLI use).</summary>
public sealed class HttpFootageSource(HttpClient http) : IFootageSource
{
    public async Task GetWindowAsync(string cam, DateTime fromUtc, DateTime toUtc, string outPath, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"api/footage?cam={Uri.EscapeDataString(cam)}&from={Uri.EscapeDataString(fromUtc.ToString("O"))}&to={Uri.EscapeDataString(toUtc.ToString("O"))}",
                                             HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"footage: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        await using var fs = File.Create(outPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    public async Task<string?> PinAsync(string? cam, DateTime fromUtc, DateTime toUtc, string reason, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync("api/pins", new { camera = cam, fromUtc, toUtc, reason }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return j.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public async Task ReleaseAsync(string pinId, CancellationToken ct) => await http.DeleteAsync($"api/pins/{pinId}", ct);

    public async Task<(float[] Samples, DateTime StartUtc)> GetAudioAsync(string cam, DateTime fromUtc, DateTime toUtc, int rate, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"api/audio?cam={Uri.EscapeDataString(cam)}&from={Uri.EscapeDataString(fromUtc.ToString("O"))}&to={Uri.EscapeDataString(toUtc.ToString("O"))}&rate={rate}", ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"audio: HTTP {(int)resp.StatusCode}");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var start = resp.Headers.TryGetValues("X-Start-Utc", out var v) ? DateTime.Parse(v.First(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) : fromUtc;
        return (System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray(), start);
    }
}

public sealed record ComposeResult(bool Ok, string Message, List<string> Clips, List<string> Notes);

/// <summary>
/// One highlight → "&lt;id&gt;-irl" (+ "&lt;id&gt;-split" for Ace/Kills4/Kills3/Clutch) in the clip contract.
/// Steps: demo (v3, UTC) → calibration → footage window → layers (Unity -renderlayers or fake) → EDL → reframe → audio → frames → clip files.
/// Fallback: no calibration / no footage / no v3 demo → nothing (the Unity game-only clip already exists).
/// </summary>
public sealed class ComposeJob(ComposerOptions opt, IFootageSource footage, ILayerRenderer layers, ILogger log)
{
    private readonly string _ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null) ?? throw new InvalidOperationException("ffmpeg not found");
    private readonly string _ffprobe = FfmpegLocator.Find("ffprobe.exe", null) ?? throw new InvalidOperationException("ffprobe not found");

    public string? FindDemo(HighlightRecord h) =>
        Directory.Exists(opt.Demos) ? Directory.EnumerateFiles(opt.Demos, h.DemoFileName, SearchOption.AllDirectories).FirstOrDefault() : null;

    public string CalibrationPath(string venue) => Path.Combine(opt.CalibrationDir, Safe(venue), Safe(opt.Camera) + ".json");
    private static string Safe(string s) => string.Concat((string.IsNullOrWhiteSpace(s) ? "default" : s).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    /// <summary>Window (true UTC) the footage, overlay, pov and game audio are cut to.</summary>
    public (DateTime From, DateTime To) Window(HighlightRecord h, DemoData d)
    {
        var kills = KillTimes(h);
        double from = Math.Max(h.RoundStart, kills.Min() - opt.WindowPreSec), to = Math.Min(d.EndTime, kills.Max() + opt.WindowPostSec);
        if (to <= from) to = kills.Max() + opt.WindowPostSec;
        return (d.UtcAt(from), d.UtcAt(to));
    }

    public static List<double> KillTimes(HighlightRecord h)
    {
        var k = h.Moments.Where(m => m.Kind is "Kill" or "Grenade" or "Defuse").Select(m => m.T).ToList();
        return k.Count > 0 ? k : [h.ClipEnd - 1];
    }

    public async Task<ComposeResult> RunAsync(HighlightRecord h, CancellationToken ct, string? existingLayersDir = null)
    {
        var notes = new List<string>();
        var demoPath = FindDemo(h);
        if (demoPath == null) return new(false, $"skip: demo {h.DemoFileName} not found", [], notes);
        var demo = DemoReader.ReadFile(demoPath);
        if (!demo.HasUtcAnchor) return new(false, "skip: demo has no UTC anchor (v1/v2) — game-only clip stays", [], notes);
        var calibPath = CalibrationPath(demo.V3!.VenueId);
        if (!File.Exists(calibPath)) return new(false, $"skip: no camera calibration for venue '{demo.V3.VenueId}' ({calibPath})", [], notes);
        var calib = JsonSerializer.Deserialize<CameraCalibration>(File.ReadAllText(calibPath))!;

        var (from, to) = Window(h, demo);
        var work = Path.Combine(opt.Work, "jobs", h.Id);
        Directory.CreateDirectory(work);
        var footagePath = Path.Combine(work, "footage.mp4");
        try { await footage.GetWindowAsync(opt.Camera, from, to, footagePath, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(false, "skip: no footage for the window — " + ex.Message, [], notes); }
        var fInfo = await Probe.RunAsync(_ffprobe, footagePath, ct);
        double fps = fInfo.Fps > 1 ? fInfo.Fps : 30;

        LayerSet ls = existingLayersDir != null
            ? LayerSet.Load(existingLayersDir)
            : await layers.RenderAsync(new LayerRequest(demoPath, h.Id, calibPath, from, to, fps, Path.Combine(work, "layers"), opt.LayerScale), ct);

        var produced = await ComposeAsync(h, ls, footagePath, fInfo, from, calib, Path.Combine(opt.Clips, DateTime.UtcNow.ToString("yyyy-MM-dd")), work, notes, ct);
        return new(true, $"composed {produced.Count} clip(s)", produced, notes);
    }

    /// <summary>
    /// Manual/integration path: an existing layer pack + a footage file (e.g. the renderer's plate.mp4 stand-in, silent room audio).
    /// No demo, calibration, footage service or gate involved; frame 0 of the footage = layers.json utcStart.
    /// </summary>
    public async Task<ComposeResult> ComposeFromFilesAsync(HighlightRecord h, string layersDir, string footagePath, string clipsDir, CancellationToken ct)
    {
        var notes = new List<string>();
        var ls = LayerSet.Load(layersDir);
        if (ls.Manifest.ClockExact == false) notes.Add("layers: clockExact=false (demo v1/v2, approximate UTC)");
        var fInfo = await Probe.RunAsync(_ffprobe, footagePath, ct);
        if (Math.Abs(fInfo.Frames - ls.Manifest.Frames) > 1) notes.Add($"footage has {fInfo.Frames} frames, layers {ls.Manifest.Frames}");
        if (!fInfo.HasAudio) notes.Add("footage has no audio: room audio silent");
        var work = Path.Combine(opt.Work, "jobs", h.Id + "-files");
        Directory.CreateDirectory(work);
        bool plate = string.Equals(Path.GetFileName(footagePath), "plate.mp4", StringComparison.OrdinalIgnoreCase);
        var clips = await ComposeAsync(h, ls, footagePath, fInfo, ls.Manifest.UtcStart, null, clipsDir, work, notes, ct,
                                       plate ? "plate" : "footage:" + Path.GetFileName(footagePath));
        return new(true, $"composed {clips.Count} clip(s)", clips, notes);
    }

    /// <summary>Compose from ready inputs (also used by the tests).</summary>
    /// <param name="footageLabel">Source label of the footage: "arenaCam:&lt;cam&gt;" (default) or "plate" (game render stand-in).</param>
    public async Task<List<string>> ComposeAsync(HighlightRecord h, LayerSet ls, string footagePath, MediaInfo fInfo, DateTime windowFromUtc,
                                                 CameraCalibration? calib, string clipsDir, string work, List<string> notes, CancellationToken ct,
                                                 string? footageLabel = null)
    {
        double fps = fInfo.Fps > 1 ? fInfo.Fps : 30;
        var oInfo = await Probe.RunAsync(_ffprobe, ls.Overlay, ct);
        MediaInfo? pInfo = File.Exists(ls.Pov) ? await Probe.RunAsync(_ffprobe, ls.Pov, ct) : null;
        double windowSec = Math.Min(fInfo.Frames, oInfo.Frames) / fps;

        // kill times in source seconds: prefer the renderer's moments (UTC), else the highlight moments via the layer start
        var killSrc = ls.Manifest.Moments.Count > 0
            ? ls.Manifest.Moments.Where(m => m.Kind is "Kill" or "Grenade" or "Defuse").Select(m => (m.Utc - windowFromUtc).TotalSeconds).ToList()
            : new List<double>();
        if (killSrc.Count == 0) throw new InvalidOperationException("no kill moments in layers.json");
        var victims = ls.Manifest.Moments.Select(m => m.VictimLabel).ToList();
        var edl = EdlBuilder.Build(killSrc, windowSec, fps, victims);
        notes.AddRange(edl.Notes);
        notes.Add($"edl: {edl.Duration:0.000} s, kills at " + string.Join(", ", edl.Kills.Select(k => $"{k.OutTime:0.000}s→{k.CountAfter}")) + $", end card {edl.EndCardStart:0.000}s");

        // hero track in camera pixels
        double sx = (double)fInfo.Width / ls.Manifest.Width, sy = (double)fInfo.Height / ls.Manifest.Height;
        var track = ls.Manifest.HeroTrack.Select(p => p with { X = p.X * sx, Y = p.Y * sy }).ToList();

        var fonts = Fonts.Resolve(opt.FontBold, opt.FontRegular);
        var audio = await AudioBuilder.RenderAsync(_ffmpeg, fInfo.HasAudio ? footagePath : null, ls.GameWav, edl,
                                                   Math.Round(edl.Duration * fps) / fps, Path.Combine(work, "mix.wav"), ct, log: n => { notes.Add(n); log.LogInformation("[Composer] {Id}: {N}", h.Id, n); });
        var inputs = new ComposeInputs(footagePath, fInfo, ls.Overlay, oInfo, pInfo != null ? ls.Pov : null, pInfo);
        var outputs = new List<string>();
        var formats = h.WantsSplit && pInfo != null ? new[] { "irl", "split" } : new[] { "irl" };
        Directory.CreateDirectory(clipsDir);
        foreach (var fmt in formats)
        {
            bool split = fmt == "split";
            var rf = Reframer.Compute(track, fInfo.Width, fInfo.Height, fps, killSrc,
                                      new ReframeOptions { Aspect = split ? 1080.0 / 960 : 9.0 / 16, OutHeight = split ? 960 : 1920 });
            foreach (var w in rf.Warnings) { notes.Add($"{fmt}: {w}"); log.LogWarning("[Composer] {Id} {Fmt}: {W}", h.Id, fmt, w); }
            string id = $"{h.Id}-{fmt}";
            var textFilter = TextPlan.Build(h, edl, split, fonts, Path.Combine(work, "text-" + fmt));
            var tmpMp4 = Path.Combine(work, id + ".mp4");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int frames = await new Compositor(_ffmpeg).RenderAsync(inputs, edl, rf.Crops, split, textFilter, audio, tmpMp4, fps, ct);
            log.LogInformation("[Composer] {Id}: {Frames} frames in {S:0.0}s", id, frames, sw.Elapsed.TotalSeconds);
            var sources = new List<string> { footageLabel ?? $"arenaCam:{opt.Camera}", "overlay" };
            if (split) sources.Add("pov");
            sources.Add("gameAudio");
            if (fInfo.HasAudio) sources.Add("roomAudio");
            outputs.Add(await WriteClipAsync(h, id, fmt, tmpMp4, frames / fps, ls, sources, clipsDir, ct));
        }
        return outputs;
    }

    /// <summary>mp4 + jpg first, json last (temp + rename) — the Publisher imports only complete clips.</summary>
    private async Task<string> WriteClipAsync(HighlightRecord h, string id, string fmt, string tmpMp4, double duration, LayerSet ls, List<string> sources, string dir, CancellationToken ct)
    {
        var mp4 = Path.Combine(dir, id + ".mp4");
        File.Copy(tmpMp4, mp4 + ".tmp", true);
        File.Move(mp4 + ".tmp", mp4, true);
        var jpg = Path.Combine(dir, id + ".jpg");
        await FfmpegLocator.RunAsync(_ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0.6", "-i", mp4, "-frames:v", "1", "-q:v", "3", jpg], ct);

        // reuse the game-only clip's caption/hashtags/venue if the Unity renderer already wrote one
        ClipManifest? game = null;
        var gameDirs = new[] { opt.Clips, Path.Combine(opt.HighlightsDir, "clips") }.Distinct().Where(Directory.Exists);
        foreach (var f in gameDirs.SelectMany(d => Directory.EnumerateFiles(d, h.Id + ".json", SearchOption.AllDirectories)))
        {
            try { game = JsonSerializer.Deserialize<ClipManifest>(File.ReadAllText(f), ClipManifest.JsonOptions); break; } catch { }
        }
        var m = new ClipManifest
        {
            HighlightId = id, Type = h.Type, Score = h.Score, MatchId = h.MatchId, Round = h.Round,
            DurationSec = Math.Round(duration, 3), Width = 1080, Height = 1920,
            Caption = game?.Caption ?? HookTemplates.For(h) + " Free-roam VR at VRLink. Book your squad.",
            Hashtags = game?.Hashtags ?? ["#VRLink", "#vr", "#freeroamvr", "#vrgaming", "#" + h.Type.ToLowerInvariant()],
            Venue = game?.Venue, CreatedUtc = DateTime.UtcNow, HeroIsBot = h.HeroIsBot,
            Format = fmt, MusicFree = true, Title = h.Type,
            Sources = sources,
            Players = ls.Manifest.Players.Select(p => new ClipPlayer { Label = p.Label, Team = p.Team, IsBot = p.IsBot, Hero = p.Label == ls.Manifest.Hero.Label }).ToList(),
            Video = id + ".mp4",
        };
        var json = Path.Combine(dir, id + ".json");
        await File.WriteAllTextAsync(json + ".tmp", JsonSerializer.Serialize(m, ClipManifest.JsonOptions), ct);
        File.Move(json + ".tmp", json, true);
        return json;
    }
}
