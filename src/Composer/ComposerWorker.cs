using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VRLink.Composer;

public enum JobStatus { Pinned, Done, Skipped, Failed }

public sealed record JobState
{
    public string Id { get; init; } = "";
    public JobStatus Status { get; init; }
    public string? PinId { get; init; }
    public int Attempts { get; init; }
    public string? Message { get; init; }
    public List<string> Clips { get; init; } = new();
    public DateTime UpdatedUtc { get; init; }
}

/// <summary>Reads highlights_*.jsonl incrementally (only complete lines).</summary>
public static class HighlightFeed
{
    public static List<HighlightRecord> ReadAll(string dir)
    {
        var list = new List<HighlightRecord>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "highlights_*.jsonl").Order())
        {
            string text;
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                text = new StreamReader(fs).ReadToEnd();
            }
            catch (IOException) { continue; }
            int last = text.LastIndexOf('\n');
            foreach (var line in (last < 0 ? "" : text[..last]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try { if (JsonSerializer.Deserialize<HighlightRecord>(line, HighlightRecord.Json) is { Id.Length: > 0 } h) list.Add(h); }
                catch (JsonException) { }
            }
        }
        return list;
    }
}

/// <summary>
/// Background worker (hosted in the Capture service): new highlight → pin its footage right away → compose when the
/// server gate allows (PostMatch/Lobby, or Boot with 0 clients; unknown/stale state → wait) → release the pin.
/// One job at a time; state in &lt;WorkDir&gt;/composer_state.json.
/// </summary>
public sealed class ComposerWorker(ComposerOptions opt, IFootageSource footage, Func<HighlightRecord, ComposeJob> jobFactory, ILogger<ComposerWorker> log, TimeProvider time)
    : BackgroundService
{
    private readonly Dictionary<string, JobState> _state = new();
    private string StatePath => Path.Combine(opt.Work, "composer_state.json");
    public string LastGate { get; private set; } = "";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(opt.Work);
        if (File.Exists(StatePath))
            foreach (var s in JsonSerializer.Deserialize<List<JobState>>(File.ReadAllText(StatePath)) ?? new()) _state[s.Id] = s;
        // only new highlights: everything older than the service start that was never seen is ignored after first run
        bool firstRun = !File.Exists(StatePath);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(firstRun, ct); firstRun = false; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "[Composer] tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(opt.PollSeconds), ct); } catch (OperationCanceledException) { break; }
        }
    }

    public async Task TickAsync(bool ignoreExisting, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var h in HighlightFeed.ReadAll(opt.HighlightsDir))
        {
            if (_state.ContainsKey(h.Id)) continue;
            if (ignoreExisting || now - h.CreatedUtc > TimeSpan.FromMinutes(30))
            {
                Save(new JobState { Id = h.Id, Status = JobStatus.Skipped, Message = "existed before the composer started", UpdatedUtc = now });
                continue;
            }
            // pin generously right away (a round is < 3 min; precise window needs the demo), release after compose
            var pin = await footage.PinAsync(opt.Camera, h.CreatedUtc.AddMinutes(-4), h.CreatedUtc.AddSeconds(30), "highlight " + h.Id, ct);
            Save(new JobState { Id = h.Id, Status = JobStatus.Pinned, PinId = pin, UpdatedUtc = now });
            log.LogInformation("[Composer] {Id} ({Type}) pinned", h.Id, h.Type);
        }

        var pending = _state.Values.Where(s => s.Status == JobStatus.Pinned).OrderBy(s => s.UpdatedUtc).FirstOrDefault();
        if (pending == null) return;
        var (allowed, reason) = ServerGate.Evaluate(ServerGate.Read(opt.StateFile), now);
        LastGate = reason;
        if (!allowed) return;

        var rec = HighlightFeed.ReadAll(opt.HighlightsDir).FirstOrDefault(x => x.Id == pending.Id);
        if (rec == null) { Save(pending with { Status = JobStatus.Failed, Message = "highlight line vanished", UpdatedUtc = now }); return; }
        ComposeResult r;
        try { r = await jobFactory(rec).RunAsync(rec, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            int attempts = pending.Attempts + 1;
            log.LogError(ex, "[Composer] {Id} failed (attempt {N})", rec.Id, attempts);
            Save(pending with { Attempts = attempts, Status = attempts >= opt.MaxAttempts ? JobStatus.Failed : JobStatus.Pinned, Message = ex.Message, UpdatedUtc = now });
            if (attempts >= opt.MaxAttempts && pending.PinId != null) await footage.ReleaseAsync(pending.PinId, ct);
            return;
        }
        if (pending.PinId != null) await footage.ReleaseAsync(pending.PinId, ct);
        Save(pending with { Status = r.Ok ? JobStatus.Done : JobStatus.Skipped, Message = r.Message + (r.Notes.Count > 0 ? " | " + string.Join("; ", r.Notes) : ""), Clips = r.Clips, UpdatedUtc = now });
        log.LogInformation("[Composer] {Id}: {Msg}", rec.Id, r.Message);
    }

    public IReadOnlyCollection<JobState> States => _state.Values;

    private void Save(JobState s)
    {
        _state[s.Id] = s;
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state.Values.OrderBy(x => x.UpdatedUtc).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StatePath, true);
    }
}
