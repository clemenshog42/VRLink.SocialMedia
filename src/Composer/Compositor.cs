using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using VRLink.Shared.Media;

namespace VRLink.Composer;

public sealed record MediaInfo(int Width, int Height, double Fps, int Frames, double Duration, bool HasAudio, bool HasVideo);

public static class Probe
{
    public static async Task<MediaInfo> RunAsync(string ffprobe, string file, CancellationToken ct)
    {
        var (code, so, err) = await FfmpegLocator.RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,width,height,r_frame_rate,nb_frames,duration:format=duration", "-of", "json", file], ct, TimeSpan.FromSeconds(60));
        if (code != 0) throw new InvalidOperationException($"ffprobe {file}: {err.Trim()}");
        var j = JsonNode.Parse(so)!;
        var streams = j["streams"]!.AsArray();
        var v = streams.FirstOrDefault(s => s?["codec_type"]?.ToString() == "video");
        bool a = streams.Any(s => s?["codec_type"]?.ToString() == "audio");
        double fps = 30;
        if (v?["r_frame_rate"]?.ToString() is { } r && r.Split('/') is [var n, var d] && double.TryParse(d, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            fps = double.Parse(n, CultureInfo.InvariantCulture) / dd;
        double dur = double.TryParse(j["format"]?["duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var du) ? du : 0;
        int frames = int.TryParse(v?["nb_frames"]?.ToString(), out var nf) ? nf : (int)Math.Round(dur * fps);
        return new MediaInfo(v?["width"]?.GetValue<int>() ?? 0, v?["height"]?.GetValue<int>() ?? 0, fps, frames, dur, a, v != null);
    }
}

/// <summary>Sequential raw frame reader (ffmpeg decode → pipe). Frames can only move forward.</summary>
public sealed class RawFrameReader : IDisposable
{
    private readonly Process _p;
    private readonly Stream _s;
    public int Width { get; }
    public int Height { get; }
    public int Bpp { get; }
    public int Index { get; private set; } = -1;
    public byte[] Buffer { get; }
    public bool Eof { get; private set; }

    public RawFrameReader(string ffmpeg, string file, int startFrame, double fps, int w, int h, string pixFmt)
    {
        Width = w; Height = h; Bpp = pixFmt == "bgra" ? 4 : 3;
        Buffer = new byte[w * h * Bpp];
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-ss", (startFrame / fps).ToString("0.######", CultureInfo.InvariantCulture), "-i", file,
                                  "-an", "-vf", $"scale={w}:{h}", "-f", "rawvideo", "-pix_fmt", pixFmt, "-" })
            psi.ArgumentList.Add(a);
        _p = Process.Start(psi)!;
        ChildProcessJob.Add(_p);
        try { _p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _ = _p.StandardError.ReadToEndAsync();
        _s = _p.StandardOutput.BaseStream;
        Index = startFrame - 1;
    }

    /// <summary>Advances to frame fi (≥ current). At the end of the stream the last frame stays.</summary>
    public byte[] Seek(int fi)
    {
        while (Index < fi && !Eof)
        {
            int read = 0;
            while (read < Buffer.Length)
            {
                int n = _s.Read(Buffer, read, Buffer.Length - read);
                if (n == 0) { Eof = true; break; }
                read += n;
            }
            if (!Eof) Index++;
        }
        return Buffer;
    }

    public void Dispose() { try { if (!_p.HasExited) _p.Kill(true); } catch { } _p.Dispose(); }
}

public sealed record ComposeInputs(string Footage, MediaInfo FootageInfo, string Overlay, MediaInfo OverlayInfo, string? Pov, MediaInfo? PovInfo);

/// <summary>
/// Frame compositor (C#): per output frame the EDL gives a source time; the reframe crop (plus kill punch) is cut from the footage,
/// the overlay (straight alpha, same time/crop scaled to its size) is blended on top, a white flash is added on kills.
/// Split: top 1080×960 = footage+overlay (9:8 reframe), bottom 1080×960 = POV, thin divider. Text comes after in ffmpeg (TextPlan).
/// The cold open (non-monotonic source access) is rendered first into memory, the rest is streamed.
/// </summary>
public sealed class Compositor(string ffmpeg)
{
    public const int OutW = 1080, OutH = 1920, HalfH = 960;

    public async Task<int> RenderAsync(ComposeInputs inp, Edl edl, IReadOnlyList<CropRect> crops, bool split, string textFilter,
                                       string audioWav, string outMp4, double fps, CancellationToken ct)
    {
        int n = (int)Math.Round(edl.Duration * fps);
        var fv = inp.FootageInfo; var ov = inp.OverlayInfo;
        int srcFrames = Math.Min(fv.Frames, ov.Frames);
        int Fi(double srcT) => Math.Clamp((int)Math.Round(srcT * fps), 0, srcFrames - 1);

        // which source frame each output frame needs
        var need = new int?[n];
        for (int k = 0; k < n; k++) { var s = edl.SourceAt(k / fps); need[k] = s == null ? null : Fi(s.Value); }
        int coldCount = Enumerable.Range(0, n).Count(k => k / fps < edl.ColdOpenEnd - 1e-9);

        var enc = StartEncoder(textFilter, audioWav, outMp4, fps);
        var stdin = enc.StandardInput.BaseStream;
        var errTask = enc.StandardError.ReadToEndAsync(ct);
        var frameBuf = new byte[OutW * OutH * 3];

        // pass A: cold open (decisive kill + rewind) → memory, in output order
        var cold = new byte[coldCount][];
        if (coldCount > 0)
        {
            int fa = Enumerable.Range(0, coldCount).Min(k => need[k] ?? 0), fb = Enumerable.Range(0, coldCount).Max(k => need[k] ?? 0);
            using var rf = Footage(inp, fa, fps); using var ro = Overlay(inp, fa, fps); using var rp = Pov(inp, fa, fps, split);
            for (int fi = fa; fi <= fb; fi++)
            {
                rf.Seek(fi); ro.Seek(fi); rp?.Seek(fi);
                for (int k = 0; k < coldCount; k++)
                    if (need[k] == fi) { cold[k] = new byte[frameBuf.Length]; Draw(cold[k], rf, ro, rp, edl, crops, k / fps, split, fv, ov); }
            }
            foreach (var f in cold) await stdin.WriteAsync(f, ct);
        }

        // pass B: the rest, streamed (source frames only move forward)
        int start = Enumerable.Range(coldCount, Math.Max(0, n - coldCount)).Select(k => need[k]).FirstOrDefault(x => x != null) ?? 0;
        RawFrameReader? f2 = null, o2 = null, p2 = null;
        try
        {
            f2 = Footage(inp, start, fps); o2 = Overlay(inp, start, fps); p2 = Pov(inp, start, fps, split);
            for (int k = coldCount; k < n; k++)
            {
                ct.ThrowIfCancellationRequested();
                if (need[k] is int fi)
                {
                    if (fi < f2.Index) // should not happen with the EDL structure; reopen to be safe
                    {
                        f2.Dispose(); o2.Dispose(); p2?.Dispose();
                        f2 = Footage(inp, fi, fps); o2 = Overlay(inp, fi, fps); p2 = Pov(inp, fi, fps, split);
                    }
                    f2.Seek(fi); o2.Seek(fi); p2?.Seek(fi);
                    Draw(frameBuf, f2, o2, p2, edl, crops, k / fps, split, fv, ov);
                }
                else EndCard(frameBuf, k / fps - edl.EndCardStart);
                await stdin.WriteAsync(frameBuf, ct);
            }
        }
        finally { f2?.Dispose(); o2?.Dispose(); p2?.Dispose(); }

        stdin.Close();
        await enc.WaitForExitAsync(ct);
        var err = await errTask;
        if (enc.ExitCode != 0) throw new InvalidOperationException("encode failed: " + err.Trim());
        return n;
    }

    private RawFrameReader Footage(ComposeInputs i, int fi, double fps) => new(ffmpeg, i.Footage, fi, fps, i.FootageInfo.Width, i.FootageInfo.Height, "bgr24");
    private RawFrameReader Overlay(ComposeInputs i, int fi, double fps) => new(ffmpeg, i.Overlay, fi, fps, i.OverlayInfo.Width, i.OverlayInfo.Height, "bgra");
    private RawFrameReader? Pov(ComposeInputs i, int fi, double fps, bool split) =>
        split && i.Pov != null ? new(ffmpeg, i.Pov, Math.Min(fi, Math.Max(0, (i.PovInfo?.Frames ?? 1) - 1)), fps, OutW, HalfH, "bgr24") : null;

    private Process StartEncoder(string textFilter, string audioWav, string outMp4, double fps)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        string r = fps.ToString("0.###", CultureInfo.InvariantCulture);
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{OutW}x{OutH}", "-r", r, "-i", "-",
                     "-i", audioWav,
                     "-filter_complex", textFilter, "-map", "[v]", "-map", "1:a",
                     "-c:v", "libx264", "-profile:v", "high", "-preset", "medium", "-crf", "19", "-r", r, "-pix_fmt", "yuv420p",
                     "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", outMp4,
                 })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        ChildProcessJob.Add(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        return p;
    }

    private static void Draw(byte[] dst, RawFrameReader f, RawFrameReader o, RawFrameReader? pov, Edl edl, IReadOnlyList<CropRect> crops,
                             double t, bool split, MediaInfo fv, MediaInfo ov)
    {
        double srcT = edl.SourceAt(t) ?? 0;
        double fps = 1 / Math.Max(1e-6, 1 / fv.Fps);
        var crop = Reframer.At(crops, srcT, fv.Fps, edl.PunchAt(t), fv.Width, fv.Height);
        int areaH = split ? HalfH : OutH;
        Blit(dst, 0, OutW, areaH, f.Buffer, f.Width, f.Height, o.Buffer, o.Width, o.Height, crop);
        if (split)
        {
            if (pov != null) System.Buffer.BlockCopy(pov.Buffer, 0, dst, HalfH * OutW * 3, OutW * HalfH * 3);
            else Array.Fill(dst, (byte)20, HalfH * OutW * 3, OutW * HalfH * 3);
            for (int y = HalfH - 3; y < HalfH + 3; y++) Array.Fill(dst, (byte)235, y * OutW * 3, OutW * 3);
        }
        double flash = edl.FlashAt(t);
        if (flash > 0)
        {
            int add = (int)(flash * 255);
            for (int i = 0; i < dst.Length; i++) dst[i] = (byte)(dst[i] + ((255 - dst[i]) * add >> 8));
        }
    }

    /// <summary>Bilinear crop+scale of the footage into dst rows [0, outH), overlay (straight alpha) blended with the same mapping.</summary>
    public static void Blit(byte[] dst, int dstY0, int outW, int outH, byte[] src, int sw, int sh, byte[] ovl, int ow, int oh, CropRect c)
    {
        double sx = c.W / outW, sy = c.H / outH, ox = (double)ow / sw, oy = (double)oh / sh;
        var x0 = new int[outW]; var fx = new float[outW]; var ox0 = new int[outW]; var ofx = new float[outW];
        for (int x = 0; x < outW; x++)
        {
            double u = c.Left + (x + 0.5) * sx - 0.5;
            u = Math.Clamp(u, 0, sw - 1.001); x0[x] = (int)u; fx[x] = (float)(u - x0[x]);
            double uo = Math.Clamp((u + 0.5) * ox - 0.5, 0, ow - 1.001); ox0[x] = (int)uo; ofx[x] = (float)(uo - ox0[x]);
        }
        Parallel.For(0, outH, y =>
        {
            double v = Math.Clamp(c.Top + (y + 0.5) * sy - 0.5, 0, sh - 1.001);
            int y0 = (int)v; float fy = (float)(v - y0);
            double vo = Math.Clamp((v + 0.5) * oy - 0.5, 0, oh - 1.001);
            int oy0 = (int)vo; float ofy = (float)(vo - oy0);
            int r0 = y0 * sw * 3, r1 = (y0 + 1) * sw * 3, q0 = oy0 * ow * 4, q1 = (oy0 + 1) * ow * 4;
            int d = (dstY0 + y) * outW * 3;
            for (int x = 0; x < outW; x++, d += 3)
            {
                int a = r0 + x0[x] * 3, b = a + 3, cc = r1 + x0[x] * 3, dd = cc + 3;
                float wx = fx[x], w00 = (1 - wx) * (1 - fy), w10 = wx * (1 - fy), w01 = (1 - wx) * fy, w11 = wx * fy;
                int qa = q0 + ox0[x] * 4, qb = qa + 4, qc = q1 + ox0[x] * 4, qd = qc + 4;
                float owx = ofx[x], o00 = (1 - owx) * (1 - ofy), o10 = owx * (1 - ofy), o01 = (1 - owx) * ofy, o11 = owx * ofy;
                float al = (ovl[qa + 3] * o00 + ovl[qb + 3] * o10 + ovl[qc + 3] * o01 + ovl[qd + 3] * o11) / 255f;
                for (int ch = 0; ch < 3; ch++)
                {
                    float s = src[a + ch] * w00 + src[b + ch] * w10 + src[cc + ch] * w01 + src[dd + ch] * w11;
                    if (al > 0.002f)
                    {
                        // straight alpha: premultiply the colour samples by their own alpha for correct edges
                        float oc = (ovl[qa + ch] * ovl[qa + 3] * o00 + ovl[qb + ch] * ovl[qb + 3] * o10 + ovl[qc + ch] * ovl[qc + 3] * o01 + ovl[qd + ch] * ovl[qd + 3] * o11) / 255f;
                        s = s * (1 - al) + oc;
                    }
                    dst[d + ch] = (byte)Math.Clamp(s + 0.5f, 0, 255);
                }
            }
        });
    }

    /// <summary>End card background (dark gradient with an accent line); texts come from TextPlan. Loops cleanly (static).</summary>
    public static void EndCard(byte[] dst, double t)
    {
        for (int y = 0; y < OutH; y++)
        {
            double g = (double)y / OutH;
            byte b = (byte)(22 + 18 * g), gg = (byte)(16 + 10 * g), r = (byte)(13 + 8 * g);
            int row = y * OutW * 3;
            for (int x = 0; x < OutW; x++) { dst[row + x * 3] = b; dst[row + x * 3 + 1] = gg; dst[row + x * 3 + 2] = r; }
        }
        for (int y = 960; y < 966; y++)
            for (int x = 240; x < 840; x++) { int i = (y * OutW + x) * 3; dst[i] = 255; dst[i + 1] = 208; dst[i + 2] = 53; }
    }
}
