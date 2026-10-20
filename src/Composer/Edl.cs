namespace VRLink.Composer;

public enum EdlKind { Play, Reverse, Freeze, EndCard }

/// <summary>
/// One piece of the edit. Source times are seconds from the window start (frame 0 = utcFrom of the footage/overlay/pov/game audio,
/// which are all cut to the same window). Output times are seconds in the final clip.
/// Play: SrcStart→SrcEnd at Speed (0.4 = slow-mo, 3 = fast). Reverse: SrcStart→SrcEnd with SrcEnd &lt; SrcStart.
/// Freeze: SrcStart held. EndCard: no source.
/// </summary>
public sealed record EdlSegment(EdlKind Kind, double OutStart, double OutDuration, double SrcStart, double SrcEnd, string Label)
{
    public double OutEnd => OutStart + OutDuration;
    public double Speed => Kind switch
    {
        EdlKind.Play or EdlKind.Reverse => Math.Abs(SrcEnd - SrcStart) / OutDuration,
        _ => 0,
    };

    /// <summary>Source time shown at output time t (t inside this segment).</summary>
    public double SourceAt(double outT)
    {
        double u = OutDuration <= 0 ? 0 : Math.Clamp((outT - OutStart) / OutDuration, 0, 1);
        return Kind switch
        {
            EdlKind.Play or EdlKind.Reverse => SrcStart + (SrcEnd - SrcStart) * u,
            _ => SrcStart,
        };
    }
}

/// <summary>A kill as it appears in the output: counter pop, white flash, zoom punch.</summary>
public sealed record EdlKill(double OutTime, int CountAfter, double SrcTime, string? VictimLabel);

public sealed record Edl(List<EdlSegment> Segments, List<EdlKill> Kills, double ColdOpenEnd, double EndCardStart, List<string> Notes)
{
    public double Duration => Segments.Count == 0 ? 0 : Segments[^1].OutEnd;

    public EdlSegment SegmentAt(double outT)
    {
        foreach (var s in Segments) if (outT < s.OutEnd - 1e-9) return s;
        return Segments[^1];
    }

    /// <summary>Source time for output time t; null during the end card.</summary>
    public double? SourceAt(double outT)
    {
        var s = SegmentAt(outT);
        return s.Kind == EdlKind.EndCard ? null : s.SourceAt(outT);
    }

    /// <summary>White flash strength 0..1 at output time t (60 ms after each kill + 60 ms after the rewind).</summary>
    public double FlashAt(double outT, double flashSec = EdlBuilder.FlashSec)
    {
        double f = 0;
        foreach (var k in Kills)
            if (outT >= k.OutTime && outT < k.OutTime + flashSec) f = Math.Max(f, 1 - (outT - k.OutTime) / flashSec);
        if (outT >= ColdOpenEnd && outT < ColdOpenEnd + flashSec) f = Math.Max(f, 0.8 * (1 - (outT - ColdOpenEnd) / flashSec));
        return f;
    }

    /// <summary>Extra zoom (0..0.06) of the kill punch, easing out.</summary>
    public double PunchAt(double outT)
    {
        double p = 0;
        foreach (var k in Kills)
            if (outT >= k.OutTime && outT < k.OutTime + 0.6) p = Math.Max(p, EdlBuilder.PunchZoom * Math.Pow(1 - (outT - k.OutTime) / 0.6, 3));
        return p;
    }

    /// <summary>Kill count shown at output time t (counter; 0 before the first kill of the main part).</summary>
    public int CountAt(double outT) => Kills.Where(k => k.OutTime <= outT).Select(k => k.CountAfter).DefaultIfEmpty(0).Max();
}

/// <summary>
/// The punchy edit (pure, unit-tested). Same EDL drives footage, overlay, POV and both audio tracks, so they stay in sync.
/// Structure: cold open (decisive kill, 1.0 s at 0.5x) → 0.25 s rewind (4x reverse + flash) → build-up from 2 s before the first kill
/// → per kill: 3-frame freeze + 60 ms white flash + 6 % zoom punch → gaps &gt; 3 s between kills at 3x with 2x ramps
/// → final kill 1.2 s at 0.4x → short tail → 1.2 s end card. Target length 12–20 s (lead-in/tail/gap speed adapt).
/// </summary>
public static class EdlBuilder
{
    public const double ColdOpenOut = 1.0, ColdOpenSpeed = 0.5, RewindOut = 0.25, RewindSpeed = 4.0;
    public const double LeadIn = 2.0, GapThreshold = 3.0, GapSpeed = 3.0, RampSpeed = 2.0, RampOut = 0.3;
    public const double FinalSlowSpeed = 0.4, FinalSlowOut = 1.2, EndCardSec = 1.2, FlashSec = 0.06, PunchZoom = 0.06;
    public const double MinTotal = 12, MaxTotal = 20, ClusterSec = 0.15;

    /// <param name="killSrc">Kill times in source seconds (from the window start).</param>
    /// <param name="windowSec">Available source length.</param>
    public static Edl Build(IReadOnlyList<double> killSrc, double windowSec, double fps, IReadOnlyList<string?>? victims = null)
    {
        if (killSrc.Count == 0) throw new ArgumentException("no kills");
        double frame = 1 / fps;
        // cluster simultaneous kills (an ace by grenade arrives in one frame): one freeze, counter +n
        var order = killSrc.Select((t, i) => (t, i)).OrderBy(x => x.t).ToList();
        var clusters = new List<(double t, int n, string? victim)>();
        foreach (var (t, i) in order)
        {
            if (clusters.Count > 0 && t - clusters[^1].t <= ClusterSec) clusters[^1] = (clusters[^1].t, clusters[^1].n + 1, clusters[^1].victim);
            else clusters.Add((Math.Clamp(t, 0, windowSec), 1, victims?.ElementAtOrDefault(i)));
        }

        Edl? best = null;
        // try progressively shorter/longer variants until the total is in [12, 20]
        foreach (var (lead, tail, gapSpeed) in Variants())
        {
            var e = BuildOnce(clusters, windowSec, frame, lead, tail, gapSpeed);
            if (best == null || Score(e.Duration) < Score(best.Duration)) best = e;
            if (e.Duration >= MinTotal && e.Duration <= MaxTotal) { best = e; break; }
        }
        if (best!.Duration < MinTotal - 0.05) best.Notes.Add($"short: {best.Duration:0.0} s (not enough footage around the kills)");
        if (best.Duration > MaxTotal + 0.05) best.Notes.Add($"long: {best.Duration:0.0} s");
        return best;
    }

    private static double Score(double d) => d < MinTotal ? MinTotal - d : d > MaxTotal ? d - MaxTotal : 0;

    private static IEnumerable<(double lead, double tail, double gapSpeed)> Variants()
    {
        yield return (LeadIn, 0.8, GapSpeed);
        // too short → more lead-in and tail
        foreach (var l in new[] { 3.0, 4.0, 5.0, 6.0, 8.0 }) yield return (l, 1.5, GapSpeed);
        foreach (var t in new[] { 2.5, 3.5, 5.0 }) yield return (8.0, t, GapSpeed);
        // too long → shorter lead-in, faster gaps
        foreach (var g in new[] { 4.0, 5.0, 6.0, 8.0 }) yield return (1.0, 0.5, g);
    }

    private static Edl BuildOnce(List<(double t, int n, string? victim)> clusters, double windowSec, double frame, double lead, double tail, double gapSpeed)
    {
        var segs = new List<EdlSegment>();
        var kills = new List<EdlKill>();
        var notes = new List<string>();
        double outT = 0;
        void Add(EdlKind k, double outDur, double a, double b, string label)
        {
            if (outDur <= 1e-6) return;
            segs.Add(new EdlSegment(k, outT, outDur, a, b, label));
            outT += outDur;
        }

        // 1) cold open: the decisive (last) kill, 1.0 s at 0.5x → 0.5 s of source centred slightly before the kill
        var decisive = clusters[^1].t;
        double coSrc = ColdOpenOut * ColdOpenSpeed;
        double coA = Math.Clamp(decisive - coSrc * 0.6, 0, Math.Max(0, windowSec - coSrc));
        Add(EdlKind.Play, ColdOpenOut, coA, coA + coSrc, "cold open (decisive kill 0.5x)");
        // 2) rewind: 0.25 s output, 4x reverse
        double rwSrc = RewindOut * RewindSpeed;
        Add(EdlKind.Reverse, RewindOut, coA + coSrc, Math.Max(0, coA + coSrc - rwSrc), "rewind 4x");
        double coldOpenEnd = outT;

        // 3) build-up from `lead` s before the first kill
        double src = Math.Max(0, clusters[0].t - lead);
        int count = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            var (k, n, victim) = clusters[i];
            bool final = i == clusters.Count - 1;
            if (final)
            {
                // final kill: 1.2 s output at 0.4x → 0.48 s source around the kill; the freeze is inside it
                double slowSrc = FinalSlowOut * FinalSlowSpeed;
                double slowA = Math.Max(src, k - slowSrc * 0.45);
                Add(EdlKind.Play, Math.Max(0, slowA - src), src, slowA, "approach final kill 1x");
                double before = (k - slowA) / FinalSlowSpeed;
                Add(EdlKind.Play, before, slowA, k, "final kill 0.4x");
                count += n;
                kills.Add(new EdlKill(outT, count, k, victim));
                Add(EdlKind.Freeze, 3 * frame, k, k, "freeze");
                double afterSrc = Math.Min(slowSrc - (k - slowA), windowSec - k);
                Add(EdlKind.Play, afterSrc / FinalSlowSpeed, k, k + afterSrc, "final kill 0.4x (after)");
                src = k + afterSrc;
                double tailSrc = Math.Min(tail, windowSec - src);
                Add(EdlKind.Play, tailSrc, src, src + tailSrc, "tail");
                src += tailSrc;
                break;
            }
            // play up to the kill, freeze 3 frames
            Add(EdlKind.Play, k - src, src, k, $"to kill {i + 1}");
            count += n;
            kills.Add(new EdlKill(outT, count, k, victim));
            Add(EdlKind.Freeze, 3 * frame, k, k, "freeze");
            src = k;
            double next = clusters[i + 1].t;
            double gap = next - k;
            if (gap > GapThreshold)
            {
                // 0.5 s at 1x after the kill, ramp 2x, fast 3x, ramp 2x, 1 s at 1x before the next kill
                double rampSrc = RampOut * RampSpeed;
                double a = k + 0.5, d = next - 1.0;
                double fastA = a + rampSrc, fastB = d - rampSrc;
                if (fastB > fastA)
                {
                    Add(EdlKind.Play, a - src, src, a, "after kill");
                    Add(EdlKind.Play, RampOut, a, fastA, "ramp up 2x");
                    Add(EdlKind.Play, (fastB - fastA) / gapSpeed, fastA, fastB, $"gap {gapSpeed:0}x");
                    Add(EdlKind.Play, RampOut, fastB, d, "ramp down 2x");
                    src = d;
                }
            }
        }
        double endCardStart = outT;
        Add(EdlKind.EndCard, EndCardSec, src, src, "end card");
        return new Edl(segs, kills, coldOpenEnd, endCardStart, notes);
    }
}
