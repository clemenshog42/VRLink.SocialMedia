using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class ImporterTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Import_is_idempotent()
    {
        await using var h = new TestHost(T0);
        h.WriteClip("hl-001", "ace", 90);
        var imp = h.Get<ClipImporter>();

        var r1 = await imp.ScanAsync(h.ClipsRoot);
        var r2 = await imp.ScanAsync(h.ClipsRoot);
        var r3 = await Task.WhenAll(imp.ScanAsync(h.ClipsRoot), imp.ScanAsync(h.ClipsRoot)); // concurrent watcher + rescan

        Assert.Equal(1, r1.Imported);
        Assert.Equal(0, r2.Imported);
        Assert.Equal(1, r2.Skipped);
        Assert.All(r3, r => Assert.Equal(0, r.Imported));
        await using var db = h.Db();
        var c = await db.Clips.SingleAsync();
        Assert.Equal("hl-001", c.HighlightId);
        Assert.Equal("ace", c.Type);
        Assert.Equal(ClipStatus.New, c.Status);
        Assert.Equal("#vrlink #clutch", c.Hashtags);
        Assert.NotNull(c.ThumbPath);
        Assert.NotNull(c.ContentHash);
        Assert.Equal(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc), c.CreatedUtc);
    }

    [Fact]
    public async Task Json_without_mp4_waits_until_video_exists()
    {
        await using var h = new TestHost(T0);
        var dir = h.WriteClip("hl-002", "clutch", 80, writeMp4: false);
        var imp = h.Get<ClipImporter>();

        var r1 = await imp.ScanAsync(h.ClipsRoot);
        Assert.Equal(0, r1.Imported);
        Assert.Equal(1, r1.Waiting);

        await File.WriteAllBytesAsync(Path.Combine(dir, "hl-002.mp4"), [1, 2, 3]);
        var r2 = await imp.ScanAsync(h.ClipsRoot);
        Assert.Equal(1, r2.Imported);
    }

    [Fact]
    public async Task Locked_mp4_is_not_imported_yet()
    {
        await using var h = new TestHost(T0);
        var dir = h.WriteClip("hl-003", "ace", 80);
        var imp = h.Get<ClipImporter>();
        await using (new FileStream(Path.Combine(dir, "hl-003.mp4"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(1, (await imp.ScanAsync(h.ClipsRoot)).Waiting);
        }
        Assert.Equal(1, (await imp.ScanAsync(h.ClipsRoot)).Imported);
    }

    [Fact]
    public async Task Invalid_json_is_logged_and_skipped()
    {
        await using var h = new TestHost(T0);
        var d = Path.Combine(h.ClipsRoot, "2026-10-19");
        Directory.CreateDirectory(d);
        await File.WriteAllTextAsync(Path.Combine(d, "broken.json"), "{ not json");
        await File.WriteAllTextAsync(Path.Combine(d, "noid.json"), "{\"type\":\"ace\"}");
        h.WriteClip("hl-004", "ace", 80);

        var r = await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        Assert.Equal(1, r.Imported);
        Assert.Equal(2, r.Invalid);
        await using var db = h.Db();
        Assert.True(await db.Log.AnyAsync(l => l.Level == LogLevelKind.Warning));
    }

    [Fact]
    public async Task Same_highlight_id_in_two_day_folders_is_imported_once()
    {
        await using var h = new TestHost(T0);
        h.WriteClip("hl-005", "ace", 80, day: "2026-10-19");
        h.WriteClip("hl-005", "ace", 80, day: "2026-10-20");
        var r = await h.Get<ClipImporter>().ScanAsync(h.ClipsRoot);
        Assert.Equal(1, r.Imported);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public async Task Missing_root_is_harmless()
    {
        await using var h = new TestHost(T0);
        var r = await h.Get<ClipImporter>().ScanAsync(Path.Combine(h.Dir, "does-not-exist"));
        Assert.Equal(0, r.Imported);
    }
}
