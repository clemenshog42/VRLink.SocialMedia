using VRLink.Shared.Media;
using System.Diagnostics;
using System.Globalization;

namespace VRLink.Capture;

public sealed record CameraStatus(
    string Camera, string Source, bool Running, int? Pid, int Restarts, int Run, double Fps, long Frames, long DroppedFrames, long DupFrames,
    double Speed, DateTime? LastSegmentEndUtc, double? LastSegmentAgeSec, int Segments, long SegmentBytes, string? LastError, bool Healthy,
    int Width, int Height);

/// <summary>
/// Runs one ffmpeg recorder for a camera (BelowNormal priority), restarts it with backoff on failure,
/// turns finished segments (from the live segment list) into UTC-named files + index records.
/// </summary>
public sealed class CameraRecorder : IAsyncDisposable
{
    private readonly CameraConfig _cam;
    private readonly CaptureOptions _opt;
    private readonly string _ffmpeg;
    private readonly string? _ffprobe;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private Process? _proc;
    private ProgressParser _progress = new();
    private int _restarts, _run;
    private string? _lastError;
    private readonly Queue<string> _stderrTail = new();

    public SegmentIndex Index { get; }
    public string Id => _cam.Id;
    public string IncomingDir => Path.Combine(Index.Dir, "incoming");
    public string ListFile => Path.Combine(Index.Dir, "segment_list.csv");
    /// <summary>Raised after a segment was indexed (tests, status).</summary>
    public event Action<SegmentRecord>? SegmentAdded;

    public CameraRecorder(CameraConfig cam, CaptureOptions opt, string ffmpeg, string? ffprobe, ILogger log, TimeProvider time)
    {
        _cam = cam; _opt = opt; _ffmpeg = ffmpeg; _ffprobe = ffprobe; _log = log; _time = time;
        Index = new SegmentIndex(cam.Id, Path.Combine(opt.ResolveDataDir(), cam.Id));
        _run = Index.LastRun;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var started = _time.GetUtcNow();
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _lastError = ex.Message; _log.LogError(ex, "[{Cam}] recorder failed", _cam.Id); }
            if (ct.IsCancellationRequested) break;
            // stable for a minute → reset backoff
            if (_time.GetUtcNow() - started > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(2);
            _restarts++;
            _log.LogWarning("[{Cam}] ffmpeg stopped ({Err}); restart #{N} in {S}s", _cam.Id, _lastError, _restarts, backoff.TotalSeconds);
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(IncomingDir);
        foreach (var f in Directory.GetFiles(IncomingDir)) TryDelete(f); // leftovers of a crashed run are incomplete
        TryDelete(ListFile);
        _run++;
        _progress = new ProgressParser();

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Index.Dir,
        };
        foreach (var a in FfmpegArgs.Recorder(_cam, _opt.SegmentSeconds, IncomingDir, ListFile)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start ffmpeg");
        _proc = p;
        ChildProcessJob.Add(p);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) { _log.LogDebug(ex, "priority"); }
        _log.LogInformation("[{Cam}] ffmpeg pid {Pid} run {Run} ({Source})", _cam.Id, p.Id, _run, _cam.Source);

        var outTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line) _progress.Feed(line, _time.GetUtcNow().UtcDateTime);
        });
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
                lock (_stderrTail) { _stderrTail.Enqueue(line); while (_stderrTail.Count > 50) _stderrTail.Dequeue(); }
        });

        long listPos = 0;
        int run = _run;
        try
        {
            while (!p.HasExited)
            {
                listPos = await ConsumeListAsync(listPos, run, ct);
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(p);
            await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
            await ConsumeListAsync(listPos, run, CancellationToken.None); // index the segment finalised by "q"
            _proc = null;
            throw;
        }
        await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        await ConsumeListAsync(listPos, run, CancellationToken.None);
        _proc = null;
        lock (_stderrTail) _lastError = $"exit {p.ExitCode}: " + string.Join(" | ", _stderrTail.TakeLast(3));
    }

    private static async Task StopProcessAsync(Process p)
    {
        try
        {
            if (p.HasExited) return;
            await p.StandardInput.WriteAsync('q');
            await p.StandardInput.FlushAsync();
            if (!p.WaitForExit(8000)) p.Kill(true);
        }
        catch { try { p.Kill(true); } catch { } }
    }

    /// <summary>Reads new complete lines of the live segment list and indexes those segments.</summary>
    private async Task<long> ConsumeListAsync(long pos, int run, CancellationToken ct)
    {
        if (!File.Exists(ListFile)) return pos;
        string text;
        try
        {
            using var fs = new FileStream(ListFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= pos) return pos;
            fs.Position = pos;
            using var sr = new StreamReader(fs);
            text = await sr.ReadToEndAsync(ct);
        }
        catch (IOException) { return pos; }
        int lastNl = text.LastIndexOf('\n');
        if (lastNl < 0) return pos;
        foreach (var line in text[..lastNl].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SegmentListEntry.TryParse(line, out var e)) continue;
            try { await IndexSegmentAsync(e, run, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "[{Cam}] cannot index {File}", _cam.Id, e.File); }
        }
        return pos + System.Text.Encoding.UTF8.GetByteCount(text[..(lastNl + 1)]);
    }

    private async Task IndexSegmentAsync(SegmentListEntry e, int run, CancellationToken ct)
    {
        var src = Path.IsPathRooted(e.File) ? e.File : Path.Combine(IncomingDir, Path.GetFileName(e.File));
        if (!File.Exists(src)) return;
        var end = SegmentListEntry.UnixToUtc(e.End);
        DateTime start; string how = "list";
        if (e.HasStart) start = SegmentListEntry.UnixToUtc(e.Start);
        else
        {
            var dur = await ProbeDurationAsync(src, ct) ?? _opt.SegmentSeconds;
            start = end.AddTicks(-(long)(dur * TimeSpan.TicksPerSecond));
            how = "probe";
        }
        var rec = new SegmentRecord
        {
            Camera = _cam.Id, File = SegmentRecord.FileNameFor(_cam.Id, start), StartUtc = start, EndUtc = end,
            Bytes = new FileInfo(src).Length, StartSource = how, Run = run, HasAudio = await ProbeHasAudioAsync(src, ct),
        };
        var dst = Index.PathOf(rec);
        File.Move(src, dst, overwrite: true);
        Index.Add(rec);
        SegmentAdded?.Invoke(rec);
    }

    private async Task<bool?> ProbeHasAudioAsync(string file, CancellationToken ct)
    {
        if (_ffprobe == null) return null;
        var (code, so, _) = await FfmpegLocator.RunAsync(_ffprobe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", file], ct, TimeSpan.FromSeconds(20));
        return code == 0 ? so.Trim().Length > 0 : null;
    }

    private async Task<double?> ProbeDurationAsync(string file, CancellationToken ct)
    {
        if (_ffprobe == null) return null;
        var (code, so, _) = await FfmpegLocator.RunAsync(_ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "csv=p=0", file], ct, TimeSpan.FromSeconds(20));
        return code == 0 && double.TryParse(so.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public CameraStatus Status()
    {
        var all = Index.All();
        var last = all.Count == 0 ? (DateTime?)null : all.Max(s => s.EndUtc);
        var now = _time.GetUtcNow().UtcDateTime;
        double? age = last == null ? null : (now - last.Value).TotalSeconds;
        bool running = _proc is { HasExited: false };
        bool healthy = running && age != null && age < _opt.SegmentSeconds * 3 + 5;
        int? pid = null;
        try { pid = _proc?.Id; } catch { }
        return new CameraStatus(_cam.Id, _cam.Source, running, pid, _restarts, _run, _progress.Fps, _progress.Frames, _progress.DroppedFrames,
            _progress.DupFrames, _progress.Speed, last, age, all.Count, all.Sum(s => s.Bytes), _lastError, healthy, _cam.Width, _cam.Height);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
