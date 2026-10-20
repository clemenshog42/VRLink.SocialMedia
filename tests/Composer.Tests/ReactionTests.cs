using System.Numerics;
using VRLink.Composer;
using VRLink.Shared.Demo;

namespace VRLink.Composer.Tests;

public class ReactionTests
{
    private const int Rate = 16000;
    private static readonly DateTime Anchor = new(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);
    private static double Sec(DateTime t) => (t - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Room tone at ~−50 dBFS with loud bursts (screams) of the given (start, length) at ~−20 dBFS, and a short click.</summary>
    private static float[] Room(double seconds, (double at, double len)[] bursts, (double at, double len)[]? clicks = null)
    {
        var rnd = new Random(3);
        var a = new float[(int)(seconds * Rate)];
        for (int i = 0; i < a.Length; i++) a[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.005);
        foreach (var (at, len) in bursts.Concat(clicks ?? []))
            for (int i = (int)(at * Rate); i < Math.Min(a.Length, (int)((at + len) * Rate)); i++)
                a[i] += (float)(0.15 * Math.Sin(2 * Math.PI * 700 * i / Rate) * (0.7 + 0.3 * rnd.NextDouble()));
        return a;
    }

    [Fact]
    public void Loud_sustained_peaks_are_found_short_clicks_ignored_and_close_peaks_merged()
    {
        double t0 = Sec(Anchor);
        var a = Room(60, [(10, 0.8), (12.0, 0.6), (40, 1.5)], clicks: [(25, 0.12)]);
        var peaks = LoudnessPeaks.Detect(a, Rate, t0);
        Assert.Equal(2, peaks.Count); // 10 s + 12 s merged (< 3 s apart), click at 25 s too short
        Assert.InRange(peaks[0].StartSec - t0, 9.8, 10.1);
        Assert.InRange(peaks[0].EndSec - t0, 12.4, 12.8);
        Assert.InRange(peaks[1].PeakSec - t0, 40, 41.5);
        Assert.All(peaks, p => Assert.True(p.AboveMedianDb > 15));
    }

    [Fact]
    public void Quiet_session_has_no_peaks()
    {
        Assert.Empty(LoudnessPeaks.Detect(Room(30, []), Rate, 0));
    }

    /// <summary>Synthetic round: 4 players standing (head 1.7 m, small sway); P1 jumps at 10 s, P2 dives at 20 s,
    /// RoundEnd at 30 s (TU wins) and P1+P3 (TU) raise their hands at 31 s.</summary>
    public static DemoData SyntheticRound(bool celebrate = true, bool jump = true, bool dive = true, DateTime? anchor = null, string venue = "test")
    {
        var an = anchor ?? Anchor;
        var d = new DemoData
        {
            Version = 3, MatchId = "abcdef1234", Round = 2, StartTime = 0, EndTime = 40, SampleRate = 30, CreatedUtc = an,
            V3 = new DemoV3Header { VenueId = venue, UtcAnchorTicks = an.Ticks, UtcAnchorServerTime = 0 },
        };
        var teams = new[] { Team.TacticalUnit, Team.Syndicate, Team.TacticalUnit, Team.Syndicate };
        for (int i = 0; i < 4; i++) d.Players.Add(new DemoPlayerInfo((ulong)(i + 1), teams[i], i >= 2, false, 1.7f, (teams[i] == Team.TacticalUnit ? "TU " : "SYN ") + (i + 1)));
        for (int f = 0; f <= 40 * 30; f++)
        {
            double t = f / 30.0;
            var fr = new DemoFrame { Time = t, Players = new DemoPlayerSample[4] };
            for (int i = 0; i < 4; i++)
            {
                double y = 1.7 + 0.02 * Math.Sin(t * 1.3 + i);
                if (jump && i == 0 && t >= 10 && t <= 10.6) y += 0.35 * Math.Sin((t - 10) / 0.6 * Math.PI);
                if (dive && i == 1 && t >= 20 && t <= 20.35) y -= 0.75 * (t - 20) / 0.35;
                if (dive && i == 1 && t > 20.35 && t < 22) y -= 0.75;
                var head = new Vector3(i * 1.5f, (float)y, 2);
                float handY = (float)(y - 0.5);
                if (celebrate && (i == 0 || i == 2) && t >= 31 && t <= 32.5) handY = (float)(y + 0.3);
                fr.Players[i] = new DemoPlayerSample
                {
                    Slot = i, Alive = true, Team = teams[i], HeadPos = head, HeadRot = Quaternion.Identity,
                    LeftPos = head with { X = head.X - 0.3f, Y = handY }, RightPos = head with { X = head.X + 0.3f, Y = handY },
                };
            }
            d.Frames.Add(fr);
        }
        d.Events.Add(new DemoEvent { Kind = DemoRecord.RoundEnd, Time = 30, Code = (byte)Team.TacticalUnit, Code2 = 0 });
        return d;
    }

    [Fact]
    public void Jump_dive_and_celebration_are_detected_with_players_and_utc()
    {
        var ev = BodyEvents.Detect(SyntheticRound());
        var jump = Assert.Single(ev, e => e.Kind == BodyEventKind.Jump);
        Assert.Equal([1UL], jump.Players);
        Assert.InRange(jump.UtcSec - Sec(Anchor), 10.1, 10.5);
        var dive = Assert.Single(ev, e => e.Kind == BodyEventKind.Dive);
        Assert.Equal([2UL], dive.Players);
        var cel = Assert.Single(ev, e => e.Kind == BodyEventKind.Celebration);
        Assert.Equal(new[] { 1UL, 3UL }, cel.Players.Order());
        Assert.InRange(cel.UtcSec - Sec(Anchor), 31, 32.6);
    }

    [Fact]
    public void Standing_sway_and_a_lone_hand_raise_are_not_reactions()
    {
        var ev = BodyEvents.Detect(SyntheticRound(celebrate: false, jump: false, dive: false));
        Assert.Empty(ev);
    }

    [Fact]
    public void Planner_merges_audio_with_body_applies_cooldown_and_max_three()
    {
        double t0 = Sec(Anchor);
        var audio = new List<AudioPeak>
        {
            new(t0 + 10, t0 + 11, t0 + 10.4, -20, 30),  // with the jump → one reaction
            new(t0 + 100, t0 + 100.5, t0 + 100.2, -25, 18),
            new(t0 + 110, t0 + 110.5, t0 + 110.2, -22, 24), // within the 20 s cooldown of 100 s → only the better one
            new(t0 + 200, t0 + 201, t0 + 200.5, -21, 26),
            new(t0 + 300, t0 + 300.3, t0 + 300.1, -28, 16),
        };
        var body = new List<BodyEvent> { new(BodyEventKind.Jump, t0 + 10.3, [1], 0.35) };
        var plan = ReactionPlanner.Plan(audio, body, null, _ => 7UL);
        Assert.Equal(3, plan.Count);
        Assert.Equal("jump", plan[0].Kind);
        Assert.Equal([1UL], plan[0].Players);
        Assert.Equal(t0 + 10.4, plan[0].PeakSec, 6); // peak = loudest audio moment
        Assert.DoesNotContain(plan, p => Math.Abs(p.PeakSec - (t0 + 100.2)) < 1); // lost to 110 s (louder)
        Assert.True(plan.Zip(plan.Skip(1)).All(x => x.Second.PeakSec - x.First.PeakSec >= 20));
        Assert.Contains(plan, p => p.Kind == "scream" && p.Players.SequenceEqual([7UL])); // audio-only → nearest player
    }

    [Fact]
    public void Reaction_edit_is_6_to_10_s_without_cold_open_with_punch_on_the_peak()
    {
        var e = ReactionEdl.Build(peakSrc: 4.0, windowSec: 8.0, fps: 30);
        Assert.InRange(e.Duration, 6, 10);
        Assert.Equal(EdlKind.Play, e.Segments[0].Kind);
        Assert.Equal(0.5, e.Segments[0].SrcStart, 6); // starts 3.5 s before the peak, no cold open
        Assert.Equal(0, e.FlashAt(0.01)); // no rewind flash at the start
        var k = Assert.Single(e.Kills);
        Assert.Equal(0, k.CountAfter); // no kill counter
        Assert.Equal(4.0, e.SourceAt(k.OutTime)!.Value, 3);
        Assert.Equal(EdlBuilder.PunchZoom, e.PunchAt(k.OutTime), 6);
        Assert.Contains(e.Segments, s => Math.Abs(s.Speed - 0.6) < 1e-6);
        Assert.Equal(EdlKind.EndCard, e.Segments[^1].Kind);
    }

    [Fact]
    public void Hooks_per_reaction_kind()
    {
        Assert.Equal("When it gets real.", ReactionPlanner.Hook("scream"));
        Assert.Equal("Round won. Real legs.", ReactionPlanner.Hook("celebration"));
    }
}
