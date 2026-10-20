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
        Assert.False(d.HasUtcAnchor); // v1/v2 have no UTC anchor
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
    public void Synthetic_v3_reads_header_and_maps_time_with_last_sync_before_t()
    {
        var anchor = new DateTime(2026, 10, 20, 18, 30, 0, 123, DateTimeKind.Utc);
        var w = new DemoTestWriter
        {
            Version = 3, StartTime = 500, EndTime = 530,
            V3 = new DemoV3Header { Kind = 1, VenueId = "home", UtcAnchorTicks = anchor.Ticks, UtcAnchorServerTime = 500, BoundaryBuffer = 0.3f,
                                    Outline = [new(-4, -3), new(4, -3), new(4, 3), new(-4, 3)] },
        };
        // hitch at ~505: server time fell 0.8 s behind the wall clock; the recorder writes a fresh sync
        w.UtcSyncs.Add((510.0, anchor.AddSeconds(10.8).Ticks));
        w.Players.Add(new DemoPlayerInfo(1, Team.TacticalUnit, false, false, 1.7f, "TU 1"));
        w.Frames.Add((500.0, [(Vector3.Zero, Vector3.Zero, Vector3.Zero)]));
        var d = DemoReader.Read(new MemoryStream(w.Build()));

        Assert.Equal(3, d.Version);
        Assert.True(d.V3!.IsCalibration);
        Assert.Equal("home", d.V3.VenueId);
        Assert.Equal(0.3f, d.V3.BoundaryBuffer);
        Assert.Equal(4, d.V3.Outline.Length);
        Assert.True(d.HasUtcAnchor);
        Assert.Equal(d.HeaderEvents, d.Events.Count);
        Assert.Equal(anchor.AddSeconds(2), d.UtcAt(502));              // header anchor
        Assert.Equal(anchor.AddSeconds(10.8 + 2.5), d.UtcAt(512.5));   // last sync before t, no interpolation
        Assert.Equal(512.5, d.ServerTimeAt(anchor.AddSeconds(13.3)), 6);
        Assert.Equal(502.0, d.ServerTimeAt(anchor.AddSeconds(2)), 6);
    }

    /// <summary>Golden files: real v3 calibration recordings from the Unity server (copied from Builds/Highlights/demos/calib).</summary>
    [Theory]
    [InlineData("calib_20261020_065546Z.vrdemo", 2, 35.0, 0)]
    [InlineData("calib_20261020_065807Z.vrdemo", 8, 33.3, 13)]
    public void Golden_v3_calibration_demos(string file, int players, double seconds, int outline)
    {
        var d = DemoReader.ReadFile(Path.Combine(DataDir, file));
        Assert.Equal(3, d.Version);
        Assert.NotNull(d.V3);
        Assert.True(d.V3!.IsCalibration);
        Assert.Equal(players, d.Players.Count);
        Assert.InRange(d.EndTime - d.StartTime, seconds - 0.2, seconds + 0.2);
        Assert.Equal(d.HeaderFrames, d.Frames.Count);
        Assert.Equal(d.HeaderEvents, d.Events.Count);
        Assert.True(d.HasUtcAnchor);
        if (outline > 0) Assert.Equal(outline, d.V3.Outline.Length);
        // UTC of the recording start is close to the time in the file name
        var nameUtc = DateTime.ParseExact(file[6..22], "yyyyMMdd_HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.InRange((d.UtcAt(d.StartTime) - nameUtc).TotalSeconds, -60, 60);
        // UTC is monotonic over the frames
        var utc = d.Frames.Select(f => d.UtcAt(f.Time)).ToList();
        Assert.True(utc.Zip(utc.Skip(1)).All(p => p.Second >= p.First));
    }

    [Fact]
    public void Golden_v3_venue_demo_has_four_utc_syncs_and_venue()
    {
        var d = DemoReader.ReadFile(Path.Combine(DataDir, "calib_20261020_065807Z.vrdemo"));
        Assert.Equal("homelane_6c21", d.V3!.VenueId);
        Assert.Equal(4, d.Events.Count(e => e.Kind == DemoRecord.UtcSync));
    }

    [Fact]
    public void Rejects_unknown_version_and_bad_magic()
    {
        var w = new DemoTestWriter { Version = 4 };
        Assert.Throws<InvalidDataException>(() => DemoReader.Read(new MemoryStream(w.Build())));
        Assert.Throws<InvalidDataException>(() => DemoReader.Read(new MemoryStream("NOPE...."u8.ToArray())));
    }
}
