using VRLink.Shared.Media;
using Microsoft.Extensions.Options;

namespace VRLink.Capture;

/// <summary>Owns the camera recorders, pins, offsets and the retention loop.</summary>
public sealed class CaptureHost : BackgroundService
{
    private readonly CaptureOptions _opt;
    private readonly ILoggerFactory _logs;
    private readonly ILogger<CaptureHost> _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, CameraRecorder> _recorders = new(StringComparer.OrdinalIgnoreCase);

    public string DataDir { get; }
    public string CacheDir => Path.Combine(DataDir, "footage-cache");
    public string Ffmpeg { get; }
    public string? Ffprobe { get; }
    public PinStore Pins { get; }
    public OffsetStore Offsets { get; }
    public RetentionPlan? LastRetention { get; private set; }
    /// <summary>When false, recorders are created (index, footage access) but ffmpeg is not started (tests, footage-only mode).</summary>
    public bool AutoStartRecorders { get; set; } = true;

    public CaptureHost(IOptions<CaptureOptions> options, ILoggerFactory logs, TimeProvider time)
    {
        _opt = options.Value; _logs = logs; _log = logs.CreateLogger<CaptureHost>(); _time = time;
        DataDir = _opt.ResolveDataDir();
        Directory.CreateDirectory(DataDir);
        Ffmpeg = FfmpegLocator.Find("ffmpeg.exe", _opt.FfmpegPath) ?? throw new InvalidOperationException("ffmpeg.exe not found (Capture:FfmpegPath or PATH)");
        Ffprobe = FfmpegLocator.Find("ffprobe.exe", _opt.FfprobePath);
        Pins = new PinStore(DataDir);
        Offsets = new OffsetStore(DataDir);
        foreach (var cam in _opt.Cameras.Where(c => c.Enabled))
            _recorders[cam.Id] = new CameraRecorder(cam, _opt, Ffmpeg, Ffprobe, logs.CreateLogger("Camera." + cam.Id), time);
    }

    public CaptureOptions Options => _opt;
    public CameraRecorder? Recorder(string cam) => _recorders.GetValueOrDefault(cam);
    public IReadOnlyCollection<CameraRecorder> Recorders => _recorders.Values;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (AutoStartRecorders) foreach (var r in _recorders.Values) r.Start();
        _log.LogInformation("Capture: {N} camera(s), data {Dir}, ffmpeg {Ffmpeg}", _recorders.Count, DataDir, Ffmpeg);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { RunRetention(); } catch (Exception ex) { _log.LogError(ex, "retention failed"); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.RetentionCheckSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await Task.WhenAll(_recorders.Values.Select(r => r.StopAsync()));
        }
    }

    public RetentionPlan RunRetention()
    {
        var all = _recorders.Values.SelectMany(r => r.Index.All()).ToList();
        var plan = RetentionPlanner.Plan(all, Pins.All(), _time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(_opt.RetentionMinutes),
                                         (long)(_opt.DiskCapGb * 1024 * 1024 * 1024));
        foreach (var g in plan.Delete.GroupBy(s => s.Camera))
            _recorders[g.Key].Index.Remove(g);
        if (plan.PinnedDeletedForCap) _log.LogWarning("Disk cap {Cap} GB reached: pinned footage was deleted", _opt.DiskCapGb);
        // footage cache: keep 1 h
        if (Directory.Exists(CacheDir))
            foreach (var f in Directory.GetFiles(CacheDir))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)) { try { File.Delete(f); } catch { } }
        LastRetention = plan;
        return plan;
    }

    public object Health()
    {
        var cams = _recorders.Values.Select(r => r.Status()).ToList();
        return new
        {
            ok = cams.Count > 0 && cams.All(c => c.Healthy),
            utc = _time.GetUtcNow().UtcDateTime,
            cameras = cams,
            pins = Pins.All().Count,
            offsetsMs = Offsets.All(),
            disk = new { usedBytes = cams.Sum(c => c.SegmentBytes), capGb = _opt.DiskCapGb, retentionMinutes = _opt.RetentionMinutes },
        };
    }
}
