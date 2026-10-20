using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VRLink.Capture;

/// <summary>Finds ffmpeg/ffprobe: configured path, process PATH, machine/user PATH (services start with a stale PATH), WinGet links.</summary>
public static class FfmpegLocator
{
    public static string? Find(string exe, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var dirs = new List<string>();
        foreach (var src in new[]
                 {
                     Environment.GetEnvironmentVariable("PATH"),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                 })
            if (src != null) dirs.AddRange(src.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        foreach (var d in dirs)
        {
            try
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(d.Trim('"')), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Runs a short ffmpeg/ffprobe command; returns (exit code, stdout, stderr).</summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + exe);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await so, await se);
    }
}

/// <summary>Builds the ffmpeg recorder command line (pure, tested). Arguments are passed via ArgumentList (no shell quoting).</summary>
public static class FfmpegArgs
{
    /// <summary>Burned-in wall clock (UTC, ms) from the frame timestamp (= wall clock thanks to -use_wallclock_as_timestamps + -copyts).</summary>
    public const string ClockText =
        "drawtext=fontfile='C\\:/Windows/Fonts/consola.ttf':fontsize=56:fontcolor=white:box=1:boxcolor=black@0.6:x=40:y=40:" +
        "text='%{pts\\:gmtime\\:0\\:%Y-%m-%d %H\\\\\\:%M\\\\\\:%S}.%{eif\\:mod(t*1000\\,1000)\\:d\\:3} UTC  {CAM}'";

    /// <summary>TEST audio: a 100 ms 1 kHz beep whenever the (wall clock) timestamp crosses a multiple of 5 s.</summary>
    public const string BeepFilter = "aresample=async=1000,aeval=if(lt(mod(t\\,5)\\,0.1)\\,0.8*sin(2*PI*1000*t)\\,0):c=same";

    public static List<string> Recorder(CameraConfig cam, int segmentSeconds, string incomingDir, string listFile)
    {
        // stdin stays open: the recorder stops ffmpeg with "q" so the last mp4 segment is finalised
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-progress", "pipe:1", "-stats_period", "2" };
        bool test = string.Equals(cam.Source, "test", StringComparison.OrdinalIgnoreCase);
        if (test)
        {
            a.AddRange(["-thread_queue_size", "512", "-use_wallclock_as_timestamps", "1", "-f", "lavfi",
                        "-i", $"testsrc2=size={cam.Width}x{cam.Height}:rate={cam.Fps},realtime"]);
            a.AddRange(["-thread_queue_size", "512", "-use_wallclock_as_timestamps", "1", "-f", "lavfi",
                        "-i", "anullsrc=r=48000:cl=mono,asetnsamples=480,arealtime"]);
            a.AddRange(["-map", "0:v", "-map", "1:a", "-vf", ClockText.Replace("{CAM}", cam.Id), "-af", BeepFilter]);
        }
        else
        {
            a.AddRange(["-f", "dshow", "-rtbufsize", "512M", "-thread_queue_size", "1024", "-use_wallclock_as_timestamps", "1",
                        "-video_size", $"{cam.Width}x{cam.Height}", "-framerate", cam.Fps.ToString(CultureInfo.InvariantCulture)]);
            a.AddRange(Split(cam.InputArgs));
            var input = $"video={cam.VideoDevice}" + (string.IsNullOrWhiteSpace(cam.AudioDevice) ? "" : $":audio={cam.AudioDevice}");
            a.AddRange(["-i", input, "-map", "0:v"]);
            if (!string.IsNullOrWhiteSpace(cam.AudioDevice)) a.AddRange(["-map", "0:a", "-af", "aresample=async=1000"]);
        }
        a.AddRange(Split(cam.VideoCodecArgs));
        // 1 s GOP so segment cuts land on the 10 s grid and trimming re-encodes little
        a.AddRange(["-g", cam.Fps.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-ac", "1"]);
        a.AddRange(["-copyts", "-f", "segment", "-segment_time", segmentSeconds.ToString(CultureInfo.InvariantCulture), "-segment_format", "mp4",
                    "-segment_list", listFile, "-segment_list_type", "csv", "-segment_list_flags", "+live", "-reset_timestamps", "1",
                    Path.Combine(incomingDir, "seg_%06d.mp4")]);
        return a;
    }

    public static IEnumerable<string> Split(string s) => (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Parses "ffmpeg -progress pipe:1" blocks (key=value lines, block ends with progress=continue|end).</summary>
public sealed class ProgressParser
{
    private readonly Dictionary<string, string> _cur = new();
    public double Fps { get; private set; }
    public long Frames { get; private set; }
    public long DroppedFrames { get; private set; }
    public long DupFrames { get; private set; }
    public double Speed { get; private set; }
    public DateTime? LastUpdateUtc { get; private set; }

    /// <returns>true when a block completed.</returns>
    public bool Feed(string line, DateTime nowUtc)
    {
        int i = line.IndexOf('=');
        if (i <= 0) return false;
        var k = line[..i].Trim(); var v = line[(i + 1)..].Trim();
        if (k != "progress") { _cur[k] = v; return false; }
        if (_cur.TryGetValue("fps", out var f) && double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)) Fps = fps;
        if (_cur.TryGetValue("frame", out var fr) && long.TryParse(fr, out var n)) Frames = n;
        if (_cur.TryGetValue("drop_frames", out var d) && long.TryParse(d, out var dn)) DroppedFrames = dn;
        if (_cur.TryGetValue("dup_frames", out var u) && long.TryParse(u, out var un)) DupFrames = un;
        if (_cur.TryGetValue("speed", out var sp) && double.TryParse(sp.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) Speed = s;
        LastUpdateUtc = nowUtc;
        _cur.Clear();
        return true;
    }
}

public sealed record DshowDevice(string Name, string Kind, string? AlternativeName);

/// <summary>Parses "ffmpeg -list_devices true -f dshow -i dummy" stderr.</summary>
public static partial class DeviceListParser
{
    [GeneratedRegex("\"(?<name>[^\"]+)\"\\s*\\((?<kind>video|audio|none)\\)")]
    private static partial Regex DeviceLine();
    [GeneratedRegex("Alternative name\\s*\"(?<alt>[^\"]+)\"")]
    private static partial Regex AltLine();

    public static List<DshowDevice> Parse(string stderr)
    {
        var list = new List<DshowDevice>();
        foreach (var line in stderr.Split('\n'))
        {
            var m = DeviceLine().Match(line);
            if (m.Success) { list.Add(new DshowDevice(m.Groups["name"].Value, m.Groups["kind"].Value, null)); continue; }
            var alt = AltLine().Match(line);
            if (alt.Success && list.Count > 0) list[^1] = list[^1] with { AlternativeName = alt.Groups["alt"].Value };
        }
        return list;
    }

    public static async Task<List<DshowDevice>> ListAsync(string ffmpeg, CancellationToken ct)
    {
        var (_, _, err) = await FfmpegLocator.RunAsync(ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], ct, TimeSpan.FromSeconds(20));
        return Parse(err);
    }
}
