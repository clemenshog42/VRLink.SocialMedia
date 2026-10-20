using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VRLink.Capture;
using VRLink.Composer;
using VRLink.Shared.Calibration;
using VRLink.Shared.Contracts;
using VRLink.Shared.Demo;
using VRLink.Shared.Media;
using Xunit.Abstractions;

namespace VRLink.Composer.Tests;

/// <summary>V3 end-to-end: TEST camera source + a synthetic v3 round demo with a jump → reaction detected → 6–10 s reaction clip.</summary>
public class ReactionEndToEndTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Jump_in_the_demo_becomes_a_reaction_clip()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrlinkrx-e2e", Guid.NewGuid().ToString("N"));
        string cap = Path.Combine(root, "cap"), hl = Path.Combine(root, "hl"), calibDir = Path.Combine(root, "calib");
        Directory.CreateDirectory(cap); Directory.CreateDirectory(hl);
        var ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null)!; var ffprobe = FfmpegLocator.Find("ffprobe.exe", null)!;
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCaptureCore(o =>
        {
            o.DataDir = cap; o.SegmentSeconds = 4; o.RetentionCheckSeconds = 3600;
            o.Cameras = [new CameraConfig { Id = "cam1", Source = "test", Width = 1920, Height = 1080, Fps = 30, VideoCodecArgs = "-c:v libx264 -preset ultrafast -crf 23" }];
        });
        await using var sp = services.BuildServiceProvider();
        var host = sp.GetRequiredService<CaptureHost>();
        try
        {
            await host.StartAsync(CancellationToken.None);
            var rec = host.Recorder("cam1")!;
            DateTime t0 = default;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var all = rec.Index.All();
                if (all.Count >= 2 && t0 == default) t0 = all[1].StartUtc;
                if (t0 != default && all[^1].EndUtc > t0.AddSeconds(15)) break;
                await Task.Delay(500);
            }
            await host.StopAsync(CancellationToken.None);

            // demo: server 0 ↔ t0 + 1 s; the jump peaks at server ≈ 10.3 → UTC ≈ t0 + 11.3 (window ±4 s inside the footage)
            var demo = ReactionTests.SyntheticRound(celebrate: false, dive: false, anchor: t0.AddSeconds(1), venue: "rx");
            demo.MatchId = "abcdef1234";
            WriteDemo(demo, Path.Combine(hl, "demos", "abcdef1234_r02.vrdemo"));
            Directory.CreateDirectory(Path.Combine(calibDir, "rx"));
            File.WriteAllText(Path.Combine(calibDir, "rx", "cam1.json"), JsonSerializer.Serialize(new CameraCalibration
            { VenueId = "rx", CameraId = "cam1", Pose = new CameraPose { X = 4, Y = 2.5, Z = -4, Yaw = -45, Pitch = 20, VFovDeg = 70, Width = 1920, Height = 1080 } }));

            var opt = new ComposerOptions { HighlightsDir = hl, CalibrationDir = calibDir, Camera = "cam1", WorkDir = Path.Combine(root, "work") };
            var job = new ReactionJob(opt, new InProcessFootageSource(host, sp.GetRequiredService<FootageService>(), TimeProvider.System),
                                      new FakeLayerRenderer(ffmpeg, 1920, 1080, []), NullLogger.Instance);
            var r = await job.RunAsync("abcdef1234", CancellationToken.None);
            output.WriteLine(r.Message + " | " + string.Join(" | ", r.Notes));
            var json = Assert.Single(r.Clips);
            var m = JsonSerializer.Deserialize<ClipManifest>(File.ReadAllText(json), ClipManifest.JsonOptions)!;
            Assert.Equal("reaction", m.Format);
            Assert.Equal("Reaction", m.Type);
            Assert.Equal("abcdef12-rx1", m.HighlightId);
            Assert.Equal("abcdef1234", m.MatchId);
            Assert.Contains(m.Players!, p => p.Label == "TU 1");
            Assert.False(m.HeroIsBot);
            Assert.Contains("roomAudio", m.Sources!);
            var mp4 = Path.Combine(Path.GetDirectoryName(json)!, m.HighlightId + ".mp4");
            var info = await Probe.RunAsync(ffprobe, mp4, CancellationToken.None);
            var (lufs, tp) = await AudioBuilder.MeasureAsync(ffmpeg, mp4, CancellationToken.None);
            output.WriteLine($"{m.HighlightId}: {info.Width}x{info.Height} {info.Fps} fps {info.Duration:0.000} s, {lufs:0.0} LUFS, TP {tp:0.0}");
            Assert.Equal((1080, 1920), (info.Width, info.Height));
            Assert.InRange(info.Duration, 6, 10);
            Assert.InRange(lufs, -15, -13);
            var preview = Environment.GetEnvironmentVariable("VRLINK_PREVIEW_DIR");
            if (!string.IsNullOrEmpty(preview))
                foreach (var (t, n) in new[] { (1.0, "1_leadin"), (4.3, "2_peak"), (info.Duration - 0.5, "3_endcard") })
                    await FfmpegLocator.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", t.ToString("0.000", CultureInfo.InvariantCulture), "-i", mp4,
                        "-frames:v", "1", "-vf", "scale=540:960", Path.Combine(preview, $"v3_reaction_{n}.png")], CancellationToken.None);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>Serialises a DemoData (frames + RoundEnd events) as a gzipped v3 .vrdemo.</summary>
    internal static void WriteDemo(DemoData d, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = new MemoryStream(); var w = new BinaryWriter(body, Encoding.UTF8);
        void V(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)32767); }
        foreach (var f in d.Frames)
        {
            w.Write((byte)DemoRecord.Frame); w.Write(f.Time); w.Write((byte)f.Players.Length);
            foreach (var p in f.Players)
            {
                w.Write((byte)p.Slot); w.Write((byte)(p.Alive ? DemoFormat.FlagAlive : 0)); w.Write((byte)p.Team);
                V(p.HeadPos); V(p.LeftPos); V(p.RightPos); V(p.RightPos); w.Write(DemoFormat.NoString);
            }
        }
        foreach (var e in d.Events.Where(e => e.Kind == DemoRecord.RoundEnd)) { w.Write((byte)DemoRecord.RoundEnd); w.Write(e.Time); w.Write(e.Code); w.Write(e.Code2); }
        w.Flush();
        var f2 = new BinaryWriter(new MemoryStream(), Encoding.UTF8);
        f2.Write("VRDM"u8.ToArray()); f2.Write((ushort)3); f2.Write(d.MatchId); f2.Write(d.Round); f2.Write("Map_test");
        f2.Write((byte)0); f2.Write(d.V3!.VenueId); f2.Write(d.V3.UtcAnchorTicks); f2.Write(d.V3.UtcAnchorServerTime); f2.Write(0.3f); f2.Write((ushort)0);
        f2.Write(d.StartTime); f2.Write(d.EndTime); f2.Write(30f); f2.Write(d.CreatedUtc.Ticks);
        f2.Write((ushort)d.Players.Count);
        foreach (var p in d.Players) { f2.Write(p.ClientId); f2.Write((byte)p.Team); f2.Write(p.IsBot); f2.Write(p.Female); f2.Write(p.EyeHeight); f2.Write(p.Label); }
        f2.Write((ushort)0);
        f2.Write(d.Frames.Count); f2.Write(d.Events.Count); f2.Write((int)body.Length); f2.Write(body.ToArray()); f2.Write((byte)0);
        f2.Flush();
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(((MemoryStream)f2.BaseStream).ToArray());
    }
}
