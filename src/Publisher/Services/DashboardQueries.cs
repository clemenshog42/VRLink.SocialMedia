using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

public record PlatformSummary(Platform Platform, int Today, int Week, int Total, long Views, long Likes, int Queued, int Failed);
public record SeriesPoint(DateTime Day, double Value);
public record TypeStat(string Type, int Posts, double AvgViews, double AvgLikes, long TotalViews);
public record ClipRow(Clip Clip, List<PlatformPost> Posts);

/// <summary>Read models for the web UI.</summary>
public class DashboardQueries(IDbContextFactory<PublisherDb> dbf, TimeProvider time)
{
    public async Task<List<PlatformSummary>> SummaryAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var tz = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(time.GetUtcNow().UtcDateTime, tz);
        var todayUtc = TimeZoneInfo.ConvertTimeToUtc(nowLocal.Date, tz);
        var weekUtc = todayUtc.AddDays(-6);
        var posts = await db.Posts.AsNoTracking().ToListAsync();
        return Enum.GetValues<Platform>().Select(p =>
        {
            var pub = posts.Where(x => x.Platform == p && x.Status == PostStatus.Published).ToList();
            return new PlatformSummary(p,
                pub.Count(x => x.PublishedUtc >= todayUtc), pub.Count(x => x.PublishedUtc >= weekUtc), pub.Count,
                pub.Sum(x => x.LastViews ?? 0), pub.Sum(x => x.LastLikes ?? 0),
                posts.Count(x => x.Platform == p && x.Status is PostStatus.Queued or PostStatus.Scheduled),
                posts.Count(x => x.Platform == p && x.Status == PostStatus.Failed));
        }).ToList();
    }

    /// <summary>Total views per day (sum over posts of the latest snapshot that day; carried forward).</summary>
    public async Task<Dictionary<Platform, List<SeriesPoint>>> ViewsOverTimeAsync(int days = 30)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var from = time.GetUtcNow().UtcDateTime.Date.AddDays(-days + 1);
        var snaps = await db.Stats.AsNoTracking().Include(s => s.PlatformPost)
            .Where(s => s.TakenUtc >= from.AddDays(-30)).OrderBy(s => s.TakenUtc).ToListAsync();
        var result = new Dictionary<Platform, List<SeriesPoint>>();
        foreach (var platform in Enum.GetValues<Platform>())
        {
            var mine = snaps.Where(s => s.PlatformPost!.Platform == platform).ToList();
            var pts = new List<SeriesPoint>();
            for (var day = from; day <= time.GetUtcNow().UtcDateTime.Date; day = day.AddDays(1))
            {
                var end = day.AddDays(1);
                var total = mine.Where(s => s.TakenUtc < end).GroupBy(s => s.PlatformPostId)
                    .Sum(g => g.OrderBy(s => s.TakenUtc).Last().Views);
                pts.Add(new SeriesPoint(day, total));
            }
            result[platform] = pts;
        }
        return result;
    }

    public async Task<List<TypeStat>> ByTypeAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var posts = await db.Posts.AsNoTracking().Include(p => p.Clip).Where(p => p.Status == PostStatus.Published).ToListAsync();
        return posts.GroupBy(p => p.Clip!.Type)
            .Select(g => new TypeStat(g.Key, g.Count(), g.Average(p => (double)(p.LastViews ?? 0)), g.Average(p => (double)(p.LastLikes ?? 0)), g.Sum(p => p.LastViews ?? 0)))
            .OrderByDescending(t => t.AvgViews).ToList();
    }

    public async Task<List<PlatformPost>> TopPostsAsync(int n = 10)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Posts.AsNoTracking().Include(p => p.Clip).Where(p => p.Status == PostStatus.Published)
            .OrderByDescending(p => p.LastViews).Take(n).ToListAsync();
    }

    public async Task<int> PendingCountAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Clips.CountAsync(c => c.Status == ClipStatus.AwaitingApproval);
    }

    public async Task<List<ClipRow>> ClipsAsync(string? statusFilter, int take = 200)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var q = db.Clips.AsNoTracking().Include(c => c.Posts).AsQueryable();
        if (!string.IsNullOrEmpty(statusFilter) && Enum.TryParse<ClipStatus>(statusFilter, out var st)) q = q.Where(c => c.Status == st);
        var clips = await q.OrderByDescending(c => c.Id).Take(take).ToListAsync();
        return clips.Select(c => new ClipRow(c, c.Posts.OrderBy(p => p.Platform).ToList())).ToList();
    }

    public async Task<List<PlatformPost>> QueueAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Posts.AsNoTracking().Include(p => p.Clip)
            .Where(p => p.Status == PostStatus.Queued || p.Status == PostStatus.Scheduled || p.Status == PostStatus.Publishing || p.Status == PostStatus.Failed)
            .OrderBy(p => p.Status).ThenBy(p => p.ScheduledUtc).ThenByDescending(p => p.Clip!.Score).ToListAsync();
    }

    public async Task<List<PlatformPost>> RecentPublishedAsync(int n = 10)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return await db.Posts.AsNoTracking().Include(p => p.Clip).Where(p => p.Status == PostStatus.Published)
            .OrderByDescending(p => p.PublishedUtc).Take(n).ToListAsync();
    }

    public async Task<List<ErrorLogEntry>> LogAsync(LogLevelKind? minLevel, int take = 300)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var q = db.Log.AsNoTracking().AsQueryable();
        if (minLevel == LogLevelKind.Warning) q = q.Where(l => l.Level != LogLevelKind.Info);
        if (minLevel == LogLevelKind.Error) q = q.Where(l => l.Level == LogLevelKind.Error);
        return await q.OrderByDescending(l => l.Id).Take(take).ToListAsync();
    }

    public async Task ClearLogAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        await db.Log.ExecuteDeleteAsync();
    }
}
