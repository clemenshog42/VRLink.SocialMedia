using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VRLink.Shared.Contracts;
using VRLink.Shared.Media;

namespace VRLink.Composer;

/// <summary>Montage clip in the SAME clip contract (ClipManifest fields) + <c>parts</c> (rank, source clip id, window).</summary>
public sealed class MontageManifest
{
    [JsonPropertyName("highlightId")] public string HighlightId { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "WeeklyTop";
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("matchId")] public string? MatchId { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("durationSec")] public double DurationSec { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; } = 1080;
    [JsonPropertyName("height")] public int Height { get; set; } = 1920;
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("hashtags")] public List<string> Hashtags { get; set; } = [];
    [JsonPropertyName("venue")] public string? Venue { get; set; }
    [JsonPropertyName("video")] public string Video { get; set; } = "";
    [JsonPropertyName("encoder")] public string Encoder { get; set; } = "ffmpeg";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "montage";
    /// <summary>True when ANY source play has a bot hero (the Publisher then applies its bot rule to the montage).</summary>
    [JsonPropertyName("heroIsBot")] public bool HeroIsBot { get; set; }
    [JsonPropertyName("musicFree")] public bool MusicFree { get; set; } = true;
    [JsonPropertyName("audio")] public string Audio { get; set; } = "game+sfx";
    /// <summary>"montage", "game", "gameAudio" and one "clip:&lt;id&gt;" per source play.</summary>
    [JsonPropertyName("sources")] public List<string> Sources { get; set; } = [];
    [JsonPropertyName("players")] public List<ClipPlayer> Players { get; set; } = [];
    /// <summary>The plays: rank 1 = best. Not part of ClipManifest (ignored by readers that do not know it).</summary>
    [JsonPropertyName("parts")] public List<MontagePart> Parts { get; set; } = [];
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
}

public sealed class MontagePart
{
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("clipId")] public string ClipId { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("heroLabel")] public string? HeroLabel { get; set; }
    [JsonPropertyName("heroIsBot")] public bool? HeroIsBot { get; set; }
    /// <summary>Window used from the source clip (seconds) and where it starts in the montage.</summary>
    [JsonPropertyName("srcStartSec")] public double SrcStartSec { get; set; }
    [JsonPropertyName("durationSec")] public double DurationSec { get; set; }
    [JsonPropertyName("outStartSec")] public double OutStartSec { get; set; }
}

public sealed record MontageRequest(string ClipsDir, MontageSelection Selection, string OutDir)
{
    public string? FontBold { get; init; }
    public string? FontRegular { get; init; }
    public string? WorkDir { get; init; }
    /// <summary>Do not reuse the #1 clip's end card; render the simple VRLink card.</summary>
    public bool RenderEndCard { get; init; }
    public MontagePlanOptions Plan { get; init; } = new();
}

public sealed record MontageResult(string Id, string Mp4, string Cover, string Json, MontageEdl Edl, IReadOnlyList<MontageClip> Plays, MontageManifest Manifest);

/// <summary>Finds where the VRLink end card starts in a rendered clip (hard cut in the last seconds: ffmpeg scene detection).</summary>
public static class EndCardDetector
{
    private static readonly Regex PtsTime = new(@"pts_time:([0-9.]+)", RegexOptions.Compiled);

    /// <returns>Start (s) of the end card, or null when no cut was found in the plausible range (then the 1.6 s default is used).</returns>
    public static async Task<double?> DetectAsync(string ffmpeg, MontageClip c, CancellationToken ct)
    {
        double dur = c.DurationSec;
        double from = Math.Max(0, dur - 3.2);
        var (code, _, err) = await FfmpegLocator.RunAsync(ffmpeg,
            ["-hide_banner", "-nostats", "-ss", From(from), "-i", c.VideoPath, "-an", "-vf", "select='gt(scene,0.3)',showinfo", "-f", "null", "-"], ct, TimeSpan.FromMinutes(2));
        if (code != 0) return null;
        double? best = null;
        foreach (Match m in PtsTime.Matches(err))
        {
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rel)) continue;
            double t = from + rel;
            if (t >= dur - 2.6 && t <= dur - 1.0) best = t; // the LAST hard cut in the window
        }
        return best;
    }

    private static string From(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class MontageJob(ILogger log)
{
    private readonly string _ffmpeg = FfmpegLocator.Find("ffmpeg.exe", null) ?? throw new InvalidOperationException("ffmpeg not found");
    private readonly string _ffprobe = FfmpegLocator.Find("ffprobe.exe", null) ?? throw new InvalidOperationException("ffprobe not found");

    private static string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    public async Task<MontageResult> RunAsync(MontageRequest req, CancellationToken ct)
    {
        var all = MontageSelector.Load(req.ClipsDir);
        var plays = MontageSelector.Select(all, req.Selection);
        int want = Math.Clamp(req.Selection.Top, MontageSelector.MinTop, MontageSelector.MaxTop);
        log.LogInformation("{All} clips with video found, {Picked} selected (wanted {Want})", all.Count, plays.Count, want);
        if (plays.Count < MontageSelector.MinTop)
            throw new InvalidOperationException($"only {plays.Count} usable clip(s) (need at least {MontageSelector.MinTop}); check --from/--to, --ids, --allow-bots");

        // real durations + end cards from the files (the json durationSec can be a few ms off)
        var ends = new Dictionary<string, double>();
        foreach (var c in plays)
        {
            var info = await Probe.RunAsync(_ffprobe, c.VideoPath, ct);
            if (info.Duration > 0) c.DurationSec = info.Duration;
            double? det = c.EndCardSec == null ? await EndCardDetector.DetectAsync(_ffmpeg, c, ct) : null;
            ends[c.HighlightId] = MontagePlanner.EndCardStart(c, det);
            log.LogInformation("{Id} ({Type}, score {Score}): {Dur:0.00} s, end card at {Ec:0.00} s{Det}", c.HighlightId, c.Type, c.Score, c.DurationSec, ends[c.HighlightId], det != null ? " (detected)" : "");
        }

        var edl = MontagePlanner.Build(plays, ends, req.Plan, req.RenderEndCard);
        foreach (var w in edl.Warnings) log.LogWarning("{W}", w);
        log.LogInformation("EDL:\n{Edl}", MontagePlanner.Summary(edl));

        var id = MontageId(plays, req.Selection);
        Directory.CreateDirectory(req.OutDir);
        var work = Path.Combine(req.WorkDir ?? Path.Combine(Path.GetTempPath(), "vrlink-montage"), id);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var fonts = Fonts.Resolve(req.FontBold, req.FontRegular);
        var mp4 = Path.Combine(req.OutDir, id + ".mp4");
        var jpg = Path.Combine(req.OutDir, id + ".jpg");
        var json = Path.Combine(req.OutDir, id + ".json");

        var hasAudio = new Dictionary<string, bool>();
        foreach (var p in edl.Pieces.Where(p => p.Clip != null))
            if (!hasAudio.ContainsKey(p.Clip!.VideoPath)) hasAudio[p.Clip.VideoPath] = (await Probe.RunAsync(_ffprobe, p.Clip.VideoPath, ct)).HasAudio;

        var inputs = MontageGraph.Inputs(edl);
        // audio
        var wav = Path.Combine(work, "audio.wav");
        var mixWav = Path.ChangeExtension(wav, ".mix.wav");
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        a.AddRange(inputs);
        a.AddRange(["-filter_complex", MontageGraph.Audio(edl, hasAudio), "-map", "[mix]", "-c:a", "pcm_s16le", "-ar", "48000", mixWav]);
        var (ac, _, ae) = await FfmpegLocator.RunAsync(_ffmpeg, a, ct, TimeSpan.FromMinutes(10));
        if (ac != 0) throw new InvalidOperationException("montage audio failed: " + ae.Trim());
        await AudioBuilder.NormalizeAsync(_ffmpeg, mixWav, wav, edl.Total, ct, m => log.LogInformation("{M}", m));

        // video
        var silent = Path.Combine(work, "video.mp4");
        var v = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        v.AddRange(inputs);
        v.AddRange(["-filter_complex", MontageGraph.Video(edl, fonts, work, plays.Count), "-map", "[vout]", "-an", "-r", edl.Fps.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "medium", "-crf", "19", "-pix_fmt", "yuv420p", "-movflags", "+faststart", silent]);
        var (vc, _, ve) = await FfmpegLocator.RunAsync(_ffmpeg, v, ct, TimeSpan.FromMinutes(20));
        if (vc != 0) throw new InvalidOperationException("montage video failed: " + ve.Trim());

        var (mc, _, me) = await FfmpegLocator.RunAsync(_ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", silent, "-i", wav, "-map", "0:v", "-map", "1:a",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-shortest", "-movflags", "+faststart", mp4], ct, TimeSpan.FromMinutes(5));
        if (mc != 0) throw new InvalidOperationException("montage mux failed: " + me.Trim());

        // cover = the hook frame (the #1 play's best moment with the TOP N text)
        var (cc, _, ce) = await FfmpegLocator.RunAsync(_ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", N(Math.Min(0.6, edl.Pieces[0].Duration - 0.1)), "-i", mp4, "-frames:v", "1", "-q:v", "2", jpg], ct);
        if (cc != 0) throw new InvalidOperationException("cover failed: " + ce.Trim());

        var manifest = BuildManifest(id, plays, edl);
        File.WriteAllText(json, JsonSerializer.Serialize(manifest, ClipManifest.JsonOptions));
        try { Directory.Delete(work, true); } catch { }
        log.LogInformation("montage {Id}: {Dur:0.0} s -> {Mp4}", id, edl.Total, mp4);
        return new MontageResult(id, mp4, jpg, json, edl, plays, manifest);
    }

    public static string MontageId(IReadOnlyList<MontageClip> plays, MontageSelection sel)
    {
        var from = sel.From ?? plays.Min(p => p.Date); var to = sel.To ?? plays.Max(p => p.Date);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("|", plays.Select(p => p.HighlightId))))).ToLowerInvariant()[..6];
        return $"weekly-top{plays.Count}-{from:yyyyMMdd}-{to:yyyyMMdd}-{hash}";
    }

    public static MontageManifest BuildManifest(string id, IReadOnlyList<MontageClip> plays, MontageEdl edl)
    {
        int n = plays.Count;
        var tags = plays.SelectMany(p => p.Hashtags ?? []).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Key).ToList();
        var list = new List<string> { "#VRLink" };
        foreach (var t in new[] { "#top" + n, "#highlights" }.Concat(tags)) if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
        list = list.Take(12).ToList();

        var players = new List<ClipPlayer>();
        foreach (var p in plays.SelectMany(p => p.Players ?? []))
        {
            var ex = players.FirstOrDefault(x => x.Label == p.Label && x.Team == p.Team && x.IsBot == p.IsBot);
            if (ex == null) players.Add(new ClipPlayer { Label = p.Label, Team = p.Team, IsBot = p.IsBot, Hero = p.Hero });
            else ex.Hero |= p.Hero;
        }

        var parts = new List<MontagePart>();
        foreach (var piece in edl.Plays.OrderBy(p => p.Rank))
            parts.Add(new MontagePart
            {
                Rank = piece.Rank, ClipId = piece.Clip!.HighlightId, Type = piece.Clip.Type, Title = piece.Clip.Title, Score = piece.Clip.Score,
                HeroLabel = piece.Clip.HeroLabel, HeroIsBot = piece.Clip.HeroIsBot,
                SrcStartSec = Math.Round(piece.SrcStart, 3), DurationSec = Math.Round(piece.Duration, 3), OutStartSec = Math.Round(piece.OutStart, 3),
            });

        var sources = new List<string> { "montage", "game", "gameAudio" };
        sources.AddRange(plays.Select(p => "clip:" + p.HighlightId));
        return new MontageManifest
        {
            HighlightId = id, Type = "WeeklyTop", Score = Math.Round(plays.Average(p => p.Score), 1), DurationSec = Math.Round(edl.Total, 3),
            Caption = $"Top {n} plays this week at VRLink - which one is your favourite? Free-roam VR, book your squad.",
            Hashtags = list,
            Venue = plays.Select(p => p.Venue).Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            Video = id + ".mp4", Title = $"TOP {n} PLAYS THIS WEEK", Subtitle = "FREE-ROAM VR · VRLink",
            HeroIsBot = plays.Any(p => p.HeroIsBot == true),
            Sources = sources, Players = players, Parts = parts, CreatedUtc = DateTime.UtcNow,
        };
    }
}

/// <summary>ffmpeg inputs and filter graphs of the montage (pure string building, unit-testable).</summary>
public static class MontageGraph
{
    private static string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
    private const string Cyan = "0x35d0ff";

    public static IEnumerable<string> Inputs(MontageEdl edl)
    {
        foreach (var p in edl.Pieces)
        {
            if (p.Clip == null) { foreach (var s in new[] { "-f", "lavfi", "-t", N(p.Duration), "-i", $"color=c=0x0a1118:s=1080x1920:r={edl.Fps}" }) yield return s; }
            else foreach (var s in new[] { "-ss", N(p.SrcStart), "-t", N(p.Duration), "-i", p.Clip.VideoPath.Replace('\\', '/') }) yield return s;
        }
    }

    public static string Audio(MontageEdl edl, IReadOnlyDictionary<string, bool> hasAudio)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < edl.Pieces.Count; i++)
        {
            var p = edl.Pieces[i]; string d = N(p.Duration);
            if (p.Clip != null && hasAudio.GetValueOrDefault(p.Clip.VideoPath))
                sb.Append($"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS,apad=whole_dur={d},atrim=end={d}[a{i}];");
            else
                sb.Append($"anullsrc=r=48000:cl=stereo,atrim=end={d},asetpts=PTS-STARTPTS[a{i}];");
        }
        string prev = "a0";
        for (int i = 1; i < edl.Pieces.Count; i++)
        {
            sb.Append($"[{prev}][a{i}]acrossfade=d={N(edl.Transition)}:c1=tri:c2=tri[ax{i}];");
            prev = "ax" + i;
        }
        sb.Append($"[{prev}]apad,atrim=end={N(edl.Total)},asetpts=PTS-STARTPTS[mix]");
        return sb.ToString();
    }

    private static readonly string[] Transitions = ["fadewhite", "slideleft", "fadewhite", "slideright"];

    public static string Video(MontageEdl edl, Fonts fonts, string workDir, int top)
    {
        Directory.CreateDirectory(workDir);
        int tn = 0;
        string Txt(string s) { var path = Path.Combine(workDir, $"txt{tn++}.txt"); File.WriteAllText(path, s, new UTF8Encoding(false)); return TextPlan.Esc(path); }
        string bold = TextPlan.Esc(fonts.Bold), reg = TextPlan.Esc(fonts.Regular);
        double T = edl.Transition;
        var sb = new StringBuilder();
        for (int i = 0; i < edl.Pieces.Count; i++)
        {
            var p = edl.Pieces[i];
            int frames = (int)Math.Round(p.Duration * edl.Fps);
            if (p.Clip == null) sb.Append($"[{i}:v]fps={edl.Fps},setsar=1,format=yuv420p,setpts=PTS-STARTPTS,trim=end_frame={frames}[v{i}];");
            else sb.Append($"[{i}:v]fps={edl.Fps},scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,format=yuv420p,setpts=PTS-STARTPTS," +
                           $"tpad=stop_mode=clone:stop_duration=0.5,trim=end_frame={frames}[v{i}];");
        }
        string prev = "v0";
        for (int i = 1; i < edl.Pieces.Count; i++)
        {
            sb.Append($"[{prev}][v{i}]xfade=transition={Transitions[(i - 1) % Transitions.Length]}:duration={N(T)}:offset={N(edl.Pieces[i].OutStart)}[x{i}];");
            prev = "x" + i;
        }

        // text on the finished stream (global times): hook, rank cards, VRLink watermark, rendered end card
        var f = new List<string>();
        int sl = TextPlan.SafeLeft, st = TextPlan.SafeTop, sr = TextPlan.SafeRight;
        double hookEnd = edl.Pieces[1].OutStart + 0.05;
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt($"TOP {top}")}':fontsize='230*(1+0.2*max(0,1-t/0.2))':fontcolor=white:borderw=8:bordercolor=black@0.85:x=(w-text_w)/2:y=760:enable='lt(t,{N(hookEnd)})'");
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("PLAYS THIS WEEK")}':fontsize=84:fontcolor={Cyan}:borderw=6:bordercolor=black@0.85:x=(w-text_w)/2:y=1040:enable='lt(t,{N(hookEnd)})'");

        double marksFrom = 0, marksTo = edl.Pieces[^1].OutStart;
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("VRLink")}':fontsize=44:fontcolor=white@0.85:borderw=3:bordercolor=black@0.6:x=w-text_w-{sr}:y={st + 4}:enable='between(t,{N(marksFrom)},{N(marksTo)})'");

        foreach (var p in edl.Plays)
        {
            double a = p.OutStart + T, b = p.OutEnd - T, bigEnd = Math.Min(b, a + 1.3);
            string rank = "#" + p.Rank;
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt(rank)}':fontsize='250*(1+0.4*max(0,1-(t-{N(a)})/0.2))':fontcolor=black:box=1:boxcolor={Cyan}@0.95:boxborderw=34:" +
                  $"x=(w-text_w)/2:y=940:enable='between(t,{N(a)},{N(bigEnd)})'");
            var title = TitleText(p.Clip!);
            int size = (int)Math.Clamp(940.0 / Math.Max(1, title.Length) / 0.62, 34, 64);
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt(title)}':fontsize={size}:fontcolor=white:box=1:boxcolor=black@0.65:boxborderw=18:" +
                  $"x=(w-text_w)/2:y=1190:enable='between(t,{N(a)},{N(bigEnd)})'");
            if (b > bigEnd)
                f.Add($"drawtext=fontfile='{bold}':textfile='{Txt(rank)}':fontsize=58:fontcolor=black:box=1:boxcolor={Cyan}@0.95:boxborderw=10:x={sl}:y={st}:enable='between(t,{N(bigEnd)},{N(b)})'");
        }

        var end = edl.Pieces[^1];
        if (end.Clip == null)
        {
            string en = $"between(t,{N(end.OutStart + T)},{N(end.OutEnd)})";
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("VRLink")}':fontsize=170:fontcolor=white:x=(w-text_w)/2:y=640:enable='{en}'");
            f.Add($"drawtext=fontfile='{reg}':textfile='{Txt("Free-roam VR team shooter")}':fontsize=58:fontcolor={Cyan}:x=(w-text_w)/2:y=860:enable='{en}'");
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("Book your squad")}':fontsize=64:fontcolor=black:box=1:boxcolor=white:boxborderw=24:x=(w-text_w)/2:y=1010:enable='{en}'");
        }
        sb.Append($"[{prev}]{string.Join(",", f)},format=yuv420p[vout]");
        return sb.ToString();
    }

    /// <summary>Upper case title, shortened so it never leaves the frame.</summary>
    public static string TitleText(MontageClip c)
    {
        var t = c.DisplayTitle.Trim().ToUpperInvariant();
        return t.Length <= 26 ? t : t[..24].TrimEnd() + "...";
    }
}
