using System.Globalization;

namespace VRLink.Composer;

public enum MontagePieceKind { Hook, Play, EndCard }

/// <summary>One piece of the montage. Consecutive pieces overlap by <see cref="MontageEdl.Transition"/> (xfade + acrossfade), so there is never dead air.</summary>
/// <param name="Rank">Play: its rank (N..1). Hook / end card: 1 (the #1 play).</param>
/// <param name="Clip">Source clip; null = end card rendered by us (brand text on a dark card).</param>
public sealed record MontagePiece(MontagePieceKind Kind, int Rank, MontageClip? Clip, double SrcStart, double Duration, double OutStart)
{
    public double OutEnd => OutStart + Duration;
}

public sealed record MontageEdl(IReadOnlyList<MontagePiece> Pieces, double Transition, int Fps, IReadOnlyList<string> Warnings)
{
    public double Total => Pieces[^1].OutEnd;
    public int TotalFrames => (int)Math.Round(Total * Fps);
    public IEnumerable<MontagePiece> Plays => Pieces.Where(p => p.Kind == MontagePieceKind.Play);
}

public sealed class MontagePlanOptions
{
    public int Fps { get; init; } = 30;
    public double Hook { get; init; } = 1.2;
    public double Transition { get; init; } = 0.2;
    public double MinSegment { get; init; } = 4;
    public double MaxSegment { get; init; } = 6;
    public double MaxSegmentShortMontage { get; init; } = 8;
    public double MinTotal { get; init; } = 25;
    public double TargetTotal { get; init; } = 30;
    public double MaxTotal { get; init; } = 45;
    /// <summary>The source end card is cut away; this much extra gameplay is dropped before it (no end-card flash in the cut).</summary>
    public double EndCardMargin { get; init; } = 0.1;
    /// <summary>Longest end card piece (the #1 clip's own end card, normally 1.5-1.6 s).</summary>
    public double EndCardMax { get; init; } = 1.8;
    /// <summary>Length of the end card we render ourselves when the #1 clip has none.</summary>
    public double RenderedEndCard { get; init; } = 1.8;
}

public static class MontagePlanner
{
    /// <summary>Default length of the VRLink end card at the end of every clip (Unity ClipRenderer 1.6 s; older clips ~1.5 s).</summary>
    public const double DefaultEndCardSec = 1.6;

    /// <summary>Where the end card of a clip starts: manifest hint, else <paramref name="detected"/> (frame analysis), else duration - 1.6 s.</summary>
    public static double EndCardStart(MontageClip c, double? detected) =>
        c.EndCardSec is { } e && e > 0 && e < c.DurationSec ? e : detected ?? Math.Max(0, c.DurationSec - DefaultEndCardSec);

    private static double Q(double t, int fps) => Math.Round(t * fps) / fps;
    private static double Floor(double t, int fps) => Math.Floor(t * fps + 1e-6) / fps;

    /// <param name="ranked">Plays best first (index 0 = #1).</param>
    /// <param name="endCardStarts">Start of the end card per clip id (see <see cref="EndCardStart"/>); the clip's duration when it has none.</param>
    /// <param name="renderEndCard">Always render our own end card instead of reusing the #1 clip's.</param>
    public static MontageEdl Build(IReadOnlyList<MontageClip> ranked, IReadOnlyDictionary<string, double> endCardStarts, MontagePlanOptions? o = null, bool renderEndCard = false)
    {
        o ??= new MontagePlanOptions();
        int n = ranked.Count;
        if (n < MontageSelector.MinTop) throw new ArgumentException($"a montage needs at least {MontageSelector.MinTop} clips, got {n}");
        int fps = o.Fps; double T = Q(o.Transition, fps);
        var warnings = new List<string>();

        double MainEnd(MontageClip c) => Floor(Math.Max(0.5, (endCardStarts.TryGetValue(c.HighlightId, out var e) ? e : c.DurationSec) - o.EndCardMargin), fps);
        var best = ranked[0];
        double bestEc = endCardStarts.TryGetValue(best.HighlightId, out var be) ? be : best.DurationSec;
        double ecAvail = Floor(best.DurationSec - bestEc, fps);
        bool reuseEnd = !renderEndCard && ecAvail >= 0.8;
        double endDur = reuseEnd ? Floor(Math.Min(o.EndCardMax, ecAvail), fps) : Q(o.RenderedEndCard, fps);
        double hook = Q(o.Hook, fps);

        // segment length: aim at TargetTotal; 4..6 s, up to 8 s when the montage would otherwise be shorter than MinTotal
        // total = hook + n*L + endDur - (n+1)*T
        double Total(double l) => hook + n * l + endDur - (n + 1) * T;
        double L = Math.Clamp((o.TargetTotal - hook - endDur + (n + 1) * T) / n, o.MinSegment, o.MaxSegment);
        double cap = o.MaxSegment;
        if (Total(L) < o.MinTotal) { cap = o.MaxSegmentShortMontage; L = Math.Clamp((o.MinTotal + 0.5 - hook - endDur + (n + 1) * T) / n, o.MinSegment, cap); }
        if (Total(L) > o.MaxTotal) L = Math.Max(2, (o.MaxTotal - hook - endDur + (n + 1) * T) / n);
        L = Floor(L, fps);

        // per-clip length (short clips give their slack to the others, up to the cap)
        var avail = ranked.Select(c => MainEnd(c)).ToArray();
        var len = avail.Select(a => Math.Min(L, a)).ToArray();
        double wantTotal = Math.Min(o.TargetTotal, o.MaxTotal);
        for (int pass = 0; pass < 4 && Total(0) + len.Sum() < wantTotal - 0.05; pass++)
        {
            double deficit = wantTotal - (Total(0) + len.Sum());
            var spare = Enumerable.Range(0, n).Where(i => len[i] < Math.Min(cap, avail[i]) - 1e-6).ToList();
            if (spare.Count == 0) break;
            foreach (var i in spare) len[i] = Floor(Math.Min(Math.Min(cap, avail[i]), len[i] + deficit / spare.Count), fps);
        }
        for (int i = 0; i < n; i++) len[i] = Floor(len[i], fps);

        // window of every play: strongest part = the last seconds before the end card (the decisive slow-mo sits there), or around the manifest peak
        double Start(MontageClip c, double dur, double mainEnd, double peakAt)
            => c.PeakSec is { } p ? Math.Clamp(p - peakAt * dur, 0, Math.Max(0, mainEnd - dur)) : Math.Max(0, mainEnd - dur);

        var specs = new List<(MontagePieceKind K, int Rank, MontageClip? C, double Src, double Dur)>();
        double hookStart = Start(best, hook, MainEnd(best), 0.5);
        specs.Add((MontagePieceKind.Hook, 1, best, Q(hookStart, fps), hook));
        for (int i = n - 1; i >= 0; i--)
            specs.Add((MontagePieceKind.Play, i + 1, ranked[i], Q(Start(ranked[i], len[i], avail[i], 0.7), fps), len[i]));
        specs.Add((MontagePieceKind.EndCard, 1, reuseEnd ? best : null, reuseEnd ? Q(bestEc, fps) : 0, endDur));

        var pieces = new List<MontagePiece>();
        double outStart = 0;
        foreach (var (k, rank, c, src, dur) in specs)
        {
            pieces.Add(new MontagePiece(k, rank, c, src, dur, outStart));
            outStart += dur - T;
        }
        var edl = new MontageEdl(pieces, T, fps, warnings);
        if (edl.Total < o.MinTotal - 0.01) warnings.Add($"montage is {edl.Total:0.0} s (< {o.MinTotal:0} s): the source clips are too short");
        if (edl.Total > o.MaxTotal + 0.01) warnings.Add($"montage is {edl.Total:0.0} s (> {o.MaxTotal:0} s)");
        foreach (var p in pieces.Where(p => p.Kind == MontagePieceKind.Play && p.Duration < o.MinSegment - 0.01))
            warnings.Add($"#{p.Rank} {p.Clip!.HighlightId}: only {p.Duration:0.0} s of gameplay before the end card");
        return edl;
    }

    public static string Summary(MontageEdl e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in e.Pieces)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{p.OutStart,6:0.00} +{p.Duration,5:0.00}  {p.Kind,-7} {(p.Kind == MontagePieceKind.Play ? "#" + p.Rank : "  "),-3} {p.Clip?.HighlightId ?? "(rendered)",-16} src {p.SrcStart,6:0.00}"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"total {e.Total:0.00} s ({e.TotalFrames} frames @ {e.Fps} fps)"));
        return sb.ToString();
    }
}
