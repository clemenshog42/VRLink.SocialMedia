using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VRLink.Shared.Calibration;
using VRLink.Shared.Contracts;
using VRLink.Shared.Demo;
using VRLink.Shared.Media;

namespace VRLink.Composer;

/// <summary>A detected reaction (V3). Hero = "none" (centroid, no POV) or the single involved player's client id.</summary>
public sealed record ReactionRecord(
    string Id, string MatchId, int Round, string Kind, DateTime PeakUtc, DateTime FromUtc, DateTime ToUtc, double Score,
    string Hero, List<ulong> Players, List<string> PlayerLabels, bool AllBots, string Hook, string? DemoPath, string Venue);

/// <summary>
/// V3 reaction clips for one match/session: loud room-audio peaks (camera mic) + jumps/dives/celebrations from the session's
/// round demos → max 3 scored reactions → short IRL clip each (no cold open, slow-mo + punch on the peak, room audio prominent,
/// game −10 dB, light overlay from Unity with -hero none|id -overlay light). Format "reaction", type "Reaction".
/// </summary>
public sealed class ReactionJob(ComposerOptions opt, IFootageSource footage, ILayerRenderer layers, ILogger log, ReactionOptions? ro = null)
{
    private readonly ReactionOptions _o = ro ?? new ReactionOptions();
    private readonly string _ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null) ?? throw new InvalidOperationException("ffmpeg not found");
    private readonly string _ffprobe = FfmpegLocator.Find("ffprobe.exe", null) ?? throw new InvalidOperationException("ffprobe not found");
    private static double Sec(DateTime t) => (t - DateTime.UnixEpoch).TotalSeconds;

    public List<(string Path, DemoData Demo)> SessionDemos(string matchId) =>
        !Directory.Exists(opt.Demos) ? new()
        : Directory.EnumerateFiles(opt.Demos, matchId + "_r*" + DemoFormat.Extension, SearchOption.AllDirectories)
            .Select(p => { try { return (p, DemoReader.ReadFile(p)); } catch { return (p, (DemoData?)null); } })
            .Where(x => x.Item2 is { HasUtcAnchor: true } && x.Item2.V3?.IsCalibration != true)
            .Select(x => (x.p, x.Item2!)).OrderBy(x => x.Item2.StartTime).ToList();

    public async Task<(List<ReactionRecord> Reactions, List<string> Notes)> DetectAsync(string matchId, CancellationToken ct)
    {
        var notes = new List<string>();
        var demos = SessionDemos(matchId);
        if (demos.Count == 0) return (new(), ["no v3 round demos for the match"]);
        var from = demos.Min(d => d.Demo.UtcAt(d.Demo.StartTime)); var to = demos.Max(d => d.Demo.UtcAt(d.Demo.EndTime));
        string venue = demos[0].Demo.V3!.VenueId;

        var audioPeaks = new List<AudioPeak>();
        try
        {
            var (samples, start) = await footage.GetAudioAsync(opt.Camera, from, to, 16000, ct);
            audioPeaks = LoudnessPeaks.Detect(samples, 16000, Sec(start), _o);
            notes.Add($"audio {samples.Length / 16000.0:0} s analysed, {audioPeaks.Count} peak(s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add("no room audio: " + ex.Message); }

        var body = demos.SelectMany(d => BodyEvents.Detect(d.Demo, _o)).ToList();
        notes.Add($"body events: {body.Count(b => b.Kind == BodyEventKind.Jump)} jump(s), {body.Count(b => b.Kind == BodyEventKind.Dive)} dive(s), {body.Count(b => b.Kind == BodyEventKind.Celebration)} celebration(s)");

        Vector3? cam = null;
        var calibPath = Path.Combine(opt.CalibrationDir, Safe(venue), Safe(opt.Camera) + ".json");
        if (File.Exists(calibPath)) cam = JsonSerializer.Deserialize<CameraCalibration>(File.ReadAllText(calibPath))!.Pose.Position;
        ulong? Nearest(double utcSec)
        {
            if (cam == null) return null;
            var t = DateTime.UnixEpoch.AddSeconds(utcSec);
            var d = demos.FirstOrDefault(x => x.Demo.UtcAt(x.Demo.StartTime) <= t && t <= x.Demo.UtcAt(x.Demo.EndTime)).Demo;
            return d == null ? null : ReactionPlanner.Nearest(d, d.ServerTimeAt(t), cam.Value);
        }

        var plan = ReactionPlanner.Plan(audioPeaks, body, _o, Nearest);
        var list = new List<ReactionRecord>();
        int n = 0;
        foreach (var c in plan)
        {
            var peak = c.PeakUtc;
            var demo = demos.FirstOrDefault(x => x.Demo.UtcAt(x.Demo.StartTime) <= peak && peak <= x.Demo.UtcAt(x.Demo.EndTime));
            var allPlayers = demos.SelectMany(d => d.Demo.Players).GroupBy(p => p.ClientId).ToDictionary(g => g.Key, g => g.First());
            var labels = c.Players.Select(id => allPlayers.TryGetValue(id, out var p) ? p.Label : id.ToString(CultureInfo.InvariantCulture)).ToList();
            bool allBots = c.Players.Count > 0 && c.Players.All(id => allPlayers.TryGetValue(id, out var p) && p.IsBot);
            string id = $"{(matchId.Length > 8 ? matchId[..8] : matchId)}-rx{++n}";
            list.Add(new ReactionRecord(id, matchId, demo.Demo?.Round ?? 0, c.Kind, peak, peak.AddSeconds(-_o.PreSec), peak.AddSeconds(_o.PostSec), c.Score,
                c.Players.Count == 1 ? c.Players[0].ToString(CultureInfo.InvariantCulture) : "none", c.Players, labels, allBots,
                ReactionPlanner.Hook(c.Kind), demo.Path, venue));
        }
        return (list, notes);
    }

    private static string Safe(string s) => string.Concat((string.IsNullOrWhiteSpace(s) ? "default" : s).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    public async Task<ComposeResult> RunAsync(string matchId, CancellationToken ct)
    {
        var (rx, notes) = await DetectAsync(matchId, ct);
        var clips = new List<string>();
        foreach (var r in rx)
        {
            try { clips.Add(await ComposeAsync(r, Path.Combine(opt.Clips, DateTime.UtcNow.ToString("yyyy-MM-dd")), notes, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add($"{r.Id}: {ex.Message}"); log.LogWarning(ex, "[Reactions] {Id} failed", r.Id); }
        }
        return new(clips.Count > 0, $"{rx.Count} reaction(s), {clips.Count} clip(s)", clips, notes);
    }

    public async Task<string> ComposeAsync(ReactionRecord r, string clipsDir, List<string> notes, CancellationToken ct)
    {
        var work = Path.Combine(opt.Work, "reactions", r.Id);
        Directory.CreateDirectory(work);
        var footagePath = Path.Combine(work, "footage.mp4");
        await footage.GetWindowAsync(opt.Camera, r.FromUtc, r.ToUtc, footagePath, ct);
        var fInfo = await Probe.RunAsync(_ffprobe, footagePath, ct);
        double fps = fInfo.Fps > 1 ? fInfo.Fps : 30;

        LayerSet? ls = null;
        var calib = Path.Combine(opt.CalibrationDir, Safe(r.Venue), Safe(opt.Camera) + ".json");
        if (r.DemoPath != null && File.Exists(calib))
        {
            try { ls = await layers.RenderAsync(new LayerRequest(r.DemoPath, r.Id, calib, r.FromUtc, r.ToUtc, fps, Path.Combine(work, "layers"), opt.LayerScale, Hero: r.Hero, LightOverlay: true), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add($"{r.Id}: no overlay ({ex.Message})"); }
        }
        ls ??= await EmptyLayersAsync(Path.Combine(work, "empty"), r, fInfo, fps, ct);
        var oInfo = await Probe.RunAsync(_ffprobe, ls.Overlay, ct);

        double windowSec = Math.Min(fInfo.Frames, oInfo.Frames) / fps;
        double peakSrc = (r.PeakUtc - r.FromUtc).TotalSeconds;
        var edl = ReactionEdl.Build(peakSrc, windowSec, fps);
        notes.AddRange(edl.Notes.Select(x => $"{r.Id}: {x}"));
        double sx = (double)fInfo.Width / ls.Manifest.Width, sy = (double)fInfo.Height / ls.Manifest.Height;
        var track = ls.Manifest.HeroTrack.Select(p => p with { X = p.X * sx, Y = p.Y * sy }).ToList();
        if (track.Count == 0) track = Enumerable.Repeat(new TrackPoint { X = fInfo.Width / 2.0, Y = fInfo.Height * 0.45, Visible = true }, (int)Math.Round(windowSec * fps)).ToList();
        var rf = Reframer.Compute(track, fInfo.Width, fInfo.Height, fps, [peakSrc]);
        foreach (var w in rf.Warnings) notes.Add($"{r.Id}: {w}");

        double total = Math.Round(edl.Duration * fps) / fps;
        var audio = await AudioBuilder.RenderAsync(_ffmpeg, fInfo.HasAudio ? footagePath : null, ls.GameWav, edl, total, Path.Combine(work, "mix.wav"), ct, gameGainDb: -10);
        var pseudo = new HighlightRecord { Id = r.Id, Type = "Reaction", MatchId = r.MatchId, Round = r.Round };
        var text = TextPlan.Build(pseudo, edl, false, Fonts.Resolve(opt.FontBold, opt.FontRegular), Path.Combine(work, "text"), hook: r.Hook);
        var tmp = Path.Combine(work, r.Id + ".mp4");
        int frames = await new Compositor(_ffmpeg).RenderAsync(new ComposeInputs(footagePath, fInfo, ls.Overlay, oInfo, null, null), edl, rf.Crops, false, text, audio, tmp, fps, ct);

        Directory.CreateDirectory(clipsDir);
        var mp4 = Path.Combine(clipsDir, r.Id + ".mp4");
        File.Copy(tmp, mp4 + ".tmp", true); File.Move(mp4 + ".tmp", mp4, true);
        await FfmpegLocator.RunAsync(_ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0.6", "-i", mp4, "-frames:v", "1", "-q:v", "3", Path.Combine(clipsDir, r.Id + ".jpg")], ct);
        bool hasOverlay = ls.Manifest.HighlightId != "empty";
        var sources = new List<string> { $"arenaCam:{opt.Camera}" };
        if (hasOverlay) sources.AddRange(["overlay", "gameAudio"]);
        if (fInfo.HasAudio) sources.Add("roomAudio");
        var m = new ClipManifest
        {
            HighlightId = r.Id, Type = "Reaction", Score = Math.Round(r.Score, 1), MatchId = r.MatchId, Round = r.Round,
            DurationSec = Math.Round(frames / fps, 3), Width = 1080, Height = 1920,
            Caption = r.Hook + " Free-roam VR at VRLink. Book your squad.", Hashtags = ["#VRLink", "#vr", "#freeroamvr", "#vrgaming", "#reaction"],
            Venue = r.Venue, CreatedUtc = DateTime.UtcNow, HeroIsBot = r.AllBots, Format = "reaction", MusicFree = true, Title = "Reaction",
            Sources = sources, Video = r.Id + ".mp4",
            Players = r.PlayerLabels.Select(l => new ClipPlayer { Label = l, Hero = r.Hero != "none" && r.PlayerLabels.Count == 1 }).ToList(),
        };
        var json = Path.Combine(clipsDir, r.Id + ".json");
        await File.WriteAllTextAsync(json + ".tmp", JsonSerializer.Serialize(m, ClipManifest.JsonOptions), ct);
        File.Move(json + ".tmp", json, true);
        log.LogInformation("[Reactions] {Id} ({Kind}, score {S:0}) → {Frames} frames", r.Id, r.Kind, r.Score, frames);
        return json;
    }

    /// <summary>No layers (no demo for the moment, or the renderer failed): transparent overlay, silent game audio, no hero track.</summary>
    private async Task<LayerSet> EmptyLayersAsync(string dir, ReactionRecord r, MediaInfo f, double fps, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        double dur = f.Frames / fps;
        string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
        await FfmpegLocator.RunAsync(_ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=black@0.0:s=16x16:r={N(fps)}:d={N(dur)},format=bgra",
                                               "-c:v", "ffv1", "-pix_fmt", "bgra", Path.Combine(dir, "overlay.mkv")], ct);
        await FfmpegLocator.RunAsync(_ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"anullsrc=r=48000:cl=stereo", "-t", N(dur), "-c:a", "pcm_s16le", Path.Combine(dir, "game.wav")], ct);
        var m = new LayerManifest { HighlightId = "empty", UtcStart = r.FromUtc, Fps = fps, Width = 16, Height = 16, Frames = f.Frames };
        await File.WriteAllTextAsync(Path.Combine(dir, "layers.json"), JsonSerializer.Serialize(m), ct);
        return new LayerSet(dir, m);
    }
}
