using System.Diagnostics;

namespace VRLink.Shared.Media;

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
        ChildProcessJob.Add(p);
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
