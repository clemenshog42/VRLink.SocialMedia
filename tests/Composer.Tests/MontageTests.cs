using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VRLink.Composer;
using VRLink.Shared.Contracts;
using VRLink.Shared.Media;
using Xunit.Abstractions;

namespace VRLink.Composer.Tests;

public class MontageSelectionTests
{
    private static MontageClip C(string id, string type, double score, string hero = "H", bool bot = false, string date = "2026-11-01", string match = "m1") => new()
    {
        HighlightId = id, Type = type, Score = score, HeroLabel = hero, HeroIsBot = bot, MatchId = match, Date = DateOnly.Parse(date, CultureInfo.InvariantCulture),
        DurationSec = 14, VideoPath = id + ".mp4",
    };

    private static string[] Ids(IEnumerable<MontageClip> c) => c.Select(x => x.HighlightId).ToArray();

    [Fact]
    public void Top_n_by_score_best_first()
    {
        var all = new[] { C("a", "Ace", 50, "h1"), C("b", "Kills3", 90, "h2"), C("c", "Clutch", 70, "h3"), C("d", "Kills4", 80, "h4"), C("e", "LastSecondDefuse", 60, "h5"), C("f", "OneTapStreak", 10, "h6") };
        Assert.Equal(["b", "d", "c", "e", "a"], Ids(MontageSelector.Select(all, new MontageSelection { Top = 5 })));
    }

    [Fact]
    public void At_most_two_clips_of_one_type()
    {
        var all = new[] { C("a1", "Ace", 100, "h1"), C("a2", "Ace", 99, "h2"), C("a3", "Ace", 98, "h3"), C("a4", "Ace", 97, "h4"), C("k1", "Kills3", 50, "h5"), C("k2", "Kills3", 49, "h6"), C("c1", "Clutch", 10, "h7") };
        var r = MontageSelector.Select(all, new MontageSelection { Top = 5 });
        Assert.Equal(2, r.Count(c => c.Type == "Ace"));
        Assert.Equal(["a1", "a2", "k1", "k2", "c1"], Ids(r));
    }

    [Fact]
    public void Bots_are_skipped_unless_allowed()
    {
        var all = new[] { C("a", "Ace", 100, "b1", bot: true), C("b", "Kills3", 90, "h2"), C("c", "Clutch", 80, "h3"), C("d", "Kills4", 70, "h4") };
        Assert.Equal(["b", "c", "d"], Ids(MontageSelector.Select(all, new MontageSelection { Top = 5 })));
        Assert.Equal(["a", "b", "c", "d"], Ids(MontageSelector.Select(all, new MontageSelection { Top = 5, AllowBots = true })));
    }

    [Fact]
    public void Ids_file_restricts_to_approved_clips_and_dates_filter()
    {
        var all = new[] { C("a", "Ace", 100, "h1", date: "2026-10-20"), C("b", "Kills3", 90, "h2"), C("c", "Clutch", 80, "h3"), C("d", "Kills4", 70, "h4"), C("e", "OneTapStreak", 60, "h5") };
        var ids = new HashSet<string>(["b", "d", "e", "zzz"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["b", "d", "e"], Ids(MontageSelector.Select(all, new MontageSelection { Ids = ids })));
        Assert.Equal(["b", "c", "d"], Ids(MontageSelector.Select(all, new MontageSelection { From = new DateOnly(2026, 10, 25), To = new DateOnly(2026, 11, 3), Top = 3 })));
    }

    [Fact]
    public void Different_heroes_are_preferred_but_not_required()
    {
        var all = new[] { C("a", "Ace", 100, "same"), C("b", "Kills3", 90, "same"), C("c", "Clutch", 80, "other"), C("d", "Kills4", 70, "third"), C("e", "OneTapStreak", 60, "same"), C("f", "LastSecondDefuse", 50, "x") };
        Assert.Equal(["a", "c", "d", "f"], Ids(MontageSelector.Select(all, new MontageSelection { Top = 4 }))); // one "same" hero, lower scored other heroes win
        var few = new[] { C("a", "Ace", 100, "p"), C("b", "Kills3", 90, "p"), C("c", "Clutch", 80, "q"), C("d", "Kills4", 70, "p") };
        Assert.Equal(["a", "b", "c", "d"], Ids(MontageSelector.Select(few, new MontageSelection { Top = 4 }))); // hero rule relaxed to fill the list
    }

    [Fact]
    public void Top_is_clamped_to_3_7_and_earlier_montages_are_ignored()
    {
        var all = Enumerable.Range(0, 10).Select(i => C("c" + i, "T" + i, 100 - i, "h" + i)).Append(new MontageClip { HighlightId = "m", Type = "WeeklyTop", Format = "montage", Score = 999 }).ToList();
        Assert.Equal(3, MontageSelector.Select(all, new MontageSelection { Top = 1 }).Count);
        Assert.Equal(7, MontageSelector.Select(all, new MontageSelection { Top = 12 }).Count);
        Assert.DoesNotContain("m", Ids(MontageSelector.Select(all, new MontageSelection { Top = 7 })));
    }

    [Fact]
    public void Load_skips_json_without_video_dedups_ids_and_reads_ids_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrlink-montage-sel", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "2026-10-19")); Directory.CreateDirectory(Path.Combine(root, "2026-10-20"));
            File.WriteAllText(Path.Combine(root, "2026-10-19", "x-r1.json"), "{\"highlightId\":\"x-r1\",\"type\":\"Ace\",\"score\":5}"); // no mp4 -> skipped
            File.WriteAllText(Path.Combine(root, "2026-10-20", "x-r1.json"), "{\"highlightId\":\"x-r1\",\"type\":\"Ace\",\"score\":5}");
            File.WriteAllBytes(Path.Combine(root, "2026-10-20", "x-r1.mp4"), [0]);
            File.WriteAllText(Path.Combine(root, "2026-10-20", "y.json"), "{\"highlightId\":\"y\",\"type\":\"Kills3\",\"score\":7,\"createdUtc\":\"2026-10-21T10:00:00Z\",\"video\":\"other.mp4\"}");
            File.WriteAllBytes(Path.Combine(root, "2026-10-20", "other.mp4"), [0]);
            var all = MontageSelector.Load(root);
            Assert.Equal(["x-r1", "y"], Ids(all));
            Assert.Equal(new DateOnly(2026, 10, 20), all[0].Date);   // folder date
            Assert.Equal(new DateOnly(2026, 10, 21), all[1].Date);   // createdUtc wins
            var f = Path.Combine(root, "ids.txt");
            File.WriteAllText(f, "# approved by Clemens\nx-r1\n\n y.mp4  # ok\n");
            Assert.Equal(["x-r1", "y"], MontageSelector.ReadIds(f).OrderBy(s => s).ToArray());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class MontageEdlTests
{
    private static List<MontageClip> Clips(int n, double dur = 14) =>
        Enumerable.Range(0, n).Select(i => new MontageClip { HighlightId = "c" + i, Type = "Ace", Score = 100 - i, DurationSec = dur, VideoPath = "c" + i + ".mp4", Title = "T" + i }).ToList();

    private static Dictionary<string, double> Ends(IEnumerable<MontageClip> cs, double cardLen = 1.6) => cs.ToDictionary(c => c.HighlightId, c => c.DurationSec - cardLen);

    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Total_length_is_25_to_45_seconds(int n)
    {
        var cs = Clips(n);
        var e = MontagePlanner.Build(cs, Ends(cs));
        Assert.InRange(e.Total, 25, 45);
        Assert.Empty(e.Warnings);
        Assert.All(e.Plays, p => Assert.InRange(p.Duration, 4 - 0.01, 8 + 0.01));
        if (n >= 4) Assert.All(e.Plays, p => Assert.InRange(p.Duration, 4 - 0.01, 6 + 0.01));
    }

    [Fact]
    public void No_gaps_countdown_order_and_one_end_card()
    {
        var cs = Clips(5);
        var e = MontagePlanner.Build(cs, Ends(cs));
        double T = e.Transition;
        Assert.InRange(T, 0.01, 0.25);
        Assert.Equal(0, e.Pieces[0].OutStart);
        for (int i = 1; i < e.Pieces.Count; i++)
        {
            Assert.Equal(e.Pieces[i - 1].OutEnd - T, e.Pieces[i].OutStart, 9); // every piece starts while the previous one fades out: no gap, no dead air
            Assert.True(e.Pieces[i].OutStart < e.Pieces[i - 1].OutEnd);
        }
        Assert.Equal(MontagePieceKind.Hook, e.Pieces[0].Kind);
        Assert.Equal(1.2, e.Pieces[0].Duration, 6);
        Assert.Equal([5, 4, 3, 2, 1], e.Plays.Select(p => p.Rank).ToArray());
        Assert.Single(e.Pieces, p => p.Kind == MontagePieceKind.EndCard);
        Assert.Equal(MontagePieceKind.EndCard, e.Pieces[^1].Kind);
        Assert.Equal(e.TotalFrames / (double)e.Fps, e.Total, 6);
    }

    [Fact]
    public void Source_end_card_is_cut_away_and_reused_once_from_the_best_clip()
    {
        var cs = Clips(5);
        var ends = Ends(cs);
        var e = MontagePlanner.Build(cs, ends);
        foreach (var p in e.Pieces.Where(p => p.Kind != MontagePieceKind.EndCard))
            Assert.True(p.SrcStart + p.Duration <= ends[p.Clip!.HighlightId] - 0.05, $"{p.Kind} {p.Clip.HighlightId} runs into its end card");
        var end = e.Pieces[^1];
        Assert.Same(cs[0], end.Clip);
        Assert.Equal(ends["c0"], end.SrcStart, 1);
        Assert.Equal(1.6, end.Duration, 1);
    }

    [Fact]
    public void Manifest_peak_is_inside_the_window_and_short_clips_do_not_break_the_plan()
    {
        var cs = Clips(4);
        cs[2].PeakSec = 5.0; // clip c2 = #3
        var e = MontagePlanner.Build(cs, Ends(cs));
        var p3 = e.Plays.Single(p => p.Rank == 3);
        Assert.InRange(5.0, p3.SrcStart, p3.SrcStart + p3.Duration);

        var shortClips = Clips(5, dur: 7.0); // only ~5.3 s of gameplay each
        var e2 = MontagePlanner.Build(shortClips, Ends(shortClips));
        Assert.All(e2.Plays, p => Assert.True(p.SrcStart >= 0 && p.Duration <= 5.31));
    }

    [Fact]
    public void Clip_without_end_card_gets_a_rendered_VRLink_card()
    {
        var cs = Clips(3);
        var e = MontagePlanner.Build(cs, cs.ToDictionary(c => c.HighlightId, c => c.DurationSec)); // no end card in the sources
        Assert.Null(e.Pieces[^1].Clip);
        var forced = MontagePlanner.Build(cs, Ends(cs), renderEndCard: true);
        Assert.Null(forced.Pieces[^1].Clip);
        Assert.Single(e.Pieces, p => p.Kind == MontagePieceKind.EndCard);
    }

    [Fact]
    public void End_card_start_prefers_manifest_then_detection_then_default()
    {
        var c = new MontageClip { DurationSec = 10 };
        Assert.Equal(8.4, MontagePlanner.EndCardStart(c, null), 6);
        Assert.Equal(8.0, MontagePlanner.EndCardStart(c, 8.0), 6);
        c.EndCardSec = 7.5;
        Assert.Equal(7.5, MontagePlanner.EndCardStart(c, 8.0), 6);
    }

    [Fact]
    public void Manifest_follows_the_clip_contract_with_parts()
    {
        var cs = Clips(5);
        cs[0].Hashtags = ["#VRLink", "#ace"]; cs[1].Hashtags = ["#VRLink", "#multikill"]; cs[0].Venue = "Arena";
        cs[0].Players = [new ClipPlayer { Label = "TU 1", Team = "TU", Hero = true }];
        cs[1].Players = [new ClipPlayer { Label = "TU 1", Team = "TU" }, new ClipPlayer { Label = "SYN 2", Team = "SYN", IsBot = true }];
        cs[1].HeroIsBot = true;
        var e = MontagePlanner.Build(cs, Ends(cs));
        var m = MontageJob.BuildManifest("weekly-x", cs, e);
        var back = JsonSerializer.Deserialize<ClipManifest>(JsonSerializer.Serialize(m, ClipManifest.JsonOptions), ClipManifest.JsonOptions)!;
        Assert.Equal("montage", back.Format); Assert.Equal("WeeklyTop", back.Type); Assert.Equal("weekly-x", back.HighlightId);
        Assert.Equal(true, back.MusicFree); Assert.Equal(true, back.HeroIsBot);
        Assert.Contains("#VRLink", back.Hashtags!); Assert.Contains("#top5", back.Hashtags!);
        Assert.Contains("VRLink", back.Caption); Assert.Contains("favourite", back.Caption);
        Assert.Equal(2, back.Players!.Count); Assert.True(back.Players.First(p => p.Label == "TU 1").Hero);
        Assert.Contains("clip:c3", back.Sources!);
        Assert.Equal(["c0", "c1", "c2", "c3", "c4"], m.Parts.Select(p => p.ClipId).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], m.Parts.Select(p => p.Rank).ToArray());
        Assert.Equal(e.Total, m.DurationSec, 2);
    }
}

/// <summary>Builds a montage from small synthetic clips (ffmpeg testsrc + sine, end card = dark card) with real ffmpeg and checks it with ffprobe.</summary>
public class MontageEndToEndTests(ITestOutputHelper output)
{
    private static async Task MakeClip(string ffmpeg, string dir, string id, string type, double score, string hero, int freq, bool audio)
    {
        var mp4 = Path.Combine(dir, id + ".mp4");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=s=270x480:r=30:d=9", "-f", "lavfi", "-i", "color=c=0x0a1218:s=270x480:r=30:d=1.6" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", $"sine=f={freq}:r=48000:d=10.6"]);
        args.AddRange(["-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0,format=yuv420p[v]", "-map", "[v]"]);
        if (audio) args.AddRange(["-map", "2:a", "-c:a", "aac", "-b:a", "128k"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", mp4]);
        var (c, _, e) = await FfmpegLocator.RunAsync(ffmpeg, args, CancellationToken.None);
        Assert.True(c == 0, e);
        var json = "{\"highlightId\":\"" + id + "\",\"type\":\"" + type + "\",\"score\":" + score.ToString(CultureInfo.InvariantCulture) + ",\"heroIsBot\":false,\"heroLabel\":\"" + hero +
                   "\",\"matchId\":\"m" + id + "\",\"round\":1,\"durationSec\":10.6,\"width\":1080,\"height\":1920,\"title\":\"" + type.ToUpperInvariant() + " " + id +
                   "\",\"caption\":\"c\",\"hashtags\":[\"#VRLink\",\"#vr\",\"#" + type.ToLowerInvariant() + "\"],\"venue\":\"Test arena\",\"video\":\"" + id +
                   ".mp4\",\"format\":\"game\",\"createdUtc\":\"2026-11-02T10:00:00Z\",\"players\":[{\"label\":\"" + hero + "\",\"team\":\"TU\",\"isBot\":false,\"hero\":true}]}";
        File.WriteAllText(Path.Combine(dir, id + ".json"), json);
    }

    [Fact]
    public async Task Synthetic_clips_give_a_1080x1920_montage_with_matching_audio_and_video_length()
    {
        var root = Path.Combine(Path.GetTempPath(), "vrlink-montage-e2e", Guid.NewGuid().ToString("N"));
        var clips = Path.Combine(root, "clips", "2026-11-02"); var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(clips);
        var ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null)!; var ffprobe = FfmpegLocator.Find("ffprobe.exe", null)!;
        try
        {
            await MakeClip(ffmpeg, clips, "s1", "Ace", 100, "A", 330, true);
            await MakeClip(ffmpeg, clips, "s2", "Kills3", 90, "B", 440, true);
            await MakeClip(ffmpeg, clips, "s3", "Clutch", 80, "C", 550, false);      // no audio track at all
            await MakeClip(ffmpeg, clips, "s4", "LastSecondDefuse", 70, "D", 660, true);
            await MakeClip(ffmpeg, clips, "s5", "OneTapStreak", 10, "E", 770, true);  // not in the top 4

            var probeClip = MontageSelector.Load(Path.Combine(root, "clips")).First(c => c.HighlightId == "s1");
            var detected = await EndCardDetector.DetectAsync(ffmpeg, probeClip, CancellationToken.None);
            output.WriteLine($"detected end card at {detected}");
            Assert.NotNull(detected);
            Assert.InRange(detected!.Value, 8.9, 9.1);

            var req = new MontageRequest(Path.Combine(root, "clips"), new MontageSelection { Top = 4, From = new DateOnly(2026, 11, 1), To = new DateOnly(2026, 11, 7) }, outDir)
            { WorkDir = Path.Combine(root, "work") };
            var r = await new MontageJob(NullLogger.Instance).RunAsync(req, CancellationToken.None);
            output.WriteLine(MontagePlanner.Summary(r.Edl));

            Assert.Equal(["s1", "s2", "s3", "s4"], r.Plays.Select(p => p.HighlightId).ToArray());
            Assert.True(File.Exists(r.Mp4) && File.Exists(r.Cover) && File.Exists(r.Json));
            Assert.Equal(Path.Combine(outDir, r.Id + ".mp4"), r.Mp4);

            var info = await Probe.RunAsync(ffprobe, r.Mp4, CancellationToken.None);
            Assert.Equal((1080, 1920), (info.Width, info.Height));
            Assert.Equal(30, info.Fps, 1);
            Assert.True(info.HasAudio);
            Assert.InRange(info.Duration, 25, 45);
            var (_, ao, _) = await FfmpegLocator.RunAsync(ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,duration", "-of", "csv=p=0", r.Mp4], CancellationToken.None);
            var ap = ao.Trim().Split(',');
            Assert.Equal("aac", ap[0]);
            var aDur = double.Parse(ap[1], CultureInfo.InvariantCulture);
            var (_, vo, _) = await FfmpegLocator.RunAsync(ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,duration", "-of", "csv=p=0", r.Mp4], CancellationToken.None);
            var vp = vo.Trim().Split(',');
            Assert.Equal("h264", vp[0]);
            var vDur = double.Parse(vp[1], CultureInfo.InvariantCulture);
            output.WriteLine($"video {vDur:0.000} s, audio {aDur:0.000} s, planned {r.Edl.Total:0.000} s");
            Assert.InRange(Math.Abs(aDur - vDur), 0, 0.1);
            Assert.InRange(Math.Abs(vDur - r.Edl.Total), 0, 0.1);

            var (lufs, tp) = await AudioBuilder.MeasureAsync(ffmpeg, r.Mp4, CancellationToken.None);
            output.WriteLine($"{lufs:0.0} LUFS, TP {tp:0.0}");
            Assert.InRange(lufs, -15.5, -12.5);

            var m = JsonSerializer.Deserialize<ClipManifest>(File.ReadAllText(r.Json), ClipManifest.JsonOptions)!;
            Assert.Equal("montage", m.Format); Assert.Equal("WeeklyTop", m.Type); Assert.Equal(r.Id, m.HighlightId);
            Assert.Equal(4, m.Players!.Count);
            Assert.True(new FileInfo(r.Cover).Length > 2000);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
