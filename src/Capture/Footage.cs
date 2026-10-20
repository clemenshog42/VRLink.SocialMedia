using VRLink.Shared.Media;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VRLink.Capture;

public sealed class FootageException(string message, int httpStatus = 404) : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;
}

public sealed record FootageWindow(string Path, DateTime FromUtc, DateTime ToUtc, DateTime CameraFromUtc, double OffsetMs, int Segments);

/// <summary>
/// Footage access: cut [from, to) of a camera into one mp4 (concat of the overlapping segments + accurate trim by re-encoding),
/// grab a frame, or decode mono audio. Times are TRUE (demo/UTC) times; the camera offset from the clap test is applied:
/// camera time = true time + offset.
/// </summary>
public sealed class FootageService(CaptureHost host)
{
    public const double MaxGapSec = 0.5;

    private (List<SegmentRecord> segs, DateTime camFrom, DateTime camTo, double offsetMs) Select(string cam, DateTime fromUtc, DateTime toUtc, bool applyOffset = true)
    {
        if (toUtc <= fromUtc) throw new FootageException("to must be after from", 400);
        var rec = host.Recorder(cam) ?? throw new FootageException($"unknown camera '{cam}'");
        double offsetMs = applyOffset ? host.Offsets.OffsetMs(cam) : 0;
        var camFrom = fromUtc.AddMilliseconds(offsetMs);
        var camTo = toUtc.AddMilliseconds(offsetMs);
        var segs = rec.Index.Overlapping(camFrom, camTo).OrderBy(s => s.StartUtc).ToList();
        if (segs.Count == 0) throw new FootageException($"no footage for {cam} {camFrom:O}..{camTo:O}");
        if (segs[0].StartUtc > camFrom.AddMilliseconds(50) || segs[^1].EndUtc < camTo.AddMilliseconds(-50))
            throw new FootageException($"footage covers only {segs[0].StartUtc:O}..{segs[^1].EndUtc:O}", 416);
        for (int i = 1; i < segs.Count; i++)
        {
            var gap = (segs[i].StartUtc - segs[i - 1].EndUtc).TotalSeconds;
            if (Math.Abs(gap) > MaxGapSec) throw new FootageException($"gap of {gap:0.00}s in the footage at {segs[i - 1].EndUtc:O} (recorder restart)", 409);
        }
        return (segs, camFrom, camTo, offsetMs);
    }

    /// <summary>One input per overlapping segment, trimmed with input -ss/-t (frame/sample accurate), joined with the concat FILTER.
    /// Pieces without an audio stream (e.g. the last segment written on stop) get generated silence.</summary>
    public static List<string> CutArgs(IReadOnlyList<(string Path, double Start, double Length, bool HasAudio)> pieces, bool audio, string outPath)
    {
        static string N(double d) => d.ToString("0.000000", CultureInfo.InvariantCulture);
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var p in pieces) a.AddRange(["-ss", N(p.Start), "-t", N(p.Length), "-i", p.Path]);
        int extra = pieces.Count;
        var silenceInput = new Dictionary<int, int>();
        if (audio)
            for (int i = 0; i < pieces.Count; i++)
                if (!pieces[i].HasAudio)
                {
                    a.AddRange(["-f", "lavfi", "-t", N(pieces[i].Length), "-i", "anullsrc=r=48000:cl=mono"]);
                    silenceInput[i] = extra++;
                }
        var f = new System.Text.StringBuilder();
        for (int i = 0; i < pieces.Count; i++)
        {
            f.Append($"[{i}:v]setpts=PTS-STARTPTS,trim=end={N(pieces[i].Length)}[v{i}];");
            // audio: honour timestamps (start-of-file offset), pad the ~50 ms tail the recorder leaves per segment, cut to the exact length
            if (audio)
            {
                var src = silenceInput.TryGetValue(i, out var si) ? $"[{si}:a]" : $"[{i}:a]";
                f.Append($"{src}aresample=async=1:min_hard_comp=0.001:first_pts=0,apad,atrim=end={N(pieces[i].Length)}[a{i}];");
            }
        }
        for (int i = 0; i < pieces.Count; i++) f.Append(audio ? $"[v{i}][a{i}]" : $"[v{i}]");
        f.Append($"concat=n={pieces.Count}:v=1:a={(audio ? 1 : 0)}[v]" + (audio ? "[a]" : ""));
        a.AddRange(["-filter_complex", f.ToString(), "-map", "[v]"]);
        if (audio) a.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "160k"]);
        a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outPath]);
        return a;
    }

    /// <summary>
    /// Cut + join, re-encoded (frame-accurate video, sample-accurate audio). Output in the footage cache unless outPath is given.
    /// Note: the ffmpeg concat DEMUXER is not used on purpose: with AAC segments it shifted audio by ~51 ms (priming), measured.
    /// </summary>
    /// <param name="applyOffset">false = raw camera time (used by the clap test itself).</param>
    public async Task<FootageWindow> GetWindowAsync(string cam, DateTime fromUtc, DateTime toUtc, string? outPath = null, CancellationToken ct = default, bool applyOffset = true)
    {
        var (segs, camFrom, camTo, offsetMs) = Select(cam, fromUtc, toUtc, applyOffset);
        var rec = host.Recorder(cam)!;
        Directory.CreateDirectory(host.CacheDir);
        outPath ??= Path.Combine(host.CacheDir, $"{cam}_{camFrom:yyyyMMddTHHmmssfff}_{(camTo - camFrom).TotalMilliseconds:0}.mp4");
        var pieces = segs.Select(s =>
        {
            var start = Math.Max(0, (camFrom - s.StartUtc).TotalSeconds);
            var end = Math.Min(s.DurationSec, (camTo - s.StartUtc).TotalSeconds);
            return (rec.Index.PathOf(s), start, end - start, s.HasAudio != false);
        }).Where(p => p.Item3 > 0.001).ToList();
        var camCfg = host.Options.Cameras.FirstOrDefault(c => string.Equals(c.Id, cam, StringComparison.OrdinalIgnoreCase));
        bool audio = camCfg == null || string.Equals(camCfg.Source, "test", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(camCfg.AudioDevice);
        var (code, _, err) = await FfmpegLocator.RunAsync(host.Ffmpeg, CutArgs(pieces, audio, outPath), ct, TimeSpan.FromMinutes(10));
        if (code != 0) throw new FootageException("ffmpeg cut failed: " + err.Trim(), 500);
        return new FootageWindow(outPath, fromUtc, toUtc, camFrom, offsetMs, segs.Count);
    }

    /// <summary>Clamps [from, to) (true time) to the contiguous footage that exists for it; null if none.</summary>
    public (DateTime From, DateTime To)? Available(string cam, DateTime fromUtc, DateTime toUtc, bool applyOffset = true)
    {
        var rec = host.Recorder(cam);
        if (rec == null) return null;
        double off = applyOffset ? host.Offsets.OffsetMs(cam) : 0;
        var segs = rec.Index.Overlapping(fromUtc.AddMilliseconds(off), toUtc.AddMilliseconds(off)).OrderBy(s => s.StartUtc).ToList();
        if (segs.Count == 0) return null;
        var f = segs[0].StartUtc.AddMilliseconds(-off + 1); var t = segs[^1].EndUtc.AddMilliseconds(-off - 1);
        f = f > fromUtc ? f : fromUtc; t = t < toUtc ? t : toUtc;
        return t > f ? (f, t) : null;
    }

    /// <summary>JPEG of the frame shown at true time utc (for the calibration page).</summary>
    public async Task<byte[]> GetFrameJpegAsync(string cam, DateTime utc, int maxWidth = 1920, CancellationToken ct = default)
    {
        var (segs, camFrom, _, _) = Select(cam, utc, utc.AddMilliseconds(40));
        var rec = host.Recorder(cam)!;
        var seg = segs[0];
        double ss = Math.Max(0, (camFrom - seg.StartUtc).TotalSeconds);
        Directory.CreateDirectory(host.CacheDir);
        var outPath = Path.Combine(host.CacheDir, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            var (code, _, err) = await FfmpegLocator.RunAsync(host.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", rec.Index.PathOf(seg), "-ss", ss.ToString("0.000", CultureInfo.InvariantCulture),
                 "-frames:v", "1", "-vf", $"scale='min({maxWidth},iw)':-2", "-q:v", "3", outPath], ct, TimeSpan.FromSeconds(30));
            if (code != 0 || !File.Exists(outPath)) throw new FootageException("frame grab failed: " + err.Trim(), 500);
            return await File.ReadAllBytesAsync(outPath, ct);
        }
        finally { try { File.Delete(outPath); } catch { } }
    }

    /// <summary>Mono float audio of [from, to) (true time) at the given rate; returns samples and the true UTC of sample 0.</summary>
    public async Task<(float[] Samples, DateTime StartUtc)> GetAudioAsync(string cam, DateTime fromUtc, DateTime toUtc, int rate = 48000, CancellationToken ct = default, bool applyOffset = true)
    {
        var w = await GetWindowAsync(cam, fromUtc, toUtc, Path.Combine(host.CacheDir, Guid.NewGuid().ToString("N") + ".mp4"), ct, applyOffset);
        var raw = Path.ChangeExtension(w.Path, ".f32");
        try
        {
            var (code, _, err) = await FfmpegLocator.RunAsync(host.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-i", w.Path, "-vn", "-ac", "1", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", raw], ct);
            if (code != 0) throw new FootageException("audio decode failed: " + err.Trim(), 500);
            var bytes = await File.ReadAllBytesAsync(raw, ct);
            return (MemoryMarshal.Cast<byte, float>(bytes).ToArray(), fromUtc);
        }
        finally { try { File.Delete(raw); File.Delete(w.Path); } catch { } }
    }
}
