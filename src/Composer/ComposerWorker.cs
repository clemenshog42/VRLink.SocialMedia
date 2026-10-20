using System.Globalization;
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
public sealed class ComposerWorker(ComposerOptions opt, IFootageSource footage, Func<HighlightRecord, ComposeJob> jobFactory, ILogger<ComposerWorker> log, TimeProvider time,
                                   Func<ReactionJob>? reactionFactory = null)
    : BackgroundService
{
    private const string SessionPinKey = "session-pin";
    private DateTime _startedUtc = DateTime.MinValue;
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
        if (_startedUtc == DateTime.MinValue) _startedUtc = now;
        var server = ServerGate.Read(opt.StateFile);
        if (reactionFactory != null) await SessionPinAsync(server, now, ct);
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

        var pending = _state.Values.Where(s => s.Status == JobStatus.Pinned && s.Id != SessionPinKey).OrderBy(s => s.UpdatedUtc).FirstOrDefault();
        var (allowed, reason) = ServerGate.Evaluate(server, now);
        LastGate = reason;
        if (!allowed) return;
        if (pending == null) { if (reactionFactory != null) await ReactionsAsync(now, ct); return; }

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

    /// <summary>
    /// While a match runs, keep its room footage pinned (rolling pin from the match start to now + 3 min) so the reaction
    /// detector can analyse the whole session afterwards, even when the match is longer than the 15 min retention.
    /// </summary>
    private async Task SessionPinAsync(ServerState? s, DateTime now, CancellationToken ct)
    {
        _state.TryGetValue(SessionPinKey, out var pin);
        bool running = s != null && now - s.UtcNow.ToUniversalTime() <= ServerGate.MaxAge && s.MatchRunning;
        if (!running) return; // keep the last pin until the reactions of the match are done
        var start = pin?.Message != null && DateTime.TryParse(pin.Message, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var st) ? st : now.AddMinutes(-1);
        if (pin?.PinId != null && pin.UpdatedUtc > now.AddMinutes(-1)) return; // refreshed recently
        var id = await footage.PinAsync(opt.Camera, start, now.AddMinutes(3), "session (reactions)", ct);
        if (pin?.PinId != null) await footage.ReleaseAsync(pin.PinId, ct);
        Save(new JobState { Id = SessionPinKey, Status = JobStatus.Pinned, PinId = id, Message = start.ToString("O"), UpdatedUtc = now });
    }

    /// <summary>After a match (gate open): detect + compose reactions once per match whose demos are complete.</summary>
    private async Task ReactionsAsync(DateTime now, CancellationToken ct)
    {
        if (!Directory.Exists(opt.Demos)) return;
        var matches = Directory.EnumerateFiles(opt.Demos, "*_r*" + VRLink.Shared.Demo.DemoFormat.Extension, SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Where(f => !f.FullName.Contains(Path.DirectorySeparatorChar + "calib" + Path.DirectorySeparatorChar))
            .GroupBy(f => f.Name[..f.Name.LastIndexOf("_r", StringComparison.Ordinal)])
            .Select(g => (Match: g.Key, Last: g.Max(f => f.LastWriteTimeUtc)))
            .Where(m => m.Last >= _startedUtc.AddMinutes(-30) && now - m.Last > TimeSpan.FromMinutes(2) && !_state.ContainsKey("rx:" + m.Match))
            .OrderBy(m => m.Last).ToList();
        foreach (var m in matches)
        {
            ComposeResult r;
            try { r = await reactionFactory!().RunAsync(m.Match, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r = new(false, ex.Message, [], []); }
            Save(new JobState { Id = "rx:" + m.Match, Status = r.Ok ? JobStatus.Done : JobStatus.Skipped, Message = r.Message + " | " + string.Join("; ", r.Notes), Clips = r.Clips, UpdatedUtc = now });
            log.LogInformation("[Reactions] {Match}: {Msg}", m.Match, r.Message);
        }
        if (matches.Count > 0 && _state.TryGetValue(SessionPinKey, out var pin) && pin.PinId != null)
        {
            await footage.ReleaseAsync(pin.PinId, ct);
            _state.Remove(SessionPinKey);
            Save(new JobState { Id = "session-pin-released", Status = JobStatus.Done, UpdatedUtc = now });
        }
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
