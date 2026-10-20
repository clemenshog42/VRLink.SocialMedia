using System.Numerics;
using VRLink.Shared.Sync;

namespace VRLink.Shared.Tests;

public class ClapSyncTests
{
    private const int Rate = 48000;

    /// <summary>Room noise + a few sharp clap transients (decaying noise bursts) at known times.</summary>
    private static float[] SyntheticAudio(double seconds, double[] clapTimes, int seed = 1, double noise = 0.01)
    {
        var rnd = new Random(seed);
        var a = new float[(int)(seconds * Rate)];
        for (int i = 0; i < a.Length; i++) a[i] = (float)((rnd.NextDouble() * 2 - 1) * noise);
        // some speech-like low rumble (slow, not sharp)
        for (int i = 0; i < a.Length; i++) a[i] += (float)(0.03 * Math.Sin(2 * Math.PI * 180 * i / Rate) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.3 * i / Rate)));
        foreach (var t in clapTimes)
        {
            int s0 = (int)Math.Round(t * Rate);
            for (int k = 0; k < Rate / 20 && s0 + k < a.Length; k++)
                a[s0 + k] += (float)((rnd.NextDouble() * 2 - 1) * 0.8 * Math.Exp(-k / (Rate * 0.006)));
        }
        return a;
    }

    [Fact]
    public void Audio_onsets_are_found_within_a_millisecond()
    {
        double[] claps = [1.2345, 3.5, 5.75, 8.01];
        var onsets = AudioOnsetDetector.Detect(SyntheticAudio(10, claps), Rate);
        Assert.Equal(claps.Length, onsets.Count);
        for (int i = 0; i < claps.Length; i++) Assert.InRange(onsets[i] - claps[i], -0.001, 0.001);
    }

    [Fact]
    public void Quiet_or_slow_audio_has_no_onsets()
    {
        Assert.Empty(AudioOnsetDetector.Detect(SyntheticAudio(5, []), Rate));
    }

    /// <summary>Hands swing together (V-shaped distance) at the given clap times, sampled at 30 Hz with a random phase.</summary>
    private static List<HandSample> SyntheticHands(double seconds, double[] claps, double phase = 0.013, double speed = 2.0)
    {
        var list = new List<HandSample>();
        var centre = new Vector3(1, 1.3f, 2);
        for (double t = phase; t < seconds; t += 1 / 30.0)
        {
            double nearest = claps.Length == 0 ? 99 : claps.Min(c => Math.Abs(t - c));
            double dist = Math.Min(0.6, 0.06 + speed * nearest); // 6 cm controller anchors at contact
            var half = new Vector3((float)(dist / 2), 0, 0);
            list.Add(new HandSample(t, centre - half, centre + half));
        }
        return list;
    }

    [Fact]
    public void Controller_claps_found_within_5_ms_despite_30Hz_sampling()
    {
        double[] claps = [101.111, 103.52, 105.947];
        var found = ControllerClapDetector.Detect(SyntheticHands(108, claps));
        Assert.Equal(3, found.Count);
        for (int i = 0; i < 3; i++) Assert.InRange(found[i] - claps[i], -0.005, 0.005);
    }

    [Fact]
    public void Slow_hand_approach_is_not_a_clap()
    {
        Assert.Empty(ControllerClapDetector.Detect(SyntheticHands(10, [5.0], speed: 0.3)));
    }

    [Fact]
    public void Offset_is_median_of_matched_claps_and_ignores_extra_noise_onsets()
    {
        double trueOffset = 0.347; // camera footage runs 347 ms ahead of the demo clock
        double[] demo = [10.0, 12.4, 15.1, 18.75];
        var rnd = new Random(3);
        var audio = demo.Select(d => d + trueOffset + (rnd.NextDouble() - 0.5) * 0.01).ToList(); // ±5 ms jitter
        audio.Add(13.9);  // somebody shouted
        audio.Add(20.2);  // door slam
        var r = ClapOffsetSolver.Solve(audio, demo);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(4, r.Matched);
        Assert.InRange(r.OffsetSec, trueOffset - 0.006, trueOffset + 0.006);
        Assert.True(r.MaxResidualSec < 0.011);
    }

    [Fact]
    public void Offset_needs_three_claps()
    {
        var r = ClapOffsetSolver.Solve([1.3, 2.3], [1.0, 2.0]);
        Assert.False(r.Ok);
    }

    [Fact]
    public void End_to_end_audio_and_hands_give_offset_within_2_frames()
    {
        // demo claps (server time 100 s ↔ UTC epoch second 1000), camera ahead by 0.21 s
        double[] demoClaps = [102.0, 104.3, 106.85];
        double anchorUtc = 1000, anchorServer = 100, offset = 0.21, footageStartUtc = 1001.5;
        var hands = ControllerClapDetector.Detect(SyntheticHands(109, demoClaps)).Select(t => anchorUtc + (t - anchorServer)).ToList();
        var audioStarts = demoClaps.Select(c => anchorUtc + (c - anchorServer) + offset - footageStartUtc).ToArray();
        var audio = AudioOnsetDetector.Detect(SyntheticAudio(8, audioStarts), Rate).Select(t => t + footageStartUtc).ToList();
        var r = ClapOffsetSolver.Solve(audio, hands);
        Assert.True(r.Ok, r.Message);
        Assert.InRange(r.OffsetSec, offset - 2 / 30.0, offset + 2 / 30.0); // done-criterion: ±2 frames
        Assert.InRange(r.OffsetSec, offset - 0.006, offset + 0.006);       // actually ~ms
    }
}
