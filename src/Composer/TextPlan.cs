using System.Globalization;
using System.Text;

namespace VRLink.Composer;

/// <summary>Hook lines per highlight type (first 1.5 s). Placeholders: {kills} {vs} {secs}.</summary>
public static class HookTemplates
{
    public static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ace"] = "{kills} kills. Real legs.",
        ["Kills4"] = "4 kills. Real legs.",
        ["Kills3"] = "3 kills. In real life.",
        ["Clutch"] = "1 vs {vs}. In real life.",
        ["OneTapStreak"] = "One tap. Again. And again.",
        ["LastSecondDefuse"] = "Defused with {secs}s left.",
        ["GrenadeMultiKill"] = "One grenade. {kills} down.",
        ["NinjaDefuse"] = "Nobody saw him coming.",
    };

    public static string For(HighlightRecord h)
    {
        var t = Table.TryGetValue(h.Type, out var s) ? s : "This is real VR.";
        return t.Replace("{kills}", Math.Max(h.Kills, h.Moments.Count(m => m.Kind == "Kill")).ToString(CultureInfo.InvariantCulture))
                .Replace("{vs}", Math.Max(1, h.ClutchVs).ToString(CultureInfo.InvariantCulture))
                .Replace("{secs}", h.DefuseSecondsLeft >= 0 ? h.DefuseSecondsLeft.ToString("0.0", CultureInfo.InvariantCulture) : "0");
    }
}

public sealed record Fonts(string Bold, string Regular)
{
    /// <summary>Configured font files, else Windows fonts (Segoe UI Bold / Bahnschrift). Barlow (OFL) can be dropped in via config.</summary>
    public static Fonts Resolve(string? bold, string? regular)
    {
        static string Pick(string? cfg, params string[] fallbacks)
        {
            if (!string.IsNullOrWhiteSpace(cfg) && File.Exists(cfg)) return cfg;
            foreach (var f in fallbacks) { var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), f); if (File.Exists(p)) return p; }
            throw new FileNotFoundException("no usable font file (Composer:FontBold / FontRegular)");
        }
        return new Fonts(Pick(bold, "segoeuib.ttf", "arialbd.ttf"), Pick(regular, "bahnschrift.ttf", "segoeui.ttf", "arial.ttf"));
    }
}

/// <summary>
/// ffmpeg drawtext chain for the hook, kinetic kill counter, "REAL VR" badge, VRLink watermark, end card and split labels.
/// Texts go through textfile= (no escaping problems); everything stays inside the TikTok/Reels safe area
/// (top 140 px, bottom 480 px, right 140 px, left 60 px of a 1080×1920 frame).
/// </summary>
public static class TextPlan
{
    public const int SafeTop = 140, SafeBottom = 480, SafeRight = 140, SafeLeft = 60;

    public static string Esc(string path) => path.Replace("\\", "/").Replace(":", "\\:").Replace("'", "\\'");
    private static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Writes the text files and returns the filter chain (input label [in], output [out]).</summary>
    public static string Build(HighlightRecord h, Edl edl, bool split, Fonts fonts, string workDir, int w = 1080, int hgt = 1920)
    {
        Directory.CreateDirectory(workDir);
        int n = 0;
        string Txt(string s) { var p = Path.Combine(workDir, $"txt{n++}.txt"); File.WriteAllText(p, s, new UTF8Encoding(false)); return Esc(p); }
        string bold = Esc(fonts.Bold), reg = Esc(fonts.Regular);
        var f = new List<string>();
        double endA = edl.EndCardStart, endB = edl.Duration;
        string notEnd = $"lt(t,{N(endA)})";

        // hook (first 1.5 s), big, upper area; fades out
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt(HookTemplates.For(h))}':fontsize=82:fontcolor=white:borderw=6:bordercolor=black@0.85:" +
              $"x=(w-text_w)/2:y={SafeTop + 120}:alpha='if(lt(t,1.2),1,max(0,(1.5-t)/0.3))':enable='lt(t,1.5)'");
        // REAL VR badge (top-left) + VRLink watermark (top-right), not on the end card
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("REAL VR")}':fontsize=40:fontcolor=black:box=1:boxcolor=0x35d0ff@0.95:boxborderw=14:" +
              $"x={SafeLeft}:y={SafeTop}:enable='{notEnd}'");
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("VRLink")}':fontsize=44:fontcolor=white@0.85:borderw=3:bordercolor=black@0.6:" +
              $"x=w-text_w-{SafeRight}:y={SafeTop + 4}:enable='{notEnd}'");
        // kinetic kill counter: each value pops (size +60 % decaying over 0.25 s)
        var kills = edl.Kills;
        int counterY = split ? 820 : hgt - SafeBottom - 160;
        for (int i = 0; i < kills.Count; i++)
        {
            double a = kills[i].OutTime, b = i + 1 < kills.Count ? kills[i + 1].OutTime : endA;
            if (b <= a) continue;
            int c = kills[i].CountAfter;
            string label = c == 1 ? "1 KILL" : $"{c} KILLS";
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt(label)}':fontcolor=white:borderw=6:bordercolor=0xff4f7b:" +
                  $"fontsize='96*(1+0.6*max(0,1-(t-{N(a)})/0.25))':x={SafeLeft}:y={counterY}-text_h/2:enable='between(t,{N(a)},{N(b)})'");
        }
        if (split)
        {
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("REAL")}':fontsize=34:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=10:x={SafeLeft}:y=900:enable='{notEnd}'");
            f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("IN GAME")}':fontsize=34:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=10:x={SafeLeft}:y=984:enable='{notEnd}'");
        }
        // end card: logo text, tagline, CTA
        string endEn = $"between(t,{N(endA)},{N(endB)})";
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("VRLink")}':fontsize=170:fontcolor=white:x=(w-text_w)/2:y=640:enable='{endEn}'");
        f.Add($"drawtext=fontfile='{reg}':textfile='{Txt("Free-roam VR team shooter")}':fontsize=58:fontcolor=0x35d0ff:x=(w-text_w)/2:y=860:enable='{endEn}'");
        f.Add($"drawtext=fontfile='{bold}':textfile='{Txt("Book your squad")}':fontsize=64:fontcolor=black:box=1:boxcolor=white:boxborderw=24:x=(w-text_w)/2:y=1010:enable='{endEn}'");
        return "[0:v]" + string.Join(",", f) + ",format=yuv420p[v]";
    }
}
