using VRLink.Shared.Demo;
using VRLink.Shared.Sync;

namespace VRLink.Capture;

public sealed record ClapSyncReport(
    bool Ok, string Message, double OffsetMs, int Matched, double MaxResidualMs,
    List<DateTime> DemoClapsUtc, List<DateTime> AudioOnsetsUtc, bool Saved, bool DemoHasUtcAnchor);

/// <summary>
/// Clap test: demo (v3, UTC anchor) + raw camera audio of the same time → offset = audioUtc − demoUtc (median of ≥3 claps).
/// The demo should be a short recording in which one person bangs the controllers together 3–6 times in front of the camera.
/// </summary>
public sealed class ClapSyncService(CaptureHost host, FootageService footage)
{
    public static List<DateTime> DemoClapsUtc(DemoData d)
    {
        var all = new List<double>();
        for (int slot = 0; slot < d.Players.Count; slot++)
        {
            var samples = d.Frames
                .SelectMany(f => f.Players.Where(p => p.Slot == slot && p.Alive).Select(p => new HandSample(f.Time, p.LeftPos, p.RightPos)))
                .ToList();
            all.AddRange(ControllerClapDetector.Detect(samples));
        }
        return all.Order().Select(d.UtcAt).ToList();
    }

    public async Task<ClapSyncReport> RunAsync(string cam, string demoPath, bool save, CancellationToken ct)
    {
        var d = DemoReader.ReadFile(demoPath);
        var claps = DemoClapsUtc(d);
        var want = (From: d.UtcAt(d.StartTime).AddSeconds(-2), To: d.UtcAt(d.EndTime).AddSeconds(2));
        var avail = footage.Available(cam, want.From, want.To, applyOffset: false)
                    ?? throw new FootageException($"no footage of {cam} during the demo ({want.From:O}..{want.To:O})");
        var (from, to) = avail;
        // raw camera time (offset 0) — the offset is what we measure
        var (samples, start) = await footage.GetAudioAsync(cam, from, to, 48000, ct, applyOffset: false);
        var onsets = AudioOnsetDetector.Detect(samples, 48000).Select(t => start.AddTicks((long)(t * TimeSpan.TicksPerSecond))).ToList();

        static double S(DateTime t) => (t - DateTime.UnixEpoch).TotalSeconds;
        var r = ClapOffsetSolver.Solve(onsets.Select(S).ToList(), claps.Select(S).ToList());
        bool saved = false;
        if (r.Ok && save) { host.Offsets.Set(cam, Math.Round(r.OffsetSec * 1000, 1)); saved = true; }
        var msg = r.Message + (d.HasUtcAnchor ? "" : " (WARNING: demo v1/v2 has no UTC anchor, offset is approximate)");
        return new ClapSyncReport(r.Ok, msg, r.OffsetSec * 1000, r.Matched, r.MaxResidualSec * 1000, claps, onsets, saved, d.HasUtcAnchor);
    }
}
