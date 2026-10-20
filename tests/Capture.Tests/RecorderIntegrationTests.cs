using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VRLink.Capture;
using VRLink.Shared.Demo;
using VRLink.Shared.Sync;
using Xunit.Abstractions;

namespace VRLink.Capture.Tests;

/// <summary>
/// Real ffmpeg with the TEST source (no camera needed): records ~19 s in 2 s segments once for all tests in this class,
/// then checks the index, a cut window (ffprobe), the burned-in beeps (UTC multiples of 5 s), a frame grab and the clap test.
/// </summary>
public sealed class RecordingFixture : IAsyncLifetime
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "vrlinkcap-it", Guid.NewGuid().ToString("N"));
    public ServiceProvider Sp { get; private set; } = null!;
    public CaptureHost Host => Sp.GetRequiredService<CaptureHost>();
    public List<SegmentRecord> Segments { get; private set; } = new();
    public string Ffprobe => FfmpegLocator.Find("ffprobe.exe", null)!;

    public async Task InitializeAsync()
    {
        var s = new ServiceCollection();
        s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        s.AddCaptureCore(o =>
        {
            o.DataDir = Dir; o.SegmentSeconds = 2; o.RetentionMinutes = 15; o.RetentionCheckSeconds = 3600;
            o.Cameras = [new CameraConfig { Id = "test1", Source = "test", Width = 1920, Height = 1080, Fps = 30, VideoCodecArgs = "-c:v libx264 -preset ultrafast -crf 26" }];
        });
        Sp = s.BuildServiceProvider();
        await Host.StartAsync(CancellationToken.None);
        var rec = Host.Recorder("test1")!;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        // need ≥ 3 beeps (UTC multiples of 5 s) inside contiguous footage
        while (DateTime.UtcNow < deadline)
        {
            var all = rec.Index.All();
            if (all.Count >= 9 && (all[^1].EndUtc - all[1].StartUtc).TotalSeconds > 21) break;
            await Task.Delay(500);
        }
        await Host.StopAsync(CancellationToken.None);
        Segments = rec.Index.All();
    }

    public async Task DisposeAsync()
    {
        await Sp.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class RecorderIntegrationTests(RecordingFixture fx, ITestOutputHelper output) : IClassFixture<RecordingFixture>
{
    private async Task<string> Probe(string file, string entries) =>
        (await FfmpegLocator.RunAsync(fx.Ffprobe, ["-v", "error", "-show_entries", entries, "-of", "default=nw=1", file], CancellationToken.None)).Out.Trim();

    [Fact]
    public void Segments_are_indexed_named_by_utc_and_contiguous()
    {
        var s = fx.Segments;
        foreach (var r in s) output.WriteLine($"{r.File}  {r.StartUtc:HH:mm:ss.fff} → {r.EndUtc:HH:mm:ss.fff}  {r.DurationSec:0.000}s  {r.Bytes / 1024} KB  ({r.StartSource})");
        Assert.True(s.Count >= 6, $"only {s.Count} segments");
        Assert.Equal("probe", s[0].StartSource); // first segment of a run: end − probed duration
        Assert.All(s.Skip(1), r => Assert.Equal("list", r.StartSource));
        Assert.All(s, r => Assert.Equal(SegmentRecord.FileNameFor("test1", r.StartUtc), r.File));
        Assert.All(s, r => Assert.True(File.Exists(fx.Host.Recorder("test1")!.Index.PathOf(r))));
        Assert.All(s.Skip(1).SkipLast(1), r => Assert.InRange(r.DurationSec, 1.9, 2.1));
        for (int i = 1; i < s.Count; i++) Assert.InRange((s[i].StartUtc - s[i - 1].EndUtc).TotalSeconds, -0.001, 0.001);
        // wall-clock stamped: segment times are close to the real time of the test
        Assert.InRange((DateTime.UtcNow - s[^1].EndUtc).TotalSeconds, -1, 60);
        Assert.True(File.ReadAllLines(Path.Combine(fx.Dir, "test1", "index.jsonl")).Length >= s.Count);
    }

    [Fact]
    public async Task Window_cut_across_segments_has_the_exact_length_and_beeps_at_utc_multiples_of_5s()
    {
        var s = fx.Segments;
        var from = s[1].StartUtc.AddMilliseconds(700);
        var to = s[^1].EndUtc.AddMilliseconds(-300);
        var foot = fx.Sp.GetRequiredService<FootageService>();
        var w = await foot.GetWindowAsync("test1", from, to);
        var probe = await Probe(w.Path, "format=duration:stream=codec_type,width,height,r_frame_rate");
        output.WriteLine($"window {from:HH:mm:ss.fff}..{to:HH:mm:ss.fff} ({(to - from).TotalSeconds:0.000}s) from {w.Segments} segments\n{probe}");
        var dur = double.Parse(probe.Split('\n').First(l => l.StartsWith("duration=")).Split('=')[1], CultureInfo.InvariantCulture);
        Assert.InRange(dur, (to - from).TotalSeconds - 0.08, (to - from).TotalSeconds + 0.08);
        Assert.Contains("width=1920", probe);
        Assert.Contains("codec_type=audio", probe);

        var (samples, start) = await foot.GetAudioAsync("test1", from, to);
        var beeps = AudioOnsetDetector.Detect(samples, 48000).Select(t => start.AddTicks((long)(t * TimeSpan.TicksPerSecond))).ToList();
        foreach (var b in beeps)
        {
            double sec = (b - DateTime.UnixEpoch).TotalSeconds;
            double err = (sec - Math.Round(sec / 5) * 5) * 1000;
            output.WriteLine($"beep onset {b:HH:mm:ss.fff} UTC, error vs 5 s grid {err:+0.0;-0.0} ms");
            Assert.InRange(err, -5, 5); // audio of the cut is sample-aligned to the UTC index
        }
        Assert.True(beeps.Count >= 2, $"{beeps.Count} beeps");
    }

    [Fact]
    public async Task Frame_grab_returns_a_jpeg()
    {
        var foot = fx.Sp.GetRequiredService<FootageService>();
        var jpg = await foot.GetFrameJpegAsync("test1", fx.Segments[2].StartUtc.AddMilliseconds(500), 960);
        Assert.True(jpg.Length > 5000);
        Assert.Equal(0xFF, jpg[0]); Assert.Equal(0xD8, jpg[1]);
    }

    [Fact]
    public async Task Missing_footage_and_gaps_are_reported()
    {
        var foot = fx.Sp.GetRequiredService<FootageService>();
        var ex = await Assert.ThrowsAsync<FootageException>(() => foot.GetWindowAsync("test1", DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-3).AddSeconds(5)));
        Assert.Equal(404, ex.HttpStatus);
        var partial = await Assert.ThrowsAsync<FootageException>(() => foot.GetWindowAsync("test1", fx.Segments[^1].EndUtc.AddSeconds(-1), fx.Segments[^1].EndUtc.AddSeconds(5)));
        Assert.Equal(416, partial.HttpStatus);
    }

    /// <summary>Clap test end-to-end: the TEST beeps play the role of the claps; the synthetic v3 demo has its claps 250 ms earlier.</summary>
    [Fact]
    public async Task Clap_test_measures_the_camera_offset()
    {
        var s = fx.Segments;
        var t0 = s[1].StartUtc; var t1 = s[^1].EndUtc;
        var beeps = new List<DateTime>();
        for (var t = new DateTime((t0.Ticks / (5 * TimeSpan.TicksPerSecond) + 1) * 5 * TimeSpan.TicksPerSecond, DateTimeKind.Utc); t < t1.AddSeconds(-0.5); t = t.AddSeconds(5)) beeps.Add(t);
        Assert.True(beeps.Count >= 3, $"only {beeps.Count} beeps in the footage");

        const double trueOffsetMs = 250; // camera audio is 250 ms "late" relative to the demo clock
        var anchorUtc = t0.AddSeconds(1); const double anchorServer = 1000;
        double Server(DateTime utc) => anchorServer + (utc - anchorUtc).TotalSeconds;
        var demoClaps = beeps.Select(b => Server(b.AddMilliseconds(-trueOffsetMs))).ToList();

        var w = new ClapDemoWriter(anchorUtc, anchorServer, Server(t0.AddSeconds(1.5)), Server(t1.AddSeconds(-1.5)), demoClaps);
        var demoPath = Path.Combine(fx.Dir, "clap.vrdemo");
        w.Write(demoPath);

        var report = await fx.Sp.GetRequiredService<ClapSyncService>().RunAsync("test1", demoPath, save: true, CancellationToken.None);
        output.WriteLine($"{report.Message}; offset {report.OffsetMs:0.0} ms (true {trueOffsetMs}), max residual {report.MaxResidualMs:0.0} ms");
        var saved = fx.Host.Offsets.OffsetMs("test1");
        fx.Host.Offsets.Set("test1", 0); // other tests cut in raw time
        Assert.True(report.Ok, report.Message);
        Assert.InRange(report.OffsetMs, trueOffsetMs - 66.7, trueOffsetMs + 66.7); // done-criterion ±2 frames at 30 fps
        Assert.True(report.Saved);
        Assert.Equal(Math.Round(report.OffsetMs, 1), saved);
    }
}

/// <summary>Writes a gzipped v3 .vrdemo with one player whose controllers bang together at the given server times.</summary>
internal sealed class ClapDemoWriter(DateTime anchorUtc, double anchorServer, double start, double end, List<double> claps)
{
    public void Write(string path)
    {
        var body = new MemoryStream(); var w = new BinaryWriter(body, Encoding.UTF8);
        int frames = 0;
        for (double t = start; t <= end; t += 1 / 30.0)
        {
            double nearest = claps.Min(c => Math.Abs(t - c));
            float dist = (float)Math.Min(0.6, 0.06 + 2.0 * nearest);
            var c0 = new Vector3(0, 1.3f, 1);
            w.Write((byte)DemoRecord.Frame); w.Write(t); w.Write((byte)1);
            w.Write((byte)0); w.Write(DemoFormat.FlagAlive); w.Write((byte)Team.TacticalUnit);
            void V(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)32767); }
            V(new Vector3(0, 1.7f, 1)); V(c0 - new Vector3(dist / 2, 0, 0)); V(c0 + new Vector3(dist / 2, 0, 0)); V(c0);
            w.Write(DemoFormat.NoString);
            frames++;
        }
        w.Flush();
        var f = new BinaryWriter(new MemoryStream(), Encoding.UTF8);
        f.Write("VRDM"u8.ToArray()); f.Write((ushort)3); f.Write("calib"); f.Write(0); f.Write("Lobby_test");
        f.Write((byte)1); f.Write("testvenue"); f.Write(anchorUtc.Ticks); f.Write(anchorServer); f.Write(0.3f); f.Write((ushort)0);
        f.Write(start); f.Write(end); f.Write(30f); f.Write(anchorUtc.Ticks);
        f.Write((ushort)1); f.Write(1UL); f.Write((byte)1); f.Write(false); f.Write(false); f.Write(1.7f); f.Write("TU 1");
        f.Write((ushort)0);
        f.Write(frames); f.Write(0); f.Write((int)body.Length); f.Write(body.ToArray()); f.Write((byte)0);
        f.Flush();
        using var fs = File.Create(path);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        gz.Write(((MemoryStream)f.BaseStream).ToArray());
    }
}
