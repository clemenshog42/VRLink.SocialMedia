using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

public record ProbeResult(double DurationSec, int Width, int Height);

public interface IMediaProbe
{
    bool Available { get; }
    Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct = default);
}

/// <summary>Uses ffprobe (configured path or PATH). If not found, Available = false and the approver trusts clip.json.</summary>
public class FfprobeMediaProbe : IMediaProbe
{
    private readonly string? _exe;
    private readonly ILogger<FfprobeMediaProbe> _logger;

    public FfprobeMediaProbe(IOptions<PublisherOptions> options, ILogger<FfprobeMediaProbe> logger)
    {
        _logger = logger;
        _exe = Resolve(options.Value.FfprobePath);
    }

    public bool Available => _exe != null;

    private static string? Resolve(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "ffprobe.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (_exe == null) return null;
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", path })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(stdout);
            var s = doc.RootElement.GetProperty("streams")[0];
            var dur = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString() ?? "0", CultureInfo.InvariantCulture);
            return new ProbeResult(dur, s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", path);
            return null;
        }
    }
}
