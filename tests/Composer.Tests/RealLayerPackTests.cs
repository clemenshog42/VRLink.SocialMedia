using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VRLink.Composer;
using VRLink.Shared.Contracts;
using VRLink.Shared.Media;
using Xunit.Abstractions;

namespace VRLink.Composer.Tests;

/// <summary>
/// Integration with a REAL Unity -renderlayers pack (Builds/Highlights/layers/4679a779-r02-8) using its plate.mp4 as footage.
/// Runs only on the dev PC where the pack exists; output goes to a temp folder, never into the live clips folder.
/// </summary>
public class RealLayerPackTests(ITestOutputHelper output)
{
    private const string Pack = @"C:\vr\VRCsGo\Builds\Highlights\layers\4679a779-r02-8";
    private const string Highlights = @"C:\vr\VRCsGo\Builds\Highlights";

    [Fact]
    public async Task Real_unity_layers_with_plate_compose_into_both_formats()
    {
        if (!File.Exists(Path.Combine(Pack, "layers.json")) || !File.Exists(Path.Combine(Pack, "plate.mp4"))) return; // not on this machine
        var ls = LayerSet.Load(Pack);
        Assert.Equal(1, ls.Manifest.V);
        Assert.Equal(ls.Manifest.Frames, ls.Manifest.HeroTrack.Count);
        Assert.False(ls.Manifest.ClockExact); // demo v2
        var h = HighlightFeed.ReadAll(Highlights).Last(x => x.Id == "4679a779-r02-8");

        var tmp = Path.Combine(Path.GetTempPath(), "vrlinkcomp-real", Guid.NewGuid().ToString("N"));
        try
        {
            var opt = new ComposerOptions { HighlightsDir = Highlights, ClipsDir = Path.Combine(tmp, "clips"), WorkDir = Path.Combine(tmp, "work") };
            var job = new ComposeJob(opt, new NullFootage(), new NullLayers(), NullLogger.Instance);
            var r = await job.ComposeFromFilesAsync(h, Pack, Path.Combine(Pack, "plate.mp4"), opt.Clips, CancellationToken.None);
            output.WriteLine(r.Message + " | " + string.Join(" | ", r.Notes));
            Assert.Equal(2, r.Clips.Count);
            var ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null)!; var ffprobe = FfmpegLocator.Find("ffprobe.exe", null)!;
            foreach (var json in r.Clips)
            {
                var m = JsonSerializer.Deserialize<ClipManifest>(File.ReadAllText(json), ClipManifest.JsonOptions)!;
                Assert.Equal("plate", m.Sources![0]);
                Assert.DoesNotContain("roomAudio", m.Sources); // the plate has no room audio
                Assert.Contains(m.Players!, p => p.Hero && p.Label == "SYN 3");
                var mp4 = Path.Combine(Path.GetDirectoryName(json)!, m.HighlightId + ".mp4");
                var info = await Probe.RunAsync(ffprobe, mp4, CancellationToken.None);
                Assert.Equal((1080, 1920), (info.Width, info.Height));
                Assert.Equal(m.DurationSec, info.Duration, 2);
                var (lufs, tp) = await AudioBuilder.MeasureAsync(ffmpeg, mp4, CancellationToken.None);
                output.WriteLine($"{m.HighlightId}: {info.Duration.ToString("0.000", CultureInfo.InvariantCulture)} s, {lufs:0.0} LUFS, TP {tp:0.0}");
                Assert.InRange(lufs, -15, -13);
            }
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private sealed class NullFootage : IFootageSource
    {
        public Task GetWindowAsync(string cam, DateTime f, DateTime t, string o, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> PinAsync(string? cam, DateTime f, DateTime t, string r, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task ReleaseAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullLayers : ILayerRenderer
    {
        public Task<LayerSet> RenderAsync(LayerRequest r, CancellationToken ct) => throw new NotSupportedException();
    }
}
