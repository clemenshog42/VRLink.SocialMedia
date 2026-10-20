using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VRLink.Shared.Calibration;
using VRLink.Shared.Demo;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

public sealed record CaptureCamera(string Camera, string Source, bool Running, bool Healthy, double Fps, long DroppedFrames,
                                   double? LastSegmentAgeSec, int Width, int Height, string? LastError);

public sealed record ClapReport(bool Ok, string Message, double OffsetMs, int Matched, double MaxResidualMs,
                                List<DateTime> DemoClapsUtc, List<DateTime> AudioOnsetsUtc, bool Saved, bool DemoHasUtcAnchor);

/// <summary>Talks to the local Capture service (src/Capture, default http://localhost:5090).</summary>
public sealed class CaptureClient(IHttpClientFactory http)
{
    private HttpClient C => http.CreateClient("capture");

    public async Task<List<CaptureCamera>> CamerasAsync(CancellationToken ct = default) =>
        await C.GetFromJsonAsync<List<CaptureCamera>>("api/cameras", ct) ?? new();

    public async Task<Dictionary<string, double>> OffsetsAsync(CancellationToken ct = default) =>
        await C.GetFromJsonAsync<Dictionary<string, double>>("api/offsets", ct) ?? new();

    public async Task SetOffsetAsync(string cam, double ms, CancellationToken ct = default) =>
        (await C.PutAsync($"api/offsets/{Uri.EscapeDataString(cam)}?ms={ms.ToString(CultureInfo.InvariantCulture)}", null, ct)).EnsureSuccessStatusCode();

    public async Task<HttpResponseMessage> FrameAsync(string cam, DateTime utc, int w, CancellationToken ct = default) =>
        await C.GetAsync($"api/frame?cam={Uri.EscapeDataString(cam)}&utc={Uri.EscapeDataString(utc.ToString("O"))}&w={w}", ct);

    public async Task PinAsync(string? cam, DateTime fromUtc, DateTime toUtc, string reason, CancellationToken ct = default) =>
        (await C.PostAsJsonAsync("api/pins", new { camera = cam, fromUtc, toUtc, reason }, ct)).EnsureSuccessStatusCode();

    public async Task<ClapReport> ClapSyncAsync(string cam, string demoPath, bool save, CancellationToken ct = default)
    {
        var resp = await C.PostAsync($"api/clapsync?cam={Uri.EscapeDataString(cam)}&demo={Uri.EscapeDataString(demoPath)}&save={(save ? "true" : "false")}", null, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"clap test: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");
        return (await resp.Content.ReadFromJsonAsync<ClapReport>(ct))!;
    }
}

/// <summary>Calibration json per venue and camera: &lt;DataDir&gt;/arena-camera/&lt;venueId&gt;/&lt;camera&gt;.json.</summary>
public sealed class CalibrationStore(IOptions<PublisherOptions> options)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root => Path.Combine(options.Value.ResolveDataDir(), "arena-camera");

    private static string Safe(string s) => string.Concat((string.IsNullOrWhiteSpace(s) ? "default" : s).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    public string PathFor(string venue, string cam) => Path.Combine(Root, Safe(venue), Safe(cam) + ".json");

    public CameraCalibration? Load(string venue, string cam)
    {
        var p = PathFor(venue, cam);
        return File.Exists(p) ? JsonSerializer.Deserialize<CameraCalibration>(File.ReadAllText(p)) : null;
    }

    public string Save(CameraCalibration c)
    {
        var p = PathFor(c.VenueId, c.CameraId);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(c, Json));
        File.Move(p + ".tmp", p, overwrite: true);
        return p;
    }
}

/// <summary>Lists demos (newest first; calibration recordings first) under DemosRoot.</summary>
public sealed class DemoCatalog(IOptions<PublisherOptions> options)
{
    public sealed record Entry(string Path, string Name, DateTime Modified, long Bytes, bool IsCalibFolder);

    public List<Entry> List()
    {
        var root = options.Value.DemosRoot;
        if (!Directory.Exists(root)) return new();
        return Directory.EnumerateFiles(root, "*" + DemoFormat.Extension, SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new Entry(f.FullName, System.IO.Path.GetRelativePath(root, f.FullName), f.LastWriteTimeUtc, f.Length,
                                   f.FullName.Contains(System.IO.Path.DirectorySeparatorChar + "calib" + System.IO.Path.DirectorySeparatorChar)))
            .OrderByDescending(e => e.IsCalibFolder).ThenByDescending(e => e.Modified).ToList();
    }

    /// <summary>Only files below DemosRoot can be opened from the UI.</summary>
    public DemoData Open(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetFullPath(options.Value.DemosRoot);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("demo outside DemosRoot");
        return DemoReader.ReadFile(full);
    }
}
