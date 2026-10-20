using VRLink.Capture;

namespace VRLink.Capture.Tests;

public class ArgsAndParserTests
{
    [Fact]
    public void Test_source_uses_wallclock_segments_and_beep()
    {
        var a = FfmpegArgs.Recorder(new CameraConfig { Id = "camX", Source = "test" }, 10, @"C:\d\incoming", @"C:\d\list.csv");
        var s = string.Join(" | ", a);
        Assert.Contains("testsrc2=size=1920x1080:rate=30,realtime", s);
        Assert.Equal(2, a.Count(x => x == "-use_wallclock_as_timestamps"));
        Assert.Contains("-copyts", a);
        Assert.Contains("segment", a);
        Assert.Equal("10", a[a.IndexOf("-segment_time") + 1]);
        Assert.Equal(@"C:\d\list.csv", a[a.IndexOf("-segment_list") + 1]);
        Assert.Contains("camX", a[a.IndexOf("-vf") + 1]);
        Assert.Contains("aeval", a[a.IndexOf("-af") + 1]);
        Assert.Equal("pipe:1", a[a.IndexOf("-progress") + 1]);
        Assert.DoesNotContain("-nostdin", a); // "q" on stdin stops ffmpeg cleanly
    }

    [Fact]
    public void Dshow_source_uses_device_names_and_input_args()
    {
        var cam = new CameraConfig { Id = "c", Source = "dshow", VideoDevice = "Logitech BRIO", AudioDevice = "Mikrofon (Logitech BRIO)", Width = 3840, Height = 2160, InputArgs = "-vcodec mjpeg" };
        var a = FfmpegArgs.Recorder(cam, 10, "in", "l.csv");
        Assert.Equal("video=Logitech BRIO:audio=Mikrofon (Logitech BRIO)", a[a.IndexOf("-i") + 1]);
        Assert.Equal("3840x2160", a[a.IndexOf("-video_size") + 1]);
        Assert.Equal("mjpeg", a[a.IndexOf("-vcodec") + 1]);
        Assert.Contains("0:a", a);
        Assert.Contains("-use_wallclock_as_timestamps", a);
    }

    [Fact]
    public void Progress_blocks_are_parsed()
    {
        var p = new ProgressParser();
        var now = new DateTime(2026, 10, 20, 7, 0, 0, DateTimeKind.Utc);
        foreach (var l in new[] { "frame=228", "fps=29.97", "drop_frames=3", "dup_frames=1", "speed=0.99x" }) Assert.False(p.Feed(l, now));
        Assert.True(p.Feed("progress=continue", now));
        Assert.Equal(228, p.Frames); Assert.Equal(29.97, p.Fps); Assert.Equal(3, p.DroppedFrames); Assert.Equal(1, p.DupFrames); Assert.Equal(0.99, p.Speed);
        Assert.Equal(now, p.LastUpdateUtc);
    }

    [Fact]
    public void Device_list_is_parsed()
    {
        const string err = """
            [dshow @ 000001] "Logitech BRIO" (video)
            [dshow @ 000001]   Alternative name "@device_pnp_\\?\usb#vid_046d&pid_085e"
            [dshow @ 000001] "OBS Virtual Camera" (none)
            [dshow @ 000001] "Mikrofon (Logitech BRIO)" (audio)
            [dshow @ 000001]   Alternative name "@device_cm_{33D9A762}\wave_{ABC}"
            dummy: Immediate exit requested
            """;
        var d = DeviceListParser.Parse(err);
        Assert.Equal(3, d.Count);
        Assert.Equal(("Logitech BRIO", "video"), (d[0].Name, d[0].Kind));
        Assert.StartsWith("@device_pnp", d[0].AlternativeName);
        Assert.Equal("audio", d[2].Kind);
        Assert.Empty(DeviceListParser.Parse("Could not enumerate video devices (or none found)."));
    }

    [Fact]
    public void Segment_list_lines_and_names()
    {
        Assert.True(SegmentListEntry.TryParse("seg_000001.mp4,1792479583.400000,1792479585.400000", out var e));
        Assert.True(e.HasStart);
        Assert.Equal(new DateTime(2026, 10, 20, 6, 59, 43, 400, DateTimeKind.Utc), SegmentListEntry.UnixToUtc(e.Start));
        Assert.True(SegmentListEntry.TryParse("seg_000000.mp4,0.000000,1792479583.400000", out var first));
        Assert.False(first.HasStart);
        Assert.False(SegmentListEntry.TryParse("garbage", out _));
        Assert.Equal("cam1_20261020T065943.400Z.mp4", SegmentRecord.FileNameFor("cam1", SegmentListEntry.UnixToUtc(e.Start)));
    }
}

public class RetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static List<SegmentRecord> Segs(string cam, int count, long bytes = 10)
    {
        // count segments of 10 s ending at Now
        return Enumerable.Range(0, count).Select(i =>
        {
            var start = Now.AddSeconds(-10 * (count - i));
            return new SegmentRecord { Camera = cam, File = $"{cam}_{i}.mp4", StartUtc = start, EndUtc = start.AddSeconds(10), Bytes = bytes };
        }).ToList();
    }

    [Fact]
    public void Old_unpinned_segments_are_deleted_after_retention()
    {
        var s = Segs("cam1", 120); // 20 min
        var plan = RetentionPlanner.Plan(s, [], Now, TimeSpan.FromMinutes(15), long.MaxValue);
        Assert.Equal(29, plan.Delete.Count); // ended more than 15 min ago (the 30th ends exactly at the limit and stays)
        Assert.All(plan.Delete, d => Assert.True(d.EndUtc < Now.AddMinutes(-15)));
    }

    [Fact]
    public void Pinned_window_is_kept_beyond_retention()
    {
        var s = Segs("cam1", 120);
        var pin = new Pin { Camera = "cam1", FromUtc = Now.AddMinutes(-19), ToUtc = Now.AddMinutes(-18), Reason = "ace" };
        var plan = RetentionPlanner.Plan(s, [pin], Now, TimeSpan.FromMinutes(15), long.MaxValue);
        Assert.Equal(29 - 6, plan.Delete.Count); // 6 segments overlap the pinned minute
        Assert.DoesNotContain(plan.Delete, pin.Covers);
        var otherCam = new Pin { Camera = "cam2", FromUtc = pin.FromUtc, ToUtc = pin.ToUtc };
        Assert.Equal(29, RetentionPlanner.Plan(s, [otherCam], Now, TimeSpan.FromMinutes(15), long.MaxValue).Delete.Count);
        var allCams = new Pin { Camera = null, FromUtc = pin.FromUtc, ToUtc = pin.ToUtc };
        Assert.Equal(23, RetentionPlanner.Plan(s, [allCams], Now, TimeSpan.FromMinutes(15), long.MaxValue).Delete.Count);
    }

    [Fact]
    public void Disk_cap_deletes_oldest_unpinned_first_then_pinned_and_never_the_newest()
    {
        var s = Segs("cam1", 10, bytes: 100); // 1000 bytes, all within retention
        var pin = new Pin { Camera = "cam1", FromUtc = s[0].StartUtc, ToUtc = s[1].EndUtc }; // the two oldest are pinned
        var plan = RetentionPlanner.Plan(s, [pin], Now, TimeSpan.FromMinutes(15), capBytes: 650);
        Assert.Equal(4, plan.Delete.Count);
        Assert.Equal(["cam1_2.mp4", "cam1_3.mp4", "cam1_4.mp4", "cam1_5.mp4"], plan.Delete.Select(d => d.File));
        Assert.False(plan.PinnedDeletedForCap);

        var tight = RetentionPlanner.Plan(s, [pin], Now, TimeSpan.FromMinutes(15), capBytes: 50);
        Assert.True(tight.PinnedDeletedForCap);
        Assert.DoesNotContain(tight.Delete, d => d.File == "cam1_9.mp4"); // newest survives
    }

    [Fact]
    public void Index_and_pins_persist_and_remove_rewrites_the_index()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkcap-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var idx = new SegmentIndex("cam1", Path.Combine(dir, "cam1"));
            var recs = Segs("cam1", 3);
            foreach (var r in recs) { File.WriteAllText(idx.PathOf(r), "x"); idx.Add(r); }
            Assert.Equal(2, idx.Overlapping(Now.AddSeconds(-15), Now).Count);
            Assert.Equal(1, idx.Remove([recs[0]]));
            Assert.False(File.Exists(idx.PathOf(recs[0])));

            var again = new SegmentIndex("cam1", Path.Combine(dir, "cam1"));
            Assert.Equal(2, again.All().Count);

            var pins = new PinStore(dir);
            var p = pins.Add(null, Now, Now.AddSeconds(30), "calibration", Now);
            Assert.Single(new PinStore(dir).All());
            Assert.True(pins.Release(p.Id));
            Assert.Empty(new PinStore(dir).All());
            Assert.Throws<ArgumentException>(() => pins.Add(null, Now, Now, "bad", Now));

            var off = new OffsetStore(dir);
            off.Set("cam1", 212.5);
            Assert.Equal(212.5, new OffsetStore(dir).OffsetMs("cam1"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
