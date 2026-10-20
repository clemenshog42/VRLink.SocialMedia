using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using VRLink.Shared.Media;

namespace VRLink.Composer;

/// <summary>
/// Audio for a composed clip, driven by the same EDL as the picture:
/// room audio (camera mic) + game.wav per segment (atempo for speed changes, silence on freezes/end card,
/// a short whoosh instead of reversed audio on the rewind), game ducked under loud room peaks (sidechain, ~−6 dB),
/// then two-pass loudnorm to −14 LUFS / −1 dBTP. Output length = video length (frames / fps) exactly.
/// </summary>
public static class AudioBuilder
{
    private static string N(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>atempo chain for a speed factor (each stage 0.5..2 for best quality).</summary>
    public static string Tempo(double speed)
    {
        var parts = new List<string>();
        double s = speed;
        while (s > 2.0 + 1e-9) { parts.Add("atempo=2"); s /= 2; }
        while (s < 0.5 - 1e-9) { parts.Add("atempo=0.5"); s /= 0.5; }
        if (Math.Abs(s - 1) > 1e-6) parts.Add("atempo=" + N(s));
        return string.Join(",", parts);
    }

    /// <summary>filter_complex for one track (input index i) cut by the EDL into [label].</summary>
    public static string Track(Edl edl, int input, string label, bool whooshOnRewind)
    {
        var sb = new StringBuilder();
        var parts = new List<string>();
        int k = 0;
        foreach (var s in edl.Segments)
        {
            string p = $"{label}{k++}";
            string len = N(s.OutDuration);
            switch (s.Kind)
            {
                case EdlKind.Play:
                    var tempo = Tempo(s.Speed);
                    sb.Append($"[{input}:a]aformat=sample_rates=48000:channel_layouts=stereo,atrim=start={N(s.SrcStart)}:end={N(s.SrcEnd)},asetpts=PTS-STARTPTS{(tempo.Length > 0 ? "," + tempo : "")},apad,atrim=end={len}[{p}];");
                    break;
                case EdlKind.Reverse when whooshOnRewind:
                    sb.Append($"anoisesrc=d={len}:c=pink:a=0.35:r=48000,highpass=f=600,afade=t=in:d={N(s.OutDuration * 0.4)},afade=t=out:st={N(s.OutDuration * 0.5)}:d={N(s.OutDuration * 0.5)},aformat=channel_layouts=stereo[{p}];");
                    break;
                default: // freeze, end card, (reverse on the room track): silence
                    sb.Append($"anullsrc=r=48000:cl=stereo,atrim=end={len}[{p}];");
                    break;
            }
            parts.Add(p);
        }
        foreach (var p in parts) sb.Append('[').Append(p).Append(']');
        sb.Append($"concat=n={parts.Count}:v=0:a=1,aformat=sample_rates=48000:channel_layouts=stereo[{label}]");
        return sb.ToString();
    }

    /// <summary>Full graph: room (input 0) + game (input 1) → ducked mix → [mix].</summary>
    /// <param name="gameGainDb">Game level before the mix (reactions: −10 dB so the room is prominent).</param>
    public static string Graph(Edl edl, double totalSec, bool hasRoom, double gameGainDb = 0)
    {
        var sb = new StringBuilder();
        sb.Append(Track(edl, 1, "g0", whooshOnRewind: true)).Append(';');
        sb.Append($"[g0]volume={N(gameGainDb)}dB[g];");
        if (hasRoom)
        {
            sb.Append(Track(edl, 0, "r", whooshOnRewind: false)).Append(';');
            sb.Append("[r]asplit=2[rmix][rsc];");
            // duck the game under loud room peaks (screams, shouts): ratio 4 above −18 dBFS ≈ −6 dB on loud peaks
            sb.Append("[g][rsc]sidechaincompress=threshold=0.125:ratio=4:attack=5:release=300:makeup=1[gd];");
            sb.Append("[rmix][gd]amix=inputs=2:normalize=0:duration=longest");
        }
        else sb.Append("[g]anull");
        sb.Append($",apad,atrim=end={N(totalSec)}[mix]");
        return sb.ToString();
    }

    /// <summary>Renders the mix to a 48 kHz stereo wav normalised to −14 LUFS / −1 dBTP (two-pass loudnorm).</summary>
    public static async Task<string> RenderAsync(string ffmpeg, string? roomSource, string gameWav, Edl edl, double totalSec, string outWav, CancellationToken ct, double gameGainDb = 0)
    {
        bool room = roomSource != null;
        var inputs = new List<string> { "-hide_banner", "-y" };
        if (room) inputs.AddRange(["-i", roomSource!]); else inputs.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        inputs.AddRange(["-i", gameWav]);
        var graph = Graph(edl, totalSec, room, gameGainDb);
        var mixWav = Path.ChangeExtension(outWav, ".mix.wav");
        var a1 = new List<string>(inputs) { "-loglevel", "error", "-filter_complex", graph, "-map", "[mix]", "-c:a", "pcm_s16le", "-ar", "48000", mixWav };
        var (c1, _, e1) = await FfmpegLocator.RunAsync(ffmpeg, a1, ct);
        if (c1 != 0) throw new InvalidOperationException("audio mix failed: " + e1.Trim());

        // pass 1: measure
        var (c2, _, e2) = await FfmpegLocator.RunAsync(ffmpeg, ["-hide_banner", "-i", mixWav, "-af", "loudnorm=I=-14:TP=-1:LRA=11:print_format=json", "-f", "null", "-"], ct);
        if (c2 != 0) throw new InvalidOperationException("loudnorm pass 1 failed: " + e2.Trim());
        var json = e2[e2.LastIndexOf('{')..(e2.LastIndexOf('}') + 1)];
        var m = JsonNode.Parse(json)!;
        string mi = m["input_i"]!.ToString(), mtp = m["input_tp"]!.ToString(), mlra = m["input_lra"]!.ToString(), mth = m["input_thresh"]!.ToString(), off = m["target_offset"]!.ToString();
        // pass 2: apply (silent input → skip normalisation)
        string af = double.TryParse(mi, NumberStyles.Float, CultureInfo.InvariantCulture, out var iv) && iv > -70
            ? $"loudnorm=I=-14:TP=-1:LRA=11:measured_I={mi}:measured_TP={mtp}:measured_LRA={mlra}:measured_thresh={mth}:offset={off}:linear=true,aresample=48000"
            : "anull";
        var (c3, _, e3) = await FfmpegLocator.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", mixWav, "-af", af + $",apad,atrim=end={N(totalSec)}", "-ar", "48000", "-c:a", "pcm_s16le", outWav], ct);
        if (c3 != 0) throw new InvalidOperationException("loudnorm pass 2 failed: " + e3.Trim());
        try { File.Delete(mixWav); } catch { }
        return outWav;
    }

    /// <summary>Integrated loudness (LUFS) and true peak via ebur128 (for tests and the log).</summary>
    public static async Task<(double Lufs, double TruePeak)> MeasureAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var (_, _, err) = await FfmpegLocator.RunAsync(ffmpeg, ["-hide_banner", "-nostats", "-i", file, "-af", "ebur128=peak=true", "-f", "null", "-"], ct);
        double Find(string key)
        {
            int i = err.LastIndexOf(key, StringComparison.Ordinal);
            if (i < 0) return double.NaN;
            var rest = err[(i + key.Length)..].TrimStart(' ', ':');
            var num = new string(rest.TakeWhile(ch => char.IsDigit(ch) || ch is '-' or '.' or '+').ToArray());
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        }
        return (Find("I:"), Find("Peak:"));
    }
}
