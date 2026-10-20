using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VRLink.Composer;

namespace VRLink.Composer.Tests;

public class WorkerTests
{
    private sealed class FakeFootage : IFootageSource
    {
        public List<(string Id, DateTime From, DateTime To)> Pins = new();
        public List<string> Released = new();
        public Task GetWindowAsync(string cam, DateTime f, DateTime t, string o, CancellationToken ct) => throw new IOException("no footage");
        public Task<string?> PinAsync(string? cam, DateTime f, DateTime t, string r, CancellationToken ct) { var id = "p" + Pins.Count; Pins.Add((id, f, t)); return Task.FromResult<string?>(id); }
        public Task ReleaseAsync(string id, CancellationToken ct) { Released.Add(id); return Task.CompletedTask; }
    }

    private sealed class ManualTime(DateTime t) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(t); }

    [Fact]
    public async Task New_highlight_is_pinned_at_once_composed_only_when_the_gate_opens_then_released()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkcomp-worker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            var opt = new ComposerOptions { HighlightsDir = dir, WorkDir = Path.Combine(dir, "work"), CalibrationDir = Path.Combine(dir, "calib") };
            var foot = new FakeFootage();
            var worker = new ComposerWorker(opt, foot, _ => new ComposeJob(opt, foot, new NoLayers(), NullLogger.Instance),
                                            NullLogger<ComposerWorker>.Instance, new ManualTime(now));
            Directory.CreateDirectory(opt.Work);

            // an old highlight from before the start is ignored on the first run
            await File.WriteAllTextAsync(Path.Combine(dir, "highlights_2026-10-20.jsonl"),
                Line("old-1", now.AddHours(-2)) + "\n");
            await worker.TickAsync(ignoreExisting: true, CancellationToken.None);
            Assert.Empty(foot.Pins);

            // new highlight → pinned right away; match running → wait
            await File.AppendAllTextAsync(Path.Combine(dir, "highlights_2026-10-20.jsonl"), Line("new-1", now.AddSeconds(-5)) + "\n");
            WriteState(opt.StateFile, now, "Match", 10, true);
            await worker.TickAsync(false, CancellationToken.None);
            Assert.Single(foot.Pins);
            Assert.InRange(foot.Pins[0].From, now.AddMinutes(-5), now.AddMinutes(-3));
            Assert.Equal(JobStatus.Pinned, worker.States.Single(s => s.Id == "new-1").Status);
            Assert.Contains("match running", worker.LastGate);

            // stale state → still waiting
            WriteState(opt.StateFile, now.AddMinutes(-2), "PostMatch", 0, false);
            await worker.TickAsync(false, CancellationToken.None);
            Assert.Equal(JobStatus.Pinned, worker.States.Single(s => s.Id == "new-1").Status);

            // PostMatch → job runs (here: no demo → skipped, the game-only clip stays) and the pin is released
            WriteState(opt.StateFile, now, "PostMatch", 4, false);
            await worker.TickAsync(false, CancellationToken.None);
            var st = worker.States.Single(s => s.Id == "new-1");
            Assert.Equal(JobStatus.Skipped, st.Status);
            Assert.Contains("demo", st.Message);
            Assert.Equal(["p0"], foot.Released);
            Assert.True(File.Exists(Path.Combine(opt.Work, "composer_state.json")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string Line(string id, DateTime created) => JsonSerializer.Serialize(new
    {
        v = 1, id, matchId = "m", round = 1, type = "Kills3", kills = 3, roundStart = 10.0, clipStart = 12.0, clipEnd = 20.0,
        moments = new[] { new { t = 14.0, kind = "Kill" } }, createdUtc = created,
    });

    private static void WriteState(string path, DateTime utc, string phase, int clients, bool running) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { utcNow = utc, phase, connectedClients = clients, matchRunning = running }));

    private sealed class NoLayers : ILayerRenderer
    {
        public Task<LayerSet> RenderAsync(LayerRequest r, CancellationToken ct) => throw new NotSupportedException();
    }
}
