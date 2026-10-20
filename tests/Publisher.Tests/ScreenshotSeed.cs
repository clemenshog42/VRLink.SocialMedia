using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Tests;

/// <summary>
/// Not a real test: when VRLINK_SEED_DIR is set it creates &lt;dir&gt;/publisher.db with a synthetic posting history and
/// four clips awaiting approval (for the docs/v5 screenshots). Otherwise it does nothing.
/// </summary>
public class ScreenshotSeed
{
    [Fact]
    public async Task Seed_synthetic_insights_data_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("VRLINK_SEED_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddPublisherCore(o => { o.DataDir = dir; o.ClipsRoot = Path.Combine(dir, "clips-empty"); }, Path.Combine(dir, "publisher.db"));
        await using var sp = services.BuildServiceProvider();
        await ServiceSetup.InitDatabaseAsync(sp);
        var now = DateTime.UtcNow;
        await using var db = await sp.GetRequiredService<IDbContextFactory<PublisherDb>>().CreateDbContextAsync();
        string[] formats = ["irl", "split", "game", "reaction", "irl", "game"];
        string[] types = ["ace", "kills3", "clutch", "kills4"];
        int[] hours = [12, 19, 11, 20, 21, 9];
        await SyntheticHistory.SeedAsync(db, now, 72,
            i => (formats[i % 6], formats[i % 6] == "reaction" ? "reaction" : types[i % 4], i % 3 == 0 ? Platform.TikTok : Platform.Instagram, hours[i % 6], $"{formats[i % 6]}.*#{i % 2}"),
            (f, t, p, hr, v) => (f switch { "reaction" => 0.9, "irl" => 0.7, "split" => 0.3, _ => -0.3 }) + (t switch { "ace" => 0.4, "clutch" => 0.35, "kills4" => 0.1, _ => 0 })
                                + (hr is 19 or 20 or 21 ? 0.3 : 0) + (v != null && v.EndsWith("#1") ? 0.2 : 0));

        // awaiting approval: sample clips (thumbnails) with different formats/types
        var samples = @"C:\vr\VRLink.SocialMedia\samples\clips\2026-10-19";
        var awaiting = new[] { ("demo-defuse-003", (string?)null, "kills3", 92.0), ("demo-ace-001", "irl", "ace", 70.0), ("demo-clutch-002", "split", "clutch", 85.0), ("demo-ace-001", "reaction", "reaction", 60.0) };
        int n = 0;
        foreach (var (file, format, type, score) in awaiting)
        {
            db.Clips.Add(new Clip
            {
                HighlightId = $"await-{++n}-{format ?? "game"}-{type}", Type = type, Format = format, Score = score, MatchId = "await-match", Status = ClipStatus.AwaitingApproval,
                StatusReason = "checks ok (json), awaiting owner approval", VideoPath = Path.Combine(samples, file + ".mp4"), ThumbPath = Path.Combine(samples, file + ".jpg"),
                JsonPath = Path.Combine(samples, file + ".json"), CreatedUtc = now.AddMinutes(-n), ImportedUtc = now, Width = 1080, Height = 1920, DurationSec = 14, Caption = "Highlight from the session",
            });
        }
        await db.SaveChangesAsync();
    }
}
