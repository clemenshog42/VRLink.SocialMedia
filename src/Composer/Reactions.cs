using System.Numerics;
using VRLink.Shared.Demo;

namespace VRLink.Composer;

/// <summary>Loudness peak in the room audio (UTC as seconds since Unix epoch for easy math).</summary>
public sealed record AudioPeak(double StartSec, double EndSec, double PeakSec, double PeakDb, double AboveMedianDb)
{
    public double Duration => EndSec - StartSec;
}

public sealed record ReactionOptions
{
    public double WindowSec { get; init; } = 0.1;
    public double HopSec { get; init; } = 0.05;
    /// <summary>Peak = louder than the session median by this much …</summary>
    public double ThresholdDb { get; init; } = 15;
    /// <summary>… for at least this long.</summary>
    public double MinSustainSec { get; init; } = 0.25;
    public double MergeSec { get; init; } = 3;
    public double JumpRise { get; init; } = 0.25;
    public double JumpRiseSec { get; init; } = 0.4;
    public double JumpReturnSec { get; init; } = 0.8;
    public double DiveDrop { get; init; } = 0.6;
    public double DiveSec { get; init; } = 0.5;
    public double CelebrationSec { get; init; } = 5;
    public int CelebrationMinPlayers { get; init; } = 2;
    public int MaxPerSession { get; init; } = 3;
    public double CooldownSec { get; init; } = 20;
    public double PreSec { get; init; } = 4;
    public double PostSec { get; init; } = 4;
}

/// <summary>Short-term loudness (RMS of 100 ms windows, 50 ms hop) and session-relative peaks (pure).</summary>
public static class LoudnessPeaks
{
    public static double[] RmsDb(ReadOnlySpan<float> s, int rate, ReactionOptions o)
    {
        int win = Math.Max(1, (int)(o.WindowSec * rate)), hop = Math.Max(1, (int)(o.HopSec * rate));
        int n = s.Length < win ? 0 : (s.Length - win) / hop + 1;
        var db = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            var w = s.Slice(i * hop, win);
            foreach (var x in w) sum += x * (double)x;
            db[i] = 10 * Math.Log10(sum / win + 1e-12); // = 20·log10(rms)
        }
        return db;
    }

    /// <param name="startSec">UTC of sample 0 (seconds since the Unix epoch).</param>
    public static List<AudioPeak> Detect(ReadOnlySpan<float> samples, int rate, double startSec, ReactionOptions? o = null)
    {
        o ??= new ReactionOptions();
        var db = RmsDb(samples, rate, o);
        var peaks = new List<AudioPeak>();
        if (db.Length == 0) return peaks;
        var sorted = (double[])db.Clone(); Array.Sort(sorted);
        double median = sorted[sorted.Length / 2], thr = median + o.ThresholdDb;
        int minHops = (int)Math.Ceiling(o.MinSustainSec / o.HopSec);
        int i = 0;
        while (i < db.Length)
        {
            if (db[i] <= thr) { i++; continue; }
            int j = i; int best = i;
            while (j < db.Length && db[j] > thr) { if (db[j] > db[best]) best = j; j++; }
            if (j - i >= minHops)
            {
                double t0 = startSec + i * o.HopSec, t1 = startSec + (j - 1) * o.HopSec + o.WindowSec;
                peaks.Add(new AudioPeak(t0, t1, startSec + best * o.HopSec + o.WindowSec / 2, db[best], db[best] - median));
            }
            i = j;
        }
        // merge peaks closer than MergeSec
        var merged = new List<AudioPeak>();
        foreach (var p in peaks)
        {
            if (merged.Count > 0 && p.StartSec - merged[^1].EndSec <= o.MergeSec)
            {
                var m = merged[^1];
                merged[^1] = p.PeakDb > m.PeakDb
                    ? p with { StartSec = m.StartSec }
                    : m with { EndSec = p.EndSec };
            }
            else merged.Add(p);
        }
        return merged;
    }
}

public enum BodyEventKind { Jump, Dive, Celebration }

public sealed record BodyEvent(BodyEventKind Kind, double UtcSec, List<ulong> Players, double Strength);

/// <summary>Jumps, dives and round-end celebrations from demo poses (pure; demo time → UTC via the demo clock).</summary>
public static class BodyEvents
{
    private static double Sec(DateTime utc) => (utc - DateTime.UnixEpoch).TotalSeconds;

    public static List<BodyEvent> Detect(DemoData d, ReactionOptions? o = null)
    {
        o ??= new ReactionOptions();
        var list = new List<BodyEvent>();
        for (int slot = 0; slot < d.Players.Count; slot++)
        {
            var pts = d.Frames.SelectMany(f => f.Players.Where(p => p.Slot == slot && p.Alive).Select(p => (t: f.Time, y: (double)p.HeadPos.Y))).ToList();
            if (pts.Count < 10) continue;
            var ys = pts.Select(p => p.y).OrderBy(y => y).ToList();
            double baseline = ys[ys.Count / 2]; // standing height ≈ median head height of the round
            ulong id = d.Players[slot].ClientId;

            // jump: up ≥ JumpRise above the baseline within JumpRiseSec of leaving it, and back down within JumpReturnSec
            for (int i = 1; i < pts.Count; i++)
            {
                if (!(pts[i - 1].y < baseline + 0.05 && pts[i].y >= baseline + 0.05)) continue;
                int k = i; double peak = pts[i].y; int pk = i;
                while (k < pts.Count && pts[k].t - pts[i].t <= o.JumpReturnSec)
                {
                    if (pts[k].y > peak) { peak = pts[k].y; pk = k; }
                    if (pts[k].y < baseline + 0.05 && k > pk) break;
                    k++;
                }
                bool back = k < pts.Count && pts[k].y < baseline + 0.05 && pts[k].t - pts[i].t <= o.JumpReturnSec;
                if (peak - baseline >= o.JumpRise && pts[pk].t - pts[i].t <= o.JumpRiseSec && back)
                {
                    list.Add(new BodyEvent(BodyEventKind.Jump, Sec(d.UtcAt(pts[pk].t)), [id], peak - baseline));
                    i = k;
                }
            }
            // dive / deep crouch: head drops ≥ DiveDrop within DiveSec
            for (int i = 0; i < pts.Count; i++)
            {
                for (int j = i + 1; j < pts.Count && pts[j].t - pts[i].t < o.DiveSec; j++)
                    if (pts[i].y - pts[j].y >= o.DiveDrop)
                    {
                        list.Add(new BodyEvent(BodyEventKind.Dive, Sec(d.UtcAt(pts[j].t)), [id], pts[i].y - pts[j].y));
                        // skip ahead past this dive
                        double tEnd = pts[j].t + 1.0;
                        while (i < pts.Count && pts[i].t < tEnd) i++;
                        break;
                    }
            }
        }

        // celebration: ≥ N players of the winning team with a hand above the head within 5 s after RoundEnd
        foreach (var e in d.Events.Where(e => e.Kind == DemoRecord.RoundEnd))
        {
            var winner = (Team)e.Code;
            if (winner == Team.None) continue;
            var who = new HashSet<ulong>(); double tBest = e.Time;
            foreach (var f in d.Frames.Where(f => f.Time >= e.Time && f.Time <= e.Time + o.CelebrationSec))
                foreach (var p in f.Players.Where(p => p.Team == winner))
                    if (Math.Max(p.LeftPos.Y, p.RightPos.Y) > p.HeadPos.Y + 0.05 && who.Add(d.Players[p.Slot].ClientId)) tBest = f.Time;
            if (who.Count >= o.CelebrationMinPlayers)
                list.Add(new BodyEvent(BodyEventKind.Celebration, Sec(d.UtcAt(tBest)), who.ToList(), who.Count));
        }
        return list.OrderBy(b => b.UtcSec).ToList();
    }
}

public sealed record ReactionCandidate(double PeakSec, double Score, string Kind, List<ulong> Players, AudioPeak? Audio, List<BodyEvent> Body)
{
    public DateTime PeakUtc => DateTime.UnixEpoch.AddSeconds(PeakSec);
}

/// <summary>Combines audio peaks and body events into scored reactions; cooldown + max per session (pure).</summary>
public static class ReactionPlanner
{
    public static double AudioScore(AudioPeak p) => 10 + 2 * (p.AboveMedianDb - 15) + 5 * Math.Min(p.Duration, 3);
    public static double BodyScore(BodyEvent b) => b.Kind switch
    {
        BodyEventKind.Celebration => 25 + 5 * (b.Strength - 2),
        BodyEventKind.Jump => 12 + 10 * (b.Strength - 0.25),
        _ => 10,
    };

    /// <param name="nearestToCamera">For audio-only reactions: the player nearest to the camera mic at that UTC second (or null).</param>
    public static List<ReactionCandidate> Plan(IReadOnlyList<AudioPeak> audio, IReadOnlyList<BodyEvent> body, ReactionOptions? o = null,
                                               Func<double, ulong?>? nearestToCamera = null)
    {
        o ??= new ReactionOptions();
        // cluster everything that happens within MergeSec
        var items = audio.Select(a => (t: a.PeakSec, a: (AudioPeak?)a, b: (BodyEvent?)null))
            .Concat(body.Select(b => (t: b.UtcSec, a: (AudioPeak?)null, b: (BodyEvent?)b)))
            .OrderBy(x => x.t).ToList();
        var clusters = new List<List<(double t, AudioPeak? a, BodyEvent? b)>>();
        foreach (var it in items)
        {
            if (clusters.Count > 0 && it.t - clusters[^1][^1].t <= o.MergeSec) clusters[^1].Add(it);
            else clusters.Add([it]);
        }
        var cands = new List<ReactionCandidate>();
        foreach (var c in clusters)
        {
            var aud = c.Where(x => x.a != null).Select(x => x.a!).OrderByDescending(a => a.PeakDb).FirstOrDefault();
            var bodies = c.Where(x => x.b != null).Select(x => x.b!).ToList();
            double score = (aud != null ? AudioScore(aud) : 0) + bodies.Sum(BodyScore);
            // multiple jumpers at once is a bigger moment
            int jumpers = bodies.Where(b => b.Kind == BodyEventKind.Jump).SelectMany(b => b.Players).Distinct().Count();
            if (jumpers > 1) score += 4 * (jumpers - 1);
            double peak = aud?.PeakSec ?? bodies.OrderByDescending(BodyScore).First().UtcSec;
            var players = bodies.SelectMany(b => b.Players).Distinct().ToList();
            if (players.Count == 0 && nearestToCamera?.Invoke(peak) is ulong n) players.Add(n);
            string kind = bodies.Any(b => b.Kind == BodyEventKind.Celebration) ? "celebration"
                        : bodies.Any(b => b.Kind == BodyEventKind.Jump) ? "jump"
                        : bodies.Any(b => b.Kind == BodyEventKind.Dive) ? "dive" : "scream";
            cands.Add(new ReactionCandidate(peak, score, kind, players, aud, bodies));
        }
        // best first with cooldown, then chronological
        var chosen = new List<ReactionCandidate>();
        foreach (var c in cands.OrderByDescending(c => c.Score))
        {
            if (chosen.Count >= o.MaxPerSession) break;
            if (chosen.Any(x => Math.Abs(x.PeakSec - c.PeakSec) < o.CooldownSec)) continue;
            chosen.Add(c);
        }
        return chosen.OrderBy(c => c.PeakSec).ToList();
    }

    public static string Hook(string kind) => kind switch
    {
        "celebration" => "Round won. Real legs.",
        "jump" => "Jumped. In real life.",
        "dive" => "Hit the deck. For real.",
        _ => "When it gets real.",
    };

    /// <summary>Player nearest to a point (e.g. the camera) at a demo time.</summary>
    public static ulong? Nearest(DemoData d, double serverTime, Vector3 point)
    {
        var f = d.Frames.MinBy(f => Math.Abs(f.Time - serverTime));
        if (f == null || f.Players.Length == 0) return null;
        var p = f.Players.MinBy(p => Vector3.Distance(p.HeadPos, point));
        return d.Players[p.Slot].ClientId;
    }
}

/// <summary>Reaction edit: no cold open; 1x lead-in, 0.6x around the peak with punch + flash, 1x after, end card. 6–10 s.</summary>
public static class ReactionEdl
{
    public static Edl Build(double peakSrc, double windowSec, double fps)
    {
        var segs = new List<EdlSegment>();
        double outT = 0;
        void Add(EdlKind k, double dur, double a, double b, string l) { if (dur > 1e-6) { segs.Add(new EdlSegment(k, outT, dur, a, b, l)); outT += dur; } }
        double a0 = Math.Max(0, peakSrc - 3.5), s0 = Math.Max(a0, peakSrc - 0.3), s1 = Math.Min(windowSec, peakSrc + 0.5), b1 = Math.Min(windowSec, peakSrc + 3.0);
        Add(EdlKind.Play, s0 - a0, a0, s0, "lead-in");
        Add(EdlKind.Play, (s1 - s0) / 0.6, s0, s1, "peak 0.6x");
        double peakOut = segs.Count > 0 ? segs[^1].OutStart + (peakSrc - s0) / 0.6 : 0;
        Add(EdlKind.Play, b1 - s1, s1, b1, "after");
        double endCard = outT;
        Add(EdlKind.EndCard, EdlBuilder.EndCardSec, b1, b1, "end card");
        var notes = new List<string>();
        if (outT < 6) notes.Add($"short reaction: {outT:0.0} s");
        // ColdOpenEnd = −1: no cold open (and no rewind flash)
        return new Edl(segs, [new EdlKill(peakOut, 0, peakSrc, null)], -1, endCard, notes);
    }
}
