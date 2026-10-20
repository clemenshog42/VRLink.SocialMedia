using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

/// <summary>Runtime settings (UI-editable) persisted as one JSON row. Cached; Save() updates cache + DB.</summary>
public class SettingsStore(IDbContextFactory<PublisherDb> dbf, IOptions<PublisherOptions> options)
{
    private const string Key = "runtime";
    private RuntimeSettings? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<RuntimeSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cache != null) return _cache.Clone();
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache == null)
            {
                await using var db = await dbf.CreateDbContextAsync(ct);
                var row = await db.Settings.FindAsync([Key], ct);
                if (row == null)
                {
                    _cache = new RuntimeSettings { DryRun = options.Value.DryRunOnFirstStart };
                    db.Settings.Add(new KeyValueSetting { Key = Key, Value = JsonSerializer.Serialize(_cache) });
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    _cache = JsonSerializer.Deserialize<RuntimeSettings>(row.Value) ?? new RuntimeSettings();
                }
            }
            return _cache.Clone();
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(RuntimeSettings s, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var row = await db.Settings.FindAsync([Key], ct);
            var json = JsonSerializer.Serialize(s);
            if (row == null) db.Settings.Add(new KeyValueSetting { Key = Key, Value = json });
            else row.Value = json;
            await db.SaveChangesAsync(ct);
            _cache = s.Clone();
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Writes to the error log table (shown on the "Error log" page) and to ILogger.</summary>
public class EventLog(IDbContextFactory<PublisherDb> dbf, ILogger<EventLog> logger, TimeProvider time)
{
    public Task InfoAsync(string source, string message, int? clipId = null) => WriteAsync(LogLevelKind.Info, source, message, null, clipId);
    public Task WarnAsync(string source, string message, string? detail = null, int? clipId = null) => WriteAsync(LogLevelKind.Warning, source, message, detail, clipId);
    public Task ErrorAsync(string source, string message, string? detail = null, int? clipId = null) => WriteAsync(LogLevelKind.Error, source, message, detail, clipId);

    public async Task WriteAsync(LogLevelKind level, string source, string message, string? detail, int? clipId)
    {
        var lvl = level switch { LogLevelKind.Error => LogLevel.Error, LogLevelKind.Warning => LogLevel.Warning, _ => LogLevel.Information };
        logger.Log(lvl, "[{Source}] {Message}", source, message);
        try
        {
            await using var db = await dbf.CreateDbContextAsync();
            db.Log.Add(new ErrorLogEntry
            {
                Utc = time.GetUtcNow().UtcDateTime, Level = level, Source = source,
                Message = Trunc(message, 1000), Detail = detail == null ? null : Trunc(detail, 8000), ClipId = clipId,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { logger.LogError(ex, "EventLog write failed"); }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
