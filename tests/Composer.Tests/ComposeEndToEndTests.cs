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

/// <summary>
/// End-to-end on the TEST camera source + fake layers (real ffmpeg): record ~24 s, write a v3 demo + calibration + highlight,
/// run the full ComposeJob (footage cut via the in-process Capture service), check both outputs with ffprobe and ebur128.
/// Set VRLINK_PREVIEW_DIR to also write preview PNGs.
/// </summary>
public class ComposeEndToEndTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Test_source_plus_fake_layers_gives_irl_and_split_clips_in_the_contract()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrlinkcomp-e2e", Guid.NewGuid().ToString("N"));
        var dirs = new { cap = Path.Combine(root, "cap"), hl = Path.Combine(root, "hl"), calib = Path.Combine(root, "calib"), work = Path.Combine(root, "work") };
        foreach (var d in new[] { dirs.cap, dirs.hl, dirs.calib, dirs.work }) Directory.CreateDirectory(d);
        var ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null)!; var ffprobe = FfmpegLocator.Find("ffprobe.exe", null)!;

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCaptureCore(o =>
        {
            o.DataDir = dirs.cap; o.SegmentSeconds = 4; o.RetentionCheckSeconds = 3600;
            o.Cameras = [new CameraConfig { Id = "cam1", Source = "test", Width = 1920, Height = 1080, Fps = 30, VideoCodecArgs = "-c:v libx264 -preset ultrafast -crf 23" }];
        });
        await using var sp = services.BuildServiceProvider();
        var host = sp.GetRequiredService<CaptureHost>();
        try
        {
            await host.StartAsync(CancellationToken.None);
            var rec = host.Recorder("cam1")!;
            DateTime t0 = default;
            var deadline = DateTime.UtcNow.AddSeconds(70);
            while (DateTime.UtcNow < deadline)
            {
                var all = rec.Index.All();
                if (all.Count >= 2 && t0 == default) t0 = all[1].StartUtc;
                if (t0 != default && all[^1].EndUtc > t0.AddSeconds(23)) break;
                await Task.Delay(500);
            }
            await host.StopAsync(CancellationToken.None);
            Assert.NotEqual(default, t0);

            // timeline: server time 1000 ↔ t0 + 1 s; kills at +7, +9.5, +15 s server
            var anchor = t0.AddSeconds(1);
            double[] killServer = [1007, 1009.5, 1015];
            WriteDemo(Path.Combine(dirs.hl, "demos", "e2ematch_r03.vrdemo"), anchor, 1000, 1000, 1022);
            var calib = new CameraCalibration { VenueId = "e2e", CameraId = "cam1", Pose = new CameraPose { X = 4, Y = 2.5, Z = -4, Yaw = -45, Pitch = 20, VFovDeg = 70, Width = 1920, Height = 1080 } };
            Directory.CreateDirectory(Path.Combine(dirs.calib, "e2e"));
            File.WriteAllText(Path.Combine(dirs.calib, "e2e", "cam1.json"), JsonSerializer.Serialize(calib));
            var h = new HighlightRecord
            {
                V = 1, Id = "e2e-r03-1", MatchId = "e2ematch", Round = 3, Type = "Kills3", Kills = 3, Score = 60, HeroLabel = "TU 1", HeroTeam = "TU",
                RoundStart = 1000, ClipStart = 1004, ClipEnd = 1016, CreatedUtc = DateTime.UtcNow,
                Moments = killServer.Select((t, i) => new HighlightMoment { T = t, Kind = "Kill", VictimLabel = $"SYN {i + 1}" }).ToList(),
            };
            var opt = new ComposerOptions { HighlightsDir = dirs.hl, CalibrationDir = dirs.calib, Camera = "cam1", WorkDir = dirs.work };
            var killUtc = killServer.Select(k => anchor.AddSeconds(k - 1000)).ToList();
            var job = new ComposeJob(opt, new InProcessFootageSource(host, sp.GetRequiredService<FootageService>(), TimeProvider.System),
                                     new FakeLayerRenderer(ffmpeg, 1920, 1080, killUtc, "TU 1"), NullLogger.Instance);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await job.RunAsync(h, CancellationToken.None);
            output.WriteLine($"{r.Message} in {sw.Elapsed.TotalSeconds:0.0}s; notes: {string.Join(" | ", r.Notes)}");
            Assert.True(r.Ok, r.Message);
            Assert.Equal(2, r.Clips.Count);

            foreach (var json in r.Clips)
            {
                var m = JsonSerializer.Deserialize<ClipManifest>(File.ReadAllText(json), ClipManifest.JsonOptions)!;
                var mp4 = Path.Combine(Path.GetDirectoryName(json)!, m.HighlightId + ".mp4");
                Assert.True(File.Exists(mp4)); Assert.True(File.Exists(Path.ChangeExtension(mp4, ".jpg")));
                Assert.Contains(m.Format, new[] { "irl", "split" });
                Assert.Equal($"e2e-r03-1-{m.Format}", m.HighlightId);
                Assert.True(m.MusicFree);
                Assert.Contains("arenaCam:cam1", m.Sources!);
                Assert.Equal(m.Format == "split", m.Sources!.Contains("pov"));
                Assert.Contains(m.Players!, p => p.Hero && p.Label == "TU 1");

                var (code, so, _) = await FfmpegLocator.RunAsync(ffprobe, ["-v", "error", "-show_entries",
                    "stream=codec_type,codec_name,profile,width,height,r_frame_rate,duration,sample_rate:format=duration", "-of", "default=nw=1", mp4], CancellationToken.None);
                output.WriteLine($"--- {Path.GetFileName(mp4)} ({m.DurationSec:0.000} s)\n{so.Trim()}");
                var lines = so.Split('\n').Select(l => l.Trim()).ToList();
                Assert.Contains("width=1080", lines); Assert.Contains("height=1920", lines); Assert.Contains("r_frame_rate=30/1", lines);
                Assert.Contains("profile=High", lines);
                var durs = lines.Where(l => l.StartsWith("duration=")).Select(l => double.Parse(l[9..], CultureInfo.InvariantCulture)).ToList();
                double v = durs[0], a = durs[1];
                Assert.InRange(Math.Abs(v - a), 0, 1 / 30.0 + 0.025); // audio == video ±1 frame (+ AAC frame rounding)
                Assert.InRange(v, 9, 21);
                var (lufs, tp) = await AudioBuilder.MeasureAsync(ffmpeg, mp4, CancellationToken.None);
                output.WriteLine($"ebur128: I = {lufs:0.0} LUFS, true peak {tp:0.0} dBFS, |v−a| = {Math.Abs(v - a) * 1000:0} ms");
                Assert.InRange(lufs, -15, -13);
                Assert.True(tp <= -0.5, $"true peak {tp}");

                var preview = Environment.GetEnvironmentVariable("VRLINK_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(preview))
                {
                    Directory.CreateDirectory(preview);
                    foreach (var (t, name) in new[] { (0.5, "1_coldopen_hook"), (6.0, "2_buildup"), (m.DurationSec - 2.6, "3_final_slowmo"), (m.DurationSec - 0.5, "4_endcard") })
                        await FfmpegLocator.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", t.ToString("0.000", CultureInfo.InvariantCulture), "-i", mp4,
                            "-frames:v", "1", "-vf", "scale=540:960", Path.Combine(preview, $"v2_composer_{m.Format}_{name}.png")], CancellationToken.None);
                }
            }
            // the footage stayed pinned only during the job (worker path) — here: no pins left over
            Assert.Empty(host.Pins.All());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>Minimal v3 demo (one player) so that UtcAt works for the highlight's server times.</summary>
    private static void WriteDemo(string path, DateTime anchorUtc, double anchorServer, double start, double end)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = new MemoryStream(); var w = new BinaryWriter(body, Encoding.UTF8);
        int frames = 0;
        for (double t = start; t <= end; t += 1 / 30.0)
        {
            w.Write((byte)DemoRecord.Frame); w.Write(t); w.Write((byte)1);
            w.Write((byte)0); w.Write(DemoFormat.FlagAlive); w.Write((byte)Team.TacticalUnit);
            for (int k = 0; k < 4; k++) { w.Write(1f); w.Write(1.5f); w.Write(2f); w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)32767); }
            w.Write(DemoFormat.NoString);
            frames++;
        }
        w.Flush();
        var f = new BinaryWriter(new MemoryStream(), Encoding.UTF8);
        f.Write("VRDM"u8.ToArray()); f.Write((ushort)3); f.Write("e2ematch"); f.Write(3); f.Write("Map_test");
        f.Write((byte)0); f.Write("e2e"); f.Write(anchorUtc.Ticks); f.Write(anchorServer); f.Write(0.3f); f.Write((ushort)0);
        f.Write(start); f.Write(end); f.Write(30f); f.Write(anchorUtc.Ticks);
        f.Write((ushort)1); f.Write(1UL); f.Write((byte)1); f.Write(false); f.Write(false); f.Write(1.7f); f.Write("TU 1");
        f.Write((ushort)0);
        f.Write(frames); f.Write(0); f.Write((int)body.Length); f.Write(body.ToArray()); f.Write((byte)0);
        f.Flush();
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        gz.Write(((MemoryStream)f.BaseStream).ToArray());
    }
}
