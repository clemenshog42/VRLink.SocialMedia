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

    /// <summary>Concat + trim, re-encoded (frame-accurate). Output in the footage cache unless outPath is given.</summary>
    /// <param name="applyOffset">false = raw camera time (used by the clap test itself).</param>
    public async Task<FootageWindow> GetWindowAsync(string cam, DateTime fromUtc, DateTime toUtc, string? outPath = null, CancellationToken ct = default, bool applyOffset = true)
    {
        var (segs, camFrom, camTo, offsetMs) = Select(cam, fromUtc, toUtc, applyOffset);
        var rec = host.Recorder(cam)!;
        Directory.CreateDirectory(host.CacheDir);
        outPath ??= Path.Combine(host.CacheDir, $"{cam}_{camFrom:yyyyMMddTHHmmssfff}_{(camTo - camFrom).TotalMilliseconds:0}.mp4");
        var listFile = Path.Combine(host.CacheDir, Guid.NewGuid().ToString("N") + ".txt");
        // "duration" = the measured wall-clock length of each segment, so the concat timeline follows the UTC index exactly
        // (without it the demuxer uses the longest stream per file and audio drifts a few ms per segment).
        await File.WriteAllLinesAsync(listFile, segs.SelectMany(s => new[]
        {
            "file '" + rec.Index.PathOf(s).Replace("\\", "/").Replace("'", "'\\''") + "'",
            "duration " + s.DurationSec.ToString("0.000000", CultureInfo.InvariantCulture),
        }), ct);
        try
        {
            double ss = (camFrom - segs[0].StartUtc).TotalSeconds;
            double t = (camTo - camFrom).TotalSeconds;
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile,
                "-ss", ss.ToString("0.000", CultureInfo.InvariantCulture), "-t", t.ToString("0.000", CultureInfo.InvariantCulture),
                // audio follows its timestamps (fills the tiny gaps between segments), so audio stays in sync with the UTC index
                "-af", "aresample=async=1:min_hard_comp=0.001:first_pts=0",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k",
                "-movflags", "+faststart", outPath,
            };
            var (code, _, err) = await FfmpegLocator.RunAsync(host.Ffmpeg, args, ct, TimeSpan.FromMinutes(10));
            if (code != 0) throw new FootageException("ffmpeg cut failed: " + err.Trim(), 500);
        }
        finally { try { File.Delete(listFile); } catch { } }
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
