using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VRLink.Composer;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

/// <summary>Pure rules of the weekly montage ("TOP 5 PLAYS THIS WEEK"): when it is due, which clips may go in.</summary>
public static class WeeklyMontageRules
{
    public const int MinClips = 3;
    public const string MontageFormat = "montage", MontageType = "weeklytop";

    /// <summary>ISO week key of a local date, e.g. "2026-W45" (Monday = first day, as in ISO 8601).</summary>
    public static string WeekKey(DateTime local) =>
        string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(local)}-W{ISOWeek.GetWeekOfYear(local):00}");

    public static TimeOnly ParseTime(string? s) =>
        TimeOnly.TryParseExact((s ?? "").Trim(), "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(10, 0);

    /// <summary>The configured day/time inside the ISO week that contains <paramref name="nowLocal"/>.</summary>
    public static DateTime ScheduledLocal(DateTime nowLocal, DayOfWeek day, TimeOnly time)
    {
        var monday = nowLocal.Date.AddDays(-(((int)nowLocal.DayOfWeek + 6) % 7));
        return monday.AddDays(((int)day + 6) % 7).Add(time.ToTimeSpan());
    }

    /// <summary>Due = the slot of the current ISO week has been reached and this week is not done yet (a late start in the same week still runs; a missed week is not caught up).</summary>
    public static bool IsDue(DateTime nowLocal, DayOfWeek day, TimeOnly time, string? doneWeek) =>
        !string.Equals(doneWeek, WeekKey(nowLocal), StringComparison.Ordinal) && nowLocal >= ScheduledLocal(nowLocal, day, time);

    /// <summary>
    /// Clips the owner approved within the last 7 days (the same notion as <see cref="Pipeline.IsApprovedForUpload"/>; already posted clips count),
    /// never montages, never bot heroes, only with a video file. Best score first.
    /// </summary>
    public static List<Clip> Eligible(IEnumerable<Clip> clips, DateTime nowUtc, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var since = nowUtc.AddDays(-7);
        return clips
            .Where(c => c.ApprovedAtUtc is { } at && at >= since && at <= nowUtc.AddMinutes(5))
            .Where(c => Pipeline.IsApprovedForUpload(c, out _))
            .Where(c => !c.HeroIsBot)
            .Where(c => !string.Equals(c.Format, MontageFormat, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Type, MontageType, StringComparison.OrdinalIgnoreCase))
            .Where(c => fileExists(c.VideoPath))
            .OrderByDescending(c => c.Score).ThenBy(c => c.HighlightId, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed record MontageRunResult(string Id, string JsonPath, int Plays);

/// <summary>Builds the montage video (the composer in-process; tests replace it so no ffmpeg is needed).</summary>
public interface IMontageRunner
{
    Task<MontageRunResult> RunAsync(string clipsDir, IReadOnlyCollection<string> ids, int top, string outDir, CancellationToken ct);
}

/// <summary><see cref="MontageJob"/> of src/Composer, called in-process (no CLI process). Only the given ids can be used (the clips the owner approved).</summary>
public sealed class ComposerMontageRunner(ILoggerFactory loggers) : IMontageRunner
{
    public async Task<MontageRunResult> RunAsync(string clipsDir, IReadOnlyCollection<string> ids, int top, string outDir, CancellationToken ct)
    {
        var sel = new MontageSelection { Top = top, Ids = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase) };
        var req = new MontageRequest(clipsDir, sel, outDir);
        var r = await new MontageJob(loggers.CreateLogger("MontageJob")).RunAsync(req, ct);
        return new MontageRunResult(r.Id, r.Json, r.Plays.Count);
    }
}

public enum WeeklyMontageOutcome { Disabled, NotDue, Skipped, Created, Failed, Waiting }

/// <summary>Once per ISO week: ids of the clips approved in the last 7 days -> montage -> imported as a new clip that waits for the APPROVE click of the owner.</summary>
public sealed class WeeklyMontageService(IDbContextFactory<PublisherDb> dbf, SettingsStore settings, ClipImporter importer, IMontageRunner runner,
    Microsoft.Extensions.Options.IOptions<PublisherOptions> options, EventLog log, TimeProvider time)
{
    public const string DoneKey = "weeklymontage.doneweek", SkippedKey = "weeklymontage.skippedweek";
    public const int MaxAttemptsPerWeek = 3;
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _failWeek; private int _fails; private DateTime _lastFailUtc;

    public async Task<WeeklyMontageOutcome> RunIfDueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = await settings.GetAsync(ct);
            if (!s.WeeklyMontageEnabled) return WeeklyMontageOutcome.Disabled;

            var nowUtc = time.GetUtcNow().UtcDateTime;
            var tz = SlotPlanner.ResolveTimeZone(s.TimeZoneId);
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
            var week = WeeklyMontageRules.WeekKey(nowLocal);
            var done = await GetAsync(DoneKey, ct);
            if (!WeeklyMontageRules.IsDue(nowLocal, s.WeeklyMontageDay, WeeklyMontageRules.ParseTime(s.WeeklyMontageTime), done)) return WeeklyMontageOutcome.NotDue;
            if (_failWeek == week && (_fails >= MaxAttemptsPerWeek || nowUtc - _lastFailUtc < RetryAfter)) return WeeklyMontageOutcome.Waiting;

            List<Clip> eligible;
            await using (var db = await dbf.CreateDbContextAsync(ct))
                eligible = WeeklyMontageRules.Eligible(await db.Clips.AsNoTracking().Where(c => c.ApprovedAtUtc != null).ToListAsync(ct), nowUtc);

            if (eligible.Count < WeeklyMontageRules.MinClips)
            {
                if (await GetAsync(SkippedKey, ct) != week)
                    await log.InfoAsync("WeeklyMontage", $"weekly montage skipped: only {eligible.Count} approved clips");
                await SetAsync(SkippedKey, week, ct);
                await SetAsync(DoneKey, week, ct); // stop for this week
                return WeeklyMontageOutcome.Skipped;
            }

            int top = Math.Clamp(s.WeeklyMontageTop, MontageSelector.MinTop, MontageSelector.MaxTop);
            var ids = eligible.Select(c => c.HighlightId).ToList();
            var root = options.Value.ClipsRoot;
            var outDir = Path.Combine(root, nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            try
            {
                Directory.CreateDirectory(outDir);
                // audit trail: exactly which approved clips were offered to the montage (the production path of docs/MONTAGE.md)
                var idsDir = Path.Combine(options.Value.ResolveDataDir(), "Montage");
                Directory.CreateDirectory(idsDir);
                await File.WriteAllTextAsync(Path.Combine(idsDir, $"weekly-{week}.ids.txt"),
                    $"# approved in the last 7 days, {nowUtc:O}{Environment.NewLine}" + string.Join(Environment.NewLine, ids) + Environment.NewLine, new UTF8Encoding(false), ct);

                await log.InfoAsync("WeeklyMontage", $"building weekly montage {week} from {ids.Count} approved clips (top {top})");
                var r = await runner.RunAsync(root, ids, top, outDir, ct);
                await SetAsync(DoneKey, week, ct); // remember BEFORE the import: a restart must not build a second one
                var imp = await importer.ScanAsync(root, ct);
                await log.InfoAsync("WeeklyMontage", $"weekly montage {r.Id} ({r.Plays} plays) built; imported {imp.Imported} clip(s), waits for your APPROVE");
                return WeeklyMontageOutcome.Created;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (_failWeek != week) { _failWeek = week; _fails = 0; }
                _fails++; _lastFailUtc = nowUtc;
                await log.ErrorAsync("WeeklyMontage", $"weekly montage failed (attempt {_fails}/{MaxAttemptsPerWeek}): {ex.Message}", ex.ToString());
                return WeeklyMontageOutcome.Failed;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct))?.Value;
    }

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync([key], ct);
        if (row == null) db.Settings.Add(new KeyValueSetting { Key = key, Value = value }); else row.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
