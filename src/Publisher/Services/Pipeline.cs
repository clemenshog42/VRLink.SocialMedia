using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Oauth;
using VRLinkPublisher.Publishers;

namespace VRLinkPublisher.Services;

/// <summary>
/// One step of each background worker. Workers call these in a loop; tests call them directly.
/// All methods are safe to call repeatedly (idempotent per state).
/// </summary>
public class Pipeline(
    IDbContextFactory<PublisherDb> dbf, SettingsStore settings, IMediaProbe probe, PublisherResolver publishers,
    EventLog log, TimeProvider time, IServiceProvider sp)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    public static IEnumerable<Platform> EnabledPlatforms(RuntimeSettings s)
    {
        if (s.TikTokEnabled) yield return Platform.TikTok;
        if (s.InstagramEnabled) yield return Platform.Instagram;
    }

    public const string Owner = "owner";

    // ------------------------------------------------------------ ClipChecker (never approves)
    public async Task<int> CheckNewAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var fresh = await db.Clips.Where(c => c.Status == ClipStatus.New).OrderBy(c => c.Id).Take(50).ToListAsync(ct);
        foreach (var clip in fresh)
        {
            ProbeResult? pr = null;
            if (!s.DryRun && probe.Available) pr = await probe.ProbeAsync(clip.VideoPath, ct);
            string? dup = clip.ContentHash == null ? null : await db.Clips
                .Where(c => c.Id != clip.Id && c.ContentHash == clip.ContentHash && c.Id < clip.Id)
                .Select(c => c.HighlightId).FirstOrDefaultAsync(ct);
            var d = ApprovalRules.Evaluate(clip, pr, s, dup);
            clip.StatusReason = d.Reason;
            clip.DecidedUtc = Now;
            clip.Status = d.Outcome == ApprovalOutcome.AwaitingApproval ? ClipStatus.AwaitingApproval : ClipStatus.Rejected;
            await db.SaveChangesAsync(ct);
            await log.InfoAsync("ClipChecker", $"{clip.HighlightId}: {clip.Status} ({d.Reason})", clip.Id);
        }
        return fresh.Count;
    }

    private static async Task EnsurePostsAsync(PublisherDb db, Clip clip, RuntimeSettings s, CancellationToken ct)
    {
        var existing = await db.Posts.Where(p => p.ClipId == clip.Id).Select(p => p.Platform).ToListAsync(ct);
        foreach (var p in EnabledPlatforms(s).Except(existing))
            db.Posts.Add(new PlatformPost { ClipId = clip.Id, Platform = p, Status = PostStatus.Queued });
    }

    // ------------------------------------------------------------ manual actions (UI)
    /// <summary>
    /// The ONLY way a clip gets approved: a human click in the web UI (single APPROVE or explicit multi-select).
    /// Optional caption/hashtag edits are saved with it. Refused while a .rejected marker exists.
    /// </summary>
    public async Task ApproveManuallyAsync(int clipId, string approvedBy = Owner, string? caption = null, string? hashtags = null, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var clip = await db.Clips.FindAsync([clipId], ct) ?? throw new InvalidOperationException("clip not found");
        if (File.Exists(ClipImporter.RejectMarkerPath(clip.VideoPath)))
            throw new InvalidOperationException($"{clip.HighlightId} has a .rejected marker from the operator; remove it first.");
        if (caption != null) clip.Caption = caption.Trim();
        if (hashtags != null) clip.Hashtags = ClipImporter.NormalizeHashtags(hashtags.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
        clip.Status = ClipStatus.Approved;
        clip.StatusReason = $"approved by {approvedBy}";
        clip.DecidedUtc = Now;
        clip.ApprovedAtUtc = Now;
        clip.ApprovedBy = approvedBy;
        await EnsurePostsAsync(db, clip, s, ct);
        foreach (var p in await db.Posts.Where(p => p.ClipId == clipId && p.Status == PostStatus.Cancelled).ToListAsync(ct))
            p.Status = PostStatus.Queued;
        await db.SaveChangesAsync(ct);
        await log.InfoAsync("UI", $"{clip.HighlightId} approved by {approvedBy}", clip.Id);
    }

    /// <summary>Explicit multi-select approve (one human click for the selected clips). Returns the number approved.</summary>
    public async Task<int> ApproveSelectedAsync(IEnumerable<int> clipIds, string approvedBy = Owner, CancellationToken ct = default)
    {
        int n = 0;
        foreach (var id in clipIds.Distinct())
        {
            try { await ApproveManuallyAsync(id, approvedBy, ct: ct); n++; }
            catch (InvalidOperationException ex) { await log.WarnAsync("UI", "Bulk approve skipped a clip", ex.Message, id); }
        }
        return n;
    }

    /// <summary>Caption/hashtag edits without approving.</summary>
    public async Task UpdateTextAsync(int clipId, string caption, string hashtags, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var clip = await db.Clips.FindAsync([clipId], ct) ?? throw new InvalidOperationException("clip not found");
        clip.Caption = caption.Trim();
        clip.Hashtags = ClipImporter.NormalizeHashtags(hashtags.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
        foreach (var p in await db.Posts.Where(p => p.ClipId == clipId && (p.Status == PostStatus.Queued || p.Status == PostStatus.Scheduled)).ToListAsync(ct))
            p.Caption = null; // recomposed from the edited text
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Defense in depth: true only for a human-approved clip without operator veto.</summary>
    public static bool IsApprovedForUpload(Clip c, out string why)
    {
        if (c.Status != ClipStatus.Approved) { why = $"status {c.Status}"; return false; }
        if (c.ApprovedAtUtc == null || string.IsNullOrEmpty(c.ApprovedBy)) { why = "no manual approval recorded (ApprovedAtUtc missing)"; return false; }
        if (File.Exists(ClipImporter.RejectMarkerPath(c.VideoPath))) { why = "operator .rejected marker present"; return false; }
        why = "";
        return true;
    }

    public async Task RejectAsync(int clipId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var clip = await db.Clips.FindAsync([clipId], ct) ?? throw new InvalidOperationException("clip not found");
        clip.Status = ClipStatus.Rejected;
        clip.StatusReason = "rejected manually";
        clip.DecidedUtc = Now;
        clip.ApprovedAtUtc = null;
        clip.ApprovedBy = null;
        foreach (var p in await db.Posts.Where(p => p.ClipId == clipId && (p.Status == PostStatus.Queued || p.Status == PostStatus.Scheduled)).ToListAsync(ct))
            p.Status = PostStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        await log.InfoAsync("UI", $"{clip.HighlightId} rejected manually", clip.Id);
    }

    /// <summary>Failed/Cancelled post → publish at the next worker tick.</summary>
    public async Task RetryAsync(int postId, CancellationToken ct = default) => await PublishNowAsync(postId, resetAttempts: true, ct);

    public async Task PublishNowAsync(int postId, bool resetAttempts = false, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var post = await db.Posts.Include(p => p.Clip).FirstAsync(p => p.Id == postId, ct);
        if (post.Status is PostStatus.Published or PostStatus.Publishing) return;
        if (!IsApprovedForUpload(post.Clip!, out var why))
            throw new InvalidOperationException($"{post.Clip!.HighlightId} is not approved for upload: {why}");
        if (post.Caption == null) (post.Caption, post.CaptionVariant) = await ComposeCaptionAsync(post.Clip!, post.Platform, s, ct);
        post.Status = PostStatus.Scheduled;
        post.ScheduledUtc = Now;
        post.NextAttemptUtc = null;
        post.SlotKey ??= "manual";
        if (resetAttempts) { post.Attempts = 0; post.LastError = null; }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Hook-first caption variant picked by the V5 model (Thompson sampling + exploration); the variant id is stored per post.</summary>
    private async Task<(string Caption, string Variant)> ComposeCaptionAsync(Clip clip, Platform platform, RuntimeSettings s, CancellationToken ct)
    {
        var variants = CaptionComposer.Variants(s, clip.Format, clip.Type);
        var learning = sp.GetRequiredService<LearningService>();
        var id = await learning.PickVariantAsync(clip, platform, variants.Select(v => v.Id).ToList(), s, ct);
        var tpl = variants.First(v => v.Id == id).Template;
        return (CaptionComposer.ComposeWith(clip, s, tpl, platform == Platform.TikTok ? CaptionComposer.TikTokMaxChars : CaptionComposer.InstagramMaxChars), id);
    }

    // ------------------------------------------------------------ Scheduler
    /// <summary>For every enabled platform: if a slot is due and not yet filled, assign the best queued clip.</summary>
    public async Task<int> ScheduleDueAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        if (s.PauseAll) return 0;
        var tz = SlotPlanner.ResolveTimeZone(s.TimeZoneId);
        int assigned = 0;
        await using var db = await dbf.CreateDbContextAsync(ct);
        await CancelUnapprovedAsync(db, "Scheduler", ct);
        foreach (var platform in EnabledPlatforms(s))
        {
            // V5: learned best hours (after ≥ 10 measured posts, unless switched off) or the configured slots
            var (slotList, _, _) = await sp.GetRequiredService<LearningService>().SlotsAsync(platform, s, ct);
            var slots = SlotPlanner.ParseSlots(slotList);
            var due = SlotPlanner.DueSlot(Now, slots, tz, TimeSpan.FromMinutes(s.SlotGraceMinutes));
            if (due == null) continue;
            if (await db.Posts.AnyAsync(p => p.Platform == platform && p.SlotKey == due.Key, ct)) continue; // already filled

            var lastType = await db.Posts.Where(p => p.Platform == platform && p.ScheduledUtc != null
                                                     && p.Status != PostStatus.Queued && p.Status != PostStatus.Cancelled)
                .OrderByDescending(p => p.ScheduledUtc).Select(p => p.Clip!.Type).FirstOrDefaultAsync(ct);
            var candidates = await db.Posts.Where(p => p.Platform == platform && p.Status == PostStatus.Queued && p.Clip!.Status == ClipStatus.Approved && p.Clip.ApprovedAtUtc != null)
                .Select(p => new QueueCandidate(p.Id, p.Clip!.Type, p.Clip.Score, p.Clip.CreatedUtc)).ToListAsync(ct);
            var pick = ClipPicker.Pick(candidates, lastType, s.StrictTypeAlternation);
            if (pick == null)
            {
                if (candidates.Count > 0)
                    await log.InfoAsync("Scheduler", $"{platform} slot {due.Key} skipped: only '{lastType}' clips waiting (strict type alternation)");
                continue;
            }
            var post = await db.Posts.Include(p => p.Clip).FirstAsync(p => p.Id == pick.PostId, ct);
            (post.Caption, post.CaptionVariant) = await ComposeCaptionAsync(post.Clip!, platform, s, ct);
            post.SlotKey = due.Key;
            post.ScheduledUtc = due.Utc;
            post.Status = PostStatus.Scheduled;
            await db.SaveChangesAsync(ct);
            assigned++;
            await log.InfoAsync("Scheduler", $"{platform} slot {due.Key}: {post.Clip!.HighlightId} ({post.Clip.Type})", post.ClipId);
        }
        return assigned;
    }

    /// <summary>Queue entries whose clip lacks a manual approval (e.g. status tampered in the DB) are cancelled and logged as errors.</summary>
    private async Task CancelUnapprovedAsync(PublisherDb db, string source, CancellationToken ct)
    {
        var open = await db.Posts.Include(p => p.Clip)
            .Where(p => p.Status == PostStatus.Queued || p.Status == PostStatus.Scheduled).ToListAsync(ct);
        foreach (var p in open)
        {
            if (IsApprovedForUpload(p.Clip!, out var why)) continue;
            p.Status = PostStatus.Cancelled;
            p.LastError = "refused: " + why;
            await db.SaveChangesAsync(ct);
            await log.ErrorAsync(source, $"Refused {p.Platform} post of {p.Clip!.HighlightId}: {why}", null, p.ClipId);
        }
    }

    // ------------------------------------------------------------ PublishWorker
    public async Task<int> PublishDueAsync(CancellationToken ct = default)
    {
        await _publishGate.WaitAsync(ct);
        try
        {
            var s = await settings.GetAsync(ct);
            if (s.PauseAll) return 0;
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = Now;
            var due = await db.Posts.Include(p => p.Clip)
                .Where(p => p.Status == PostStatus.Scheduled && p.ScheduledUtc <= now && (p.NextAttemptUtc == null || p.NextAttemptUtc <= now))
                .OrderBy(p => p.ScheduledUtc).Take(5).ToListAsync(ct);
            int done = 0;
            foreach (var post in due)
            {
                // Last gate right before the upload: manual approval recorded + operator marker re-checked now.
                if (!IsApprovedForUpload(post.Clip!, out var why))
                {
                    post.Status = PostStatus.Cancelled;
                    post.LastError = "refused: " + why;
                    await db.SaveChangesAsync(ct);
                    await log.ErrorAsync("PublishWorker", $"Refused upload of {post.Clip!.HighlightId} to {post.Platform}: {why}", null, post.ClipId);
                    continue;
                }
                post.Status = PostStatus.Publishing;
                post.Attempts++;
                await db.SaveChangesAsync(ct);

                var pub = publishers.Get(post.Platform, s);
                PublishResult r;
                try { r = await pub.PublishAsync(new PublishRequest(post.Clip!, post, s), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { r = PublishResult.Fail(ex.Message, retryable: true); }

                post.ExternalId = r.ExternalId ?? post.ExternalId;
                if (r.Success)
                {
                    post.Status = PostStatus.Published;
                    post.PublishedUtc = Now;
                    post.PostId = r.PostId;
                    post.PostUrl = r.PostUrl;
                    post.PrivacyLevel = r.PrivacyLevel;
                    post.LastError = r.Error;
                    post.NextStatsPollUtc = Now.AddMinutes(30);
                    done++;
                    await log.InfoAsync(pub.Name, $"Published {post.Clip!.HighlightId} → {r.PostUrl ?? r.PostId ?? r.ExternalId}", post.ClipId);
                }
                else
                {
                    post.LastError = r.Error;
                    if (r.Retryable && post.Attempts < s.MaxAttempts)
                    {
                        post.Status = PostStatus.Scheduled;
                        post.NextAttemptUtc = Now.AddMinutes(Math.Min(120, 2 * Math.Pow(2, post.Attempts - 1))); // 2,4,8,16.. min
                        await log.WarnAsync(pub.Name, $"Publish {post.Clip!.HighlightId} failed (attempt {post.Attempts}), retry at {post.NextAttemptUtc:HH:mm} UTC", r.Error, post.ClipId);
                    }
                    else
                    {
                        post.Status = PostStatus.Failed;
                        await log.ErrorAsync(pub.Name, $"Publish {post.Clip!.HighlightId} failed permanently", r.Error, post.ClipId);
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            return done;
        }
        finally { _publishGate.Release(); }
    }

    // ------------------------------------------------------------ StatsPoller
    public static DateTime NextPoll(DateTime publishedUtc, DateTime nowUtc, RuntimeSettings s) =>
        nowUtc - publishedUtc < TimeSpan.FromDays(s.StatsIntensiveDays)
            ? nowUtc.AddHours(s.StatsIntensiveHours)
            : nowUtc.AddDays(1);

    public async Task<int> PollStatsAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = Now;
        var due = await db.Posts.Where(p => p.Status == PostStatus.Published && p.NextStatsPollUtc != null && p.NextStatsPollUtc <= now)
            .OrderBy(p => p.NextStatsPollUtc).Take(100).ToListAsync(ct);
        int stored = 0;
        foreach (var group in due.GroupBy(p => p.Platform))
        {
            IReadOnlyList<PostStats> stats;
            try { stats = await publishers.Get(group.Key, s).GetStatsAsync(group.ToList(), s, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await log.WarnAsync("StatsPoller", $"{group.Key} stats failed", ex.Message);
                foreach (var p in group) p.NextStatsPollUtc = now.AddHours(1);
                await db.SaveChangesAsync(ct);
                continue;
            }
            foreach (var p in group)
            {
                var st = stats.FirstOrDefault(x => x.PlatformPostId == p.Id);
                if (st != null)
                {
                    db.Stats.Add(new StatSnapshot
                    {
                        PlatformPostId = p.Id, TakenUtc = now, Views = st.Views, Likes = st.Likes, Comments = st.Comments,
                        Shares = st.Shares, Saves = st.Saves, Reach = st.Reach,
                    });
                    p.LastViews = st.Views;
                    p.LastLikes = st.Likes;
                    if (string.IsNullOrEmpty(p.PostUrl) && st.PostUrl != null) p.PostUrl = st.PostUrl;
                    stored++;
                }
                var published = p.PublishedUtc ?? now;
                p.NextStatsPollUtc = now - published > TimeSpan.FromDays(s.StatsStopAfterDays) ? null : NextPoll(published, now, s);
            }
            await db.SaveChangesAsync(ct);
        }
        return stored;
    }

    // ------------------------------------------------------------ TokenRefresher
    public record TokenWarning(Platform Platform, string Message);

    /// <summary>Warnings for tokens expiring within 7 days (UI banner + log).</summary>
    public async Task<List<TokenWarning>> TokenWarningsAsync(CancellationToken ct = default)
    {
        var list = new List<TokenWarning>();
        var accounts = sp.GetRequiredService<AccountService>();
        var soon = Now.AddDays(7);
        foreach (var a in await accounts.GetConnectionsAsync(ct))
        {
            // TikTok: access token lives 24 h and is refreshed automatically → only the refresh token matters.
            var critical = a.Platform == Platform.TikTok ? a.RefreshExpiresUtc : a.AccessExpiresUtc ?? a.RefreshExpiresUtc;
            if (critical is { } exp && exp <= soon)
                list.Add(new(a.Platform, exp <= Now
                    ? $"{a.Platform} login expired on {exp:yyyy-MM-dd}. Click Connect again."
                    : $"{a.Platform} login expires on {exp:yyyy-MM-dd}. Click Connect again before then."));
            if (a.Platform == Platform.TikTok && a.AccessExpiresUtc is { } ax && ax <= Now && a.RefreshExpiresUtc > Now)
                list.Add(new(a.Platform, "TikTok access token expired, automatic refresh pending."));
        }
        return list;
    }

    public async Task RefreshTokensAsync(CancellationToken ct = default)
    {
        var accounts = sp.GetRequiredService<AccountService>();
        var oauth = sp.GetRequiredService<OAuthService>();
        foreach (var a in await accounts.GetConnectionsAsync(ct))
        {
            try
            {
                if (a.Platform == Platform.TikTok && a.AccessExpiresUtc is { } exp && exp - Now < TimeSpan.FromHours(3)
                    && (a.RefreshExpiresUtc == null || a.RefreshExpiresUtc > Now))
                {
                    await oauth.RefreshTikTokAsync(ct);
                    await log.InfoAsync("TokenRefresher", "TikTok access token refreshed");
                }
                else if (a.Platform == Platform.Instagram && (a.LastRefreshUtc == null || Now - a.LastRefreshUtc > TimeSpan.FromDays(1)))
                {
                    var (valid, pageExp) = await oauth.CheckInstagramTokenAsync(ct);
                    await accounts.SaveConnectionAsync(new AccountConnection { Platform = Platform.Instagram, AccessExpiresUtc = pageExp, LastRefreshUtc = Now }, null, null, ct);
                    if (!valid) await log.ErrorAsync("TokenRefresher", "Instagram Page token is no longer valid. Click Connect again.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await log.ErrorAsync("TokenRefresher", $"{a.Platform} token refresh failed", ex.Message);
            }
        }
        foreach (var w in await TokenWarningsAsync(ct))
        {
            var a = await accounts.GetConnectionAsync(w.Platform, ct);
            if (a?.LastWarningUtc == null || Now - a.LastWarningUtc > TimeSpan.FromDays(1))
            {
                await log.WarnAsync("TokenRefresher", w.Message);
                await accounts.MarkWarnedAsync(w.Platform, ct);
            }
        }
    }
}
