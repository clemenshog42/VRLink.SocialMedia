using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLink.Shared.Contracts;

namespace VRLinkPublisher.Services;

public record ImportResult(int Imported, int Skipped, int Waiting, int Invalid, int MarkedRejected = 0);

/// <summary>
/// Idempotent import of &lt;ClipsRoot&gt;/&lt;yyyy-MM-dd&gt;/&lt;highlightId&gt;.{mp4,jpg,json}.
/// A clip is imported once (unique HighlightId). The json is written last by the renderer;
/// if the mp4 is missing or still locked we wait for the next scan.
/// </summary>
public class ClipImporter(IDbContextFactory<PublisherDb> dbf, EventLog log, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ImportResult> ScanAsync(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return new ImportResult(0, 0, 0, 0);
        await _gate.WaitAsync(ct); // watcher + periodic rescan never import concurrently
        try
        {
            int imported = 0, skipped = 0, waiting = 0, invalid = 0;
            await using var db = await dbf.CreateDbContextAsync(ct);
            var known = (await db.Clips.Select(c => c.HighlightId).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var json in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(p => p))
            {
                ct.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(json);
                if (known.Contains(stem)) { skipped++; continue; } // cheap path: file name == highlightId

                ClipManifest? m;
                try
                {
                    await using var fs = new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.Read);
                    m = await JsonSerializer.DeserializeAsync<ClipManifest>(fs, ClipManifest.JsonOptions, ct);
                }
                catch (IOException) { waiting++; continue; } // still being written
                catch (JsonException ex)
                {
                    invalid++;
                    await log.WarnAsync("ClipWatcher", $"Invalid clip json {json}", ex.Message);
                    continue;
                }
                if (m == null || string.IsNullOrWhiteSpace(m.HighlightId) || string.IsNullOrWhiteSpace(m.Type))
                {
                    invalid++;
                    await log.WarnAsync("ClipWatcher", $"Clip json without highlightId/type: {json}");
                    continue;
                }
                if (known.Contains(m.HighlightId)) { skipped++; continue; }

                var dir = Path.GetDirectoryName(json)!;
                var mp4 = Path.Combine(dir, stem + ".mp4");
                var jpg = Path.Combine(dir, stem + ".jpg");
                if (!File.Exists(mp4) || IsLocked(mp4)) { waiting++; continue; }

                var size = new FileInfo(mp4).Length;
                var rejectedByOperator = File.Exists(RejectMarkerPath(mp4));
                var clip = new Clip
                {
                    HighlightId = m.HighlightId.Trim(),
                    Type = m.Type.Trim().ToLowerInvariant(),
                    Score = m.Score,
                    MatchId = m.MatchId,
                    Round = m.Round,
                    DurationSec = m.DurationSec,
                    Width = m.Width,
                    Height = m.Height,
                    Caption = m.Caption,
                    Hashtags = NormalizeHashtags(m.Hashtags),
                    Venue = m.Venue,
                    HeroIsBot = m.HeroIsBot ?? false,
                    CreatedUtc = m.CreatedUtc?.ToUniversalTime() ?? File.GetLastWriteTimeUtc(json),
                    ImportedUtc = time.GetUtcNow().UtcDateTime,
                    VideoPath = Path.GetFullPath(mp4),
                    ThumbPath = File.Exists(jpg) ? Path.GetFullPath(jpg) : null,
                    JsonPath = Path.GetFullPath(json),
                    FileSizeBytes = size,
                    ContentHash = await HashAsync(mp4, ct),
                    Status = rejectedByOperator ? ClipStatus.Rejected : ClipStatus.New,
                    StatusReason = rejectedByOperator ? OperatorRejectReason : null,
                    DecidedUtc = rejectedByOperator ? time.GetUtcNow().UtcDateTime : null,
                };
                db.Clips.Add(clip);
                try
                {
                    await db.SaveChangesAsync(ct);
                    known.Add(clip.HighlightId);
                    imported++;
                    await log.InfoAsync("ClipWatcher", $"Imported {clip.HighlightId} ({clip.Type}, score {clip.Score:0.#})", clip.Id);
                }
                catch (DbUpdateException)
                {
                    // Unique index hit (raced with another import): idempotent, just drop it.
                    db.Entry(clip).State = EntityState.Detached;
                    known.Add(clip.HighlightId);
                    skipped++;
                }
            }
            var marked = await ApplyRejectMarkersAsync(db, ct);
            return new ImportResult(imported, skipped, waiting, invalid, marked);
        }
        finally { _gate.Release(); }
    }

    public const string OperatorRejectReason = "rejected by operator (.rejected marker)";

    /// <summary>The Unity operator writes &lt;highlightId&gt;.rejected next to the mp4 to veto a clip.</summary>
    public static string RejectMarkerPath(string videoPath) =>
        Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath) + ".rejected");

    /// <summary>Marker written after the import: reject the clip and cancel its unpublished queue entries (already published posts stay).</summary>
    private async Task<int> ApplyRejectMarkersAsync(PublisherDb db, CancellationToken ct)
    {
        var open = await db.Clips.Include(c => c.Posts).Where(c => c.Status != ClipStatus.Rejected && !c.Posts.Any(p => p.Status == PostStatus.Published || p.Status == PostStatus.Publishing))
            .ToListAsync(ct);
        int n = 0;
        foreach (var c in open.Where(c => File.Exists(RejectMarkerPath(c.VideoPath))))
        {
            c.Status = ClipStatus.Rejected;
            c.StatusReason = OperatorRejectReason;
            c.DecidedUtc = time.GetUtcNow().UtcDateTime;
            c.ApprovedAtUtc = null;
            c.ApprovedBy = null;
            foreach (var p in c.Posts.Where(p => p.Status is PostStatus.Queued or PostStatus.Scheduled))
                p.Status = PostStatus.Cancelled;
            n++;
        }
        if (n > 0)
        {
            await db.SaveChangesAsync(ct);
            await log.InfoAsync("ClipWatcher", $"{n} clip(s) rejected by operator marker");
        }
        return n;
    }

    public static string NormalizeHashtags(IEnumerable<string>? tags) =>
        string.Join(' ', (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0)
            .Select(t => t.StartsWith('#') ? t : "#" + t).Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool IsLocked(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return false; }
        catch (IOException) { return true; }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }
}
