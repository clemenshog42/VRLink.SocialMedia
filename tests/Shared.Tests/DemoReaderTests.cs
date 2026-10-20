using System.Numerics;
using VRLink.Shared.Demo;

namespace VRLink.Shared.Tests;

public class DemoReaderTests
{
    /// <summary>tests/data is copied next to the test assembly (see csproj).</summary>
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Golden file: a real round demo recorded by the Unity server (copied from VRCsGo/Builds/Highlights/demos).</summary>
    [Fact]
    public void Golden_real_demo_matches_unity_counts()
    {
        var d = DemoReader.ReadFile(Path.Combine(DataDir, "b1dcee9dbfd448208d5febaa8c037f0c_r04.vrdemo"));

        Assert.Equal(507, d.Frames.Count);
        Assert.Equal(6, d.Players.Count);
        Assert.Equal(d.HeaderFrames, d.Frames.Count);
        Assert.Equal(d.HeaderEvents, d.Events.Count); // Unity counts events without FreeObjects records
        Assert.Equal("b1dcee9dbfd448208d5febaa8c037f0c", d.MatchId);
        Assert.Equal(4, d.Round);
        Assert.InRange(d.Version, (ushort)1, (ushort)2);
        Assert.True(d.EndTime > d.StartTime);
        Assert.Equal(d.Frames.OrderBy(f => f.Time).Select(f => f.Time), d.Frames.Select(f => f.Time)); // time-ordered
        Assert.All(d.Frames, f => Assert.InRange(f.Time, d.StartTime - 0.5, d.EndTime + 0.5));
        Assert.All(d.Frames.SelectMany(f => f.Players), p =>
        {
            Assert.InRange(p.Slot, 0, d.Players.Count - 1);
            Assert.InRange(p.HeadPos.Y, -5f, 10f);                      // plausible arena coordinates
            Assert.InRange(p.HeadRot.LengthSquared(), 0.98f, 1.02f);   // normalised quaternions
        });
        Assert.All(d.Players, p => Assert.False(string.IsNullOrEmpty(p.Label)));
        Assert.False(d.Clock.Exact); // v1/v2 have no UTC anchor
    }

    [Fact]
    public void All_real_demos_in_the_game_folder_parse_if_present()
    {
        const string dir = @"C:\vr\VRCsGo\Builds\Highlights\demos";
        if (!Directory.Exists(dir)) return; // other machines: nothing to compare
        foreach (var f in Directory.GetFiles(dir, "*.vrdemo"))
        {
            var d = DemoReader.ReadFile(f);
            Assert.Equal(d.HeaderFrames, d.Frames.Count);
            Assert.True(d.Players.Count > 0, f);
        }
    }

    [Theory]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    public void Synthetic_v1_v2_roundtrip(ushort version)
    {
        var w = new DemoTestWriter { Version = version, StartTime = 100, EndTime = 101 };
        w.Players.Add(new DemoPlayerInfo(7, Team.Syndicate, false, true, 1.6f, "SY 1"));
        w.Frames.Add((100.0, [(new Vector3(1, 1.7f, 2), new Vector3(0.8f, 1.2f, 2.2f), new Vector3(1.2f, 1.2f, 2.2f))]));
        w.PhaseEvents.Add(100.5);
        var d = DemoReader.Read(new MemoryStream(w.Build()));
        Assert.Equal(version, d.Version);
        Assert.Equal(version >= 2 ? "Arena" : "", d.MapScene);
        Assert.Single(d.Frames);
        Assert.Equal(new Vector3(1, 1.7f, 2), d.Frames[0].Players[0].HeadPos);
        Assert.Equal("SY 1", d.Players[0].Label);
        Assert.Equal(RoundPhase.Action, (RoundPhase)d.Events.Single().Code);
        Assert.Null(d.V3);
    }

    [Fact]
    public void V3_stub_reads_anchor_venue_and_boundary_and_maps_time()
    {
        var anchor = new DateTime(2026, 10, 20, 18, 30, 0, 123, DateTimeKind.Utc);
        var w = new DemoTestWriter
        {
            Version = 3, StartTime = 500, EndTime = 530,
            V3 = new DemoV3Header { UtcAnchorTicks = anchor.Ticks, UtcAnchorServerTime = 512.25, VenueId = "home", Boundary = [new(-4, -3), new(4, -3), new(4, 3), new(-4, 3)] },
        };
        w.Players.Add(new DemoPlayerInfo(1, Team.TacticalUnit, false, false, 1.7f, "TU 1"));
        w.Frames.Add((512.25, [(Vector3.Zero, Vector3.Zero, Vector3.Zero)]));
        var d = DemoReader.Read(new MemoryStream(w.Build()));

        Assert.Equal(3, d.Version);
        Assert.Equal("home", d.V3!.VenueId);
        Assert.Equal(4, d.V3.Boundary.Length);
        Assert.True(d.Clock.Exact);
        Assert.Equal(anchor, d.Clock.ToUtc(512.25));
        Assert.Equal(anchor.AddMilliseconds(1500), d.Clock.ToUtc(513.75));
        Assert.Equal(510.0, d.Clock.ToServerTime(anchor.AddSeconds(-2.25)), 6);
    }

    [Fact]
    public void Rejects_unknown_version_and_bad_magic()
    {
        var w = new DemoTestWriter { Version = 4 };
        Assert.Throws<InvalidDataException>(() => DemoReader.Read(new MemoryStream(w.Build())));
        Assert.Throws<InvalidDataException>(() => DemoReader.Read(new MemoryStream("NOPE...."u8.ToArray())));
    }
}
