using Microsoft.Extensions.Options;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Workers;

/// <summary>Base loop: runs Step every interval, logs exceptions, never dies.</summary>
public abstract class LoopWorker(ILogger logger, EventLog log) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }
    protected virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(3);
    protected abstract Task StepAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await StepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Worker} step failed", GetType().Name);
                await log.ErrorAsync(GetType().Name, ex.Message, ex.ToString());
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>FileSystemWatcher on ClipsRoot (+ subfolders) plus a periodic full rescan; import is idempotent.</summary>
public sealed class ClipWatcher(ClipImporter importer, IOptions<PublisherOptions> options, ILogger<ClipWatcher> logger, EventLog log) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = options.Value.ClipsRoot;
        FileSystemWatcher? fsw = null;
        try
        {
            Directory.CreateDirectory(root);
            fsw = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            fsw.Created += (_, _) => Poke();
            fsw.Changed += (_, _) => Poke();
            fsw.Renamed += (_, _) => Poke();
            fsw.Error += (_, e) => { logger.LogWarning(e.GetException(), "FileSystemWatcher overflow, rescan"); Poke(); };
            fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            await log.WarnAsync("ClipWatcher", $"Cannot watch {root} (periodic rescan only)", ex.Message);
        }

        var rescan = TimeSpan.FromSeconds(Math.Max(5, options.Value.RescanSeconds));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var r = await importer.ScanAsync(root, stoppingToken);
                    if (r.Imported > 0) logger.LogInformation("ClipWatcher imported {N}", r.Imported);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { await log.ErrorAsync("ClipWatcher", ex.Message, ex.ToString()); }

                await _signal.WaitAsync(rescan, stoppingToken);           // event or timeout
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); // debounce: let the renderer finish the json
                while (_signal.CurrentCount > 0) _signal.Wait(0);
            }
        }
        catch (OperationCanceledException) { }
        finally { fsw?.Dispose(); }
    }

    private void Poke() { if (_signal.CurrentCount < 10) _signal.Release(); }
}

/// <summary>Checks new clips (rules) → AwaitingApproval or Rejected. It NEVER approves; only the owner does, in the web UI.</summary>
public sealed class ClipChecker(Pipeline p, IOptions<PublisherOptions> o, ILogger<ClipChecker> l, EventLog log) : LoopWorker(l, log)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.WorkerTickSeconds);
    protected override Task StepAsync(CancellationToken ct) => p.CheckNewAsync(ct);
}

public sealed class Scheduler(Pipeline p, IOptions<PublisherOptions> o, ILogger<Scheduler> l, EventLog log) : LoopWorker(l, log)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.WorkerTickSeconds * 2);
    protected override Task StepAsync(CancellationToken ct) => p.ScheduleDueAsync(ct);
}

public sealed class PublishWorker(Pipeline p, IOptions<PublisherOptions> o, ILogger<PublishWorker> l, EventLog log) : LoopWorker(l, log)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.WorkerTickSeconds);
    protected override Task StepAsync(CancellationToken ct) => p.PublishDueAsync(ct);
}

/// <summary>Every 3 h for 14 days after the post, then daily (per-post NextStatsPollUtc); checks every 5 min which are due.</summary>
public sealed class StatsPoller(Pipeline p, ILogger<StatsPoller> l, EventLog log) : LoopWorker(l, log)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    protected override TimeSpan InitialDelay => TimeSpan.FromSeconds(20);
    protected override Task StepAsync(CancellationToken ct) => p.PollStatsAsync(ct);
}

public sealed class TokenRefresher(Pipeline p, ILogger<TokenRefresher> l, EventLog log) : LoopWorker(l, log)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(30);
    protected override TimeSpan InitialDelay => TimeSpan.FromSeconds(10);
    protected override Task StepAsync(CancellationToken ct) => p.RefreshTokensAsync(ct);
}
