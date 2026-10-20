using VRLink.Composer;
using VRLink.Shared.Contracts;
using System.Text.Json;

namespace VRLink.Composer.Tests;

public class EdlTests
{
    private const double Fps = 30;

    private static void AssertContiguous(Edl e)
    {
        for (int i = 1; i < e.Segments.Count; i++) Assert.Equal(e.Segments[i - 1].OutEnd, e.Segments[i].OutStart, 9);
        Assert.Equal(0, e.Segments[0].OutStart);
    }

    [Fact]
    public void Structure_cold_open_rewind_freezes_final_slowmo_end_card()
    {
        double[] kills = [8.0, 10.5, 12.0];
        var e = EdlBuilder.Build(kills, windowSec: 18, Fps);
        AssertContiguous(e);
        var s = e.Segments;
        Assert.Equal(EdlKind.Play, s[0].Kind); Assert.Equal(1.0, s[0].OutDuration, 9); Assert.Equal(0.5, s[0].Speed, 9);
        Assert.InRange(kills[^1], s[0].SrcStart, s[0].SrcEnd); // cold open shows the decisive (last) kill
        Assert.Equal(EdlKind.Reverse, s[1].Kind); Assert.Equal(0.25, s[1].OutDuration, 9); Assert.Equal(4.0, s[1].Speed, 9);
        Assert.True(s[1].SrcEnd < s[1].SrcStart);
        Assert.Equal(1.25, e.ColdOpenEnd, 9);
        Assert.True(s[2].SrcStart <= kills[0] - EdlBuilder.LeadIn + 1e-9); // build-up from ≥ 2 s before the first kill (longer if the clip would be < 12 s)
        // one 3-frame freeze per kill, at the kill time
        var freezes = s.Where(x => x.Kind == EdlKind.Freeze).ToList();
        Assert.Equal(3, freezes.Count);
        Assert.All(freezes, f => Assert.Equal(3 / Fps, f.OutDuration, 9));
        Assert.Equal(kills, freezes.Select(f => f.SrcStart));
        // final kill: 0.4x for 1.2 s (around the freeze)
        var slow = s.Where(x => x.Kind == EdlKind.Play && Math.Abs(x.Speed - 0.4) < 1e-9).ToList();
        Assert.Equal(1.2, slow.Sum(x => x.OutDuration), 6);
        Assert.Equal(EdlKind.EndCard, s[^1].Kind); Assert.Equal(1.2, s[^1].OutDuration, 9);
        Assert.InRange(e.Duration, EdlBuilder.MinTotal, EdlBuilder.MaxTotal);
        Assert.Equal([1, 2, 3], e.Kills.Select(k => k.CountAfter));
        Assert.Equal(e.EndCardStart, s[^1].OutStart);
    }

    [Fact]
    public void Gaps_over_3s_are_sped_up_with_ramps_and_short_gaps_are_not()
    {
        var e = EdlBuilder.Build([5.0, 6.5, 15.0], windowSec: 22, Fps);
        AssertContiguous(e);
        var speeds = e.Segments.Where(s => s.Kind == EdlKind.Play).Select(s => Math.Round(s.Speed, 3)).ToList();
        Assert.Contains(3.0, speeds);
        Assert.Equal(2, speeds.Count(x => x == 2.0)); // one ramp up + one ramp down, only for the 8.5 s gap
        var fast = e.Segments.Single(s => Math.Abs(s.Speed - 3) < 1e-9);
        Assert.True(fast.SrcStart > 6.5 && fast.SrcEnd < 15.0);
        // no fast segment between kill 1 and 2 (1.5 s gap)
        Assert.DoesNotContain(e.Segments, s => s.Speed > 1.01 && s.SrcStart >= 5.0 && s.SrcEnd <= 6.5);
    }

    [Fact]
    public void Simultaneous_kills_are_one_freeze_and_the_counter_jumps()
    {
        var e = EdlBuilder.Build([10.0, 10.0, 10.05], windowSec: 20, Fps);
        Assert.Single(e.Segments, s => s.Kind == EdlKind.Freeze);
        Assert.Equal(3, e.Kills.Single().CountAfter);
    }

    [Fact]
    public void Source_mapping_stays_in_the_window_and_flash_and_punch_follow_kills()
    {
        var e = EdlBuilder.Build([4.0, 9.0, 16.0], windowSec: 21, Fps);
        int n = (int)Math.Round(e.Duration * Fps);
        for (int k = 0; k < n; k++)
        {
            var src = e.SourceAt(k / Fps);
            if (k / Fps >= e.EndCardStart - 1e-9) { Assert.Null(src); continue; }
            Assert.NotNull(src); Assert.InRange(src!.Value, 0, 21);
        }
        foreach (var kill in e.Kills)
        {
            Assert.Equal(1.0, e.FlashAt(kill.OutTime), 6);
            Assert.Equal(0, e.FlashAt(kill.OutTime + 0.07));
            Assert.Equal(EdlBuilder.PunchZoom, e.PunchAt(kill.OutTime), 6);
            Assert.True(e.PunchAt(kill.OutTime + 0.3) < EdlBuilder.PunchZoom / 2);
            Assert.Equal(kill.CountAfter, e.CountAt(kill.OutTime + 0.01));
        }
        // the main part (after the cold open) only moves forward in source time → streamable
        double last = -1;
        for (int k = (int)Math.Ceiling(e.ColdOpenEnd * Fps); k < n; k++)
        {
            var src = e.SourceAt(k / Fps);
            if (src == null) break;
            Assert.True(src.Value >= last - 1e-9, $"frame {k}: {src} < {last}");
            last = src.Value;
        }
    }

    [Fact]
    public void Long_highlights_are_compressed_into_the_target_length()
    {
        // 5 kills spread over 40 s
        var e = EdlBuilder.Build([3, 12, 20, 29, 38], windowSec: 44, Fps);
        Assert.InRange(e.Duration, EdlBuilder.MinTotal, EdlBuilder.MaxTotal);
        Assert.Contains(e.Segments, s => s.Speed > 3.01); // faster gaps were needed
    }

    [Fact]
    public void Short_footage_is_reported()
    {
        var e = EdlBuilder.Build([2.0], windowSec: 3.0, Fps);
        Assert.True(e.Duration < EdlBuilder.MinTotal);
        Assert.Contains(e.Notes, n => n.StartsWith("short"));
    }

    [Fact]
    public void Tempo_chains_stay_within_atempo_limits()
    {
        Assert.Equal("atempo=0.5,atempo=0.8", AudioBuilder.Tempo(0.4));
        Assert.Equal("atempo=2,atempo=1.5", AudioBuilder.Tempo(3));
        Assert.Equal("", AudioBuilder.Tempo(1));
        Assert.Equal("atempo=0.5", AudioBuilder.Tempo(0.5));
    }
}

public class ReframeTests
{
    private const double Fps = 30;

    private static List<TrackPoint> Track(int n, Func<int, (double x, double y, bool vis)> f) =>
        Enumerable.Range(0, n).Select(i => { var (x, y, v) = f(i); return new TrackPoint { X = x, Y = y, Visible = v, Depth = 4 }; }).ToList();

    [Fact]
    public void Crops_stay_inside_the_source_and_keep_the_aspect()
    {
        var rnd = new Random(1);
        var tr = Track(600, i => (rnd.NextDouble() * 3840, rnd.NextDouble() * 2160, rnd.NextDouble() > 0.1)); // wild jumps
        var r = Reframer.Compute(tr, 3840, 2160, Fps, [5, 12]);
        Assert.All(r.Crops, c =>
        {
            Assert.InRange(c.Left, -1e-6, 3840 - c.W + 1e-6);
            Assert.InRange(c.Top, -1e-6, 2160 - c.H + 1e-6);
            Assert.Equal(9.0 / 16, c.W / c.H, 6);
        });
    }

    [Fact]
    public void Push_in_toward_kills_is_limited_to_1_35x()
    {
        var tr = Track(600, i => (1920, 1000, true));
        var r = Reframer.Compute(tr, 3840, 2160, Fps, [10]);
        double zoomAtKill = 2160 / r.Crops[(int)(10 * Fps)].H;
        double zoomEarly = 2160 / r.Crops[(int)(3 * Fps)].H;
        Assert.InRange(zoomAtKill, 1.2, 1.35 + 1e-9);
        Assert.InRange(zoomEarly, 0.999, 1.01);
        Assert.All(r.Crops, c => Assert.True(2160 / c.H <= 1.35 + 1e-9));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Upscale_limit_disables_push_in_on_a_1080p_source_with_a_warning()
    {
        var r = Reframer.Compute(Track(300, i => (960, 500, true)), 1920, 1080, Fps, [5]);
        Assert.Equal(1, r.EffectiveMaxZoom);
        Assert.InRange(r.BaseUpscale, 1.77, 1.78);
        Assert.Contains(r.Warnings, w => w.Contains("upscaled"));
        // the split top half (960 px tall) from 1080p is fine
        var split = Reframer.Compute(Track(300, i => (960, 500, true)), 1920, 1080, Fps, [5], new ReframeOptions { Aspect = 1080.0 / 960, OutHeight = 960 });
        Assert.Equal(1.35, split.EffectiveMaxZoom, 6);
    }

    [Fact]
    public void Spring_is_critically_damped_no_overshoot_and_bounded_speed()
    {
        // hero jumps from x=1000 to x=2800 at frame 60
        var tr = Track(240, i => (i < 60 ? 1000 : 2800, 1000, true));
        var r = Reframer.Compute(tr, 3840, 2160, Fps, []);
        var xs = r.Crops.Select(c => c.Cx).ToList();
        double target = 2800 - new ReframeOptions().DeadZone * r.Crops[^1].W; // stops at the dead-zone edge
        Assert.True(xs.Max() <= 2800 + 1e-6, "overshoot");
        Assert.InRange(xs[^1], target - 5, 2800);
        double maxStep = xs.Zip(xs.Skip(1), (a, b) => Math.Abs(b - a)).Max();
        Assert.True(maxStep < 0.06 * r.Crops[0].W, $"step {maxStep:0.0} px/frame");
    }

    [Fact]
    public void Dead_zone_ignores_small_jitter()
    {
        var rnd = new Random(7);
        var tr = Track(300, i => (1920 + (rnd.NextDouble() - 0.5) * 60, 1000 + (rnd.NextDouble() - 0.5) * 40, true));
        var r = Reframer.Compute(tr, 3840, 2160, Fps, []);
        var xs = r.Crops.Skip(30).Select(c => c.Cx).ToList();
        Assert.True(xs.Max() - xs.Min() < 1.0, $"camera moved {xs.Max() - xs.Min():0.00} px for jitter");
    }

    [Fact]
    public void Hero_lost_holds_position_and_widens()
    {
        var tr = Track(300, i => i < 100 ? (2600, 900, true) : (0, 0, false));
        var r = Reframer.Compute(tr, 3840, 2160, Fps, [3.0]); // push-in around frame 90, then the hero is lost
        var lost = r.Crops[^1];
        Assert.InRange(lost.Cx, 2400, 2700);             // held near the last seen position (not jumping to 0,0)
        Assert.InRange(2160 / lost.H, 0.999, 1.02);       // widened back to 1x
    }

    [Fact]
    public void Hero_sits_in_the_upper_middle_of_the_frame()
    {
        // at full height the crop cannot move vertically; during the push-in at a kill it can
        var r = Reframer.Compute(Track(300, i => (1920, 1300, true)), 3840, 2160, Fps, [6.0]);
        var c = r.Crops[(int)(6.0 * Fps)];
        double rel = (1300 - c.Top) / c.H;
        Assert.InRange(rel, 0.33, 0.50);
    }
}

public class ContractAndGateTests
{
    [Fact]
    public void Clip_contract_v2_fields_roundtrip_and_stay_optional()
    {
        var m = new ClipManifest
        {
            HighlightId = "x-irl", Type = "Ace", Format = "irl", MusicFree = true,
            Sources = ["arenaCam:cam1", "overlay", "gameAudio", "roomAudio"],
            Players = [new ClipPlayer { Label = "TU 1", Team = "TU", Hero = true }],
        };
        var json = JsonSerializer.Serialize(m, ClipManifest.JsonOptions);
        Assert.Contains("\"format\": \"irl\"", json);
        Assert.Contains("\"musicFree\": true", json);
        Assert.Contains("\"arenaCam:cam1\"", json);
        var back = JsonSerializer.Deserialize<ClipManifest>(json, ClipManifest.JsonOptions)!;
        Assert.Equal("TU 1", back.Players![0].Label);
        var old = JsonSerializer.Deserialize<ClipManifest>("{\"highlightId\":\"a\",\"type\":\"Ace\"}", ClipManifest.JsonOptions)!;
        Assert.Null(old.Format); Assert.Null(old.Sources);
    }

    [Theory]
    [InlineData("PostMatch", 4, false, 5, true)]
    [InlineData("Lobby", 6, false, 5, true)]
    [InlineData("Boot", 0, false, 5, true)]
    [InlineData("Boot", 2, false, 5, false)]
    [InlineData("Match", 10, true, 5, false)]
    [InlineData("Lobby", 0, true, 5, false)]
    [InlineData("PostMatch", 0, false, 61, false)] // stale → unknown → wait
    public void Server_gate(string phase, int clients, bool running, int ageSec, bool allowed)
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var s = new ServerState { UtcNow = now.AddSeconds(-ageSec), Phase = phase, ConnectedClients = clients, MatchRunning = running };
        Assert.Equal(allowed, ServerGate.Evaluate(s, now).Allowed);
        Assert.False(ServerGate.Evaluate(null, now).Allowed);
    }

    [Fact]
    public void Hooks_per_type()
    {
        Assert.Equal("1 vs 3. In real life.", HookTemplates.For(new HighlightRecord { Type = "Clutch", ClutchVs = 3 }));
        Assert.Equal("5 kills. Real legs.", HookTemplates.For(new HighlightRecord { Type = "Ace", Kills = 5 }));
        Assert.Equal("This is real VR.", HookTemplates.For(new HighlightRecord { Type = "Unknown" }));
        Assert.True(new HighlightRecord { Type = "Kills3" }.WantsSplit);
        Assert.False(new HighlightRecord { Type = "OneTapStreak" }.WantsSplit);
    }

    [Fact]
    public void Text_plan_stays_in_safe_margins_and_ends_with_the_end_card()
    {
        var e = EdlBuilder.Build([5, 8, 13], 18, 30);
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkcomp", Guid.NewGuid().ToString("N"));
        var f = TextPlan.Build(new HighlightRecord { Type = "Kills3", Kills = 3 }, e, split: false, Fonts.Resolve(null, null), dir);
        Assert.StartsWith("[0:v]", f);
        Assert.EndsWith("format=yuv420p[v]", f);
        Assert.Contains("enable='lt(t,1.5)'", f); // hook only in the first 1.5 s
        Assert.Contains($"x={TextPlan.SafeLeft}", f);
        Assert.Contains($"x=w-text_w-{TextPlan.SafeRight}", f);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(f, "fontsize='96\\*").Count); // one counter pop per kill
        Assert.Contains(File.ReadAllLines(Directory.GetFiles(dir, "txt*.txt").First()), l => l.Contains("3 kills"));
        Directory.Delete(dir, true);
    }
}
