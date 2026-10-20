using System.Numerics;

namespace VRLink.Shared.Sync;

/// <summary>
/// Clap test (time offset per camera): a person bangs the two controllers together in front of the camera.
/// The bang is (a) a sharp transient in the room-mic audio of the camera footage and (b) a minimum of the
/// left-to-right controller distance with a high closing speed in the demo. offset = audioUtc − demoUtc (median of ≥3 claps).
/// Footage time = true (demo) time + offset, so to get footage for demo time t use camera time t + offset.
/// All functions are pure.
/// </summary>
public static class AudioOnsetDetector
{
    public sealed record Options
    {
        /// <summary>Envelope window (s).</summary>
        public double WindowSec { get; init; } = 0.001;
        /// <summary>Onset when the envelope exceeds this multiple of the noise floor (median envelope)...</summary>
        public double RatioOverFloor { get; init; } = 8;
        /// <summary>...and this absolute amplitude (full scale = 1).</summary>
        public double MinAmplitude { get; init; } = 0.05;
        /// <summary>The preceding window must be quiet (envelope below threshold × this) for a sharp attack.</summary>
        public double QuietBeforeSec { get; init; } = 0.02;
        /// <summary>Ignore further onsets for this long (ringing, echo).</summary>
        public double RefractorySec { get; init; } = 0.3;
    }

    /// <summary>Onset times in seconds relative to sample 0 (sample-accurate: first sample reaching half the local peak).</summary>
    public static List<double> Detect(ReadOnlySpan<float> samples, int sampleRate, Options? o = null)
    {
        o ??= new Options();
        var result = new List<double>();
        int win = Math.Max(1, (int)Math.Round(o.WindowSec * sampleRate));
        int nw = samples.Length / win;
        if (nw < 4) return result;
        var env = new double[nw];
        for (int w = 0; w < nw; w++)
        {
            double peak = 0;
            for (int i = w * win, e = i + win; i < e; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
            env[w] = peak;
        }
        var sorted = (double[])env.Clone();
        Array.Sort(sorted);
        double floor = sorted[nw / 2];
        double thr = Math.Max(o.MinAmplitude, floor * o.RatioOverFloor);
        int quiet = Math.Max(1, (int)Math.Round(o.QuietBeforeSec * sampleRate / win));
        int refractory = (int)Math.Round(o.RefractorySec * sampleRate / win);

        for (int w = quiet; w < nw; w++)
        {
            if (env[w] < thr) continue;
            bool sharp = true;
            for (int k = w - quiet; k < w; k++) if (env[k] >= thr * 0.5) { sharp = false; break; }
            if (!sharp) continue;
            // Local peak in the next 10 ms, then the first sample reaching half of it = onset.
            int s0 = w * win, s1 = Math.Min(samples.Length, s0 + Math.Max(win, sampleRate / 100));
            double localPeak = 0;
            for (int i = s0; i < s1; i++) localPeak = Math.Max(localPeak, Math.Abs(samples[i]));
            int onset = s0;
            for (int i = Math.Max(0, s0 - win); i < s1; i++) if (Math.Abs(samples[i]) >= localPeak * 0.5) { onset = i; break; }
            result.Add(onset / (double)sampleRate);
            w += refractory;
        }
        return result;
    }
}

public readonly record struct HandSample(double Time, Vector3 Left, Vector3 Right);

public static class ControllerClapDetector
{
    public sealed record Options
    {
        /// <summary>Distance between the controller anchors at contact is at most this (m).</summary>
        public double MaxContactDistance { get; init; } = 0.15;
        /// <summary>Hands must approach at least this fast just before contact (m/s).</summary>
        public double MinClosingSpeed { get; init; } = 1.0;
        /// <summary>Look-back window for the closing speed (s).</summary>
        public double ApproachWindowSec { get; init; } = 0.15;
        public double RefractorySec { get; init; } = 0.3;
    }

    /// <summary>
    /// Clap times (server seconds) of one player. The distance curve at a bang is V-shaped, so the contact time is the
    /// intersection of the approach line (2 samples before the minimum) and the separation line (2 samples after).
    /// </summary>
    public static List<double> Detect(IReadOnlyList<HandSample> s, Options? o = null)
    {
        o ??= new Options();
        var result = new List<double>();
        if (s.Count < 5) return result;
        var d = s.Select(x => (double)Vector3.Distance(x.Left, x.Right)).ToArray();
        double lastClap = double.NegativeInfinity;
        for (int i = 2; i < s.Count - 2; i++)
        {
            if (!(d[i] <= d[i - 1] && d[i] < d[i + 1]) || d[i] > o.MaxContactDistance) continue;
            // closing speed: max approach speed in the look-back window
            double speed = 0;
            for (int k = i; k > 0 && s[i].Time - s[k - 1].Time <= o.ApproachWindowSec + 1e-9; k--)
            {
                double dt = s[k].Time - s[k - 1].Time;
                if (dt > 0) speed = Math.Max(speed, (d[k - 1] - d[k]) / dt);
            }
            if (speed < o.MinClosingSpeed) continue;
            double t = VIntersection(s[i - 2].Time, d[i - 2], s[i - 1].Time, d[i - 1], s[i + 1].Time, d[i + 1], s[i + 2].Time, d[i + 2], s[i].Time);
            if (t - lastClap < o.RefractorySec) continue;
            result.Add(t);
            lastClap = t;
        }
        return result;
    }

    private static double VIntersection(double t0, double d0, double t1, double d1, double t3, double d3, double t4, double d4, double fallback)
    {
        double m1 = (d1 - d0) / (t1 - t0), m2 = (d4 - d3) / (t4 - t3);
        if (m1 >= -1e-9 || m2 <= 1e-9) return fallback;
        // d = d1 + m1 (t - t1) = d3 + m2 (t - t3)
        double t = (d3 - d1 + m1 * t1 - m2 * t3) / (m1 - m2);
        return t >= t1 && t <= t3 ? t : fallback;
    }
}

public sealed record ClapOffsetResult(bool Ok, double OffsetSec, int Matched, double MaxResidualSec, IReadOnlyList<(double AudioUtcSec, double DemoUtcSec, double ResidualSec)> Pairs, string Message);

public static class ClapOffsetSolver
{
    /// <summary>
    /// Pairs audio claps with demo claps (both as UTC seconds on any common epoch) and returns offset = audio − demo.
    /// Coarse step: every audio−demo difference within ±maxOffset is a candidate; the one matching the most claps (within tolerance) wins.
    /// Fine step: median of the paired differences. Needs ≥ minClaps matches.
    /// </summary>
    public static ClapOffsetResult Solve(IReadOnlyList<double> audioUtc, IReadOnlyList<double> demoUtc,
                                         double maxOffsetSec = 10, double toleranceSec = 0.1, int minClaps = 3)
    {
        double bestOffset = 0; int bestCount = 0; double bestSpread = double.MaxValue;
        foreach (var a in audioUtc)
            foreach (var dm in demoUtc)
            {
                double cand = a - dm;
                if (Math.Abs(cand) > maxOffsetSec) continue;
                var pairs = Pair(audioUtc, demoUtc, cand, toleranceSec);
                double spread = pairs.Count == 0 ? double.MaxValue : pairs.Sum(p => Math.Abs(p.a - p.d - cand));
                if (pairs.Count > bestCount || (pairs.Count == bestCount && spread < bestSpread))
                { bestCount = pairs.Count; bestOffset = cand; bestSpread = spread; }
            }
        if (bestCount < minClaps)
            return new(false, 0, bestCount, 0, [], $"only {bestCount} clap(s) matched, need {minClaps}");

        var matched = Pair(audioUtc, demoUtc, bestOffset, toleranceSec);
        var diffs = matched.Select(p => p.a - p.d).OrderBy(x => x).ToList();
        double median = diffs.Count % 2 == 1 ? diffs[diffs.Count / 2] : (diffs[diffs.Count / 2 - 1] + diffs[diffs.Count / 2]) / 2;
        var outPairs = matched.Select(p => (p.a, p.d, p.a - p.d - median)).ToList();
        double maxRes = outPairs.Max(p => Math.Abs(p.Item3));
        return new(true, median, matched.Count, maxRes, outPairs, $"{matched.Count} claps, max residual {maxRes * 1000:0} ms");
    }

    private static List<(double a, double d)> Pair(IReadOnlyList<double> audio, IReadOnlyList<double> demo, double offset, double tol)
    {
        var used = new HashSet<int>();
        var pairs = new List<(double, double)>();
        foreach (var dm in demo)
        {
            int best = -1; double bestErr = tol;
            for (int i = 0; i < audio.Count; i++)
            {
                if (used.Contains(i)) continue;
                double err = Math.Abs(audio[i] - dm - offset);
                if (err <= bestErr) { bestErr = err; best = i; }
            }
            if (best >= 0) { used.Add(best); pairs.Add((audio[best], dm)); }
        }
        return pairs;
    }
}
