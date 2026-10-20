using System.Security.Cryptography;
using System.Text;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Publishers;

public class FakePublisherFactory(TimeProvider time)
{
    private readonly FakePublisher _tiktok = new(Platform.TikTok, time);
    private readonly FakePublisher _instagram = new(Platform.Instagram, time);
    public FakePublisher Get(Platform p) => p == Platform.TikTok ? _tiktok : _instagram;
}

/// <summary>
/// DryRun: simulates a successful post (no network) and deterministic, growing fake stats
/// so the full pipeline (import → approve → schedule → publish → stats → charts) runs without accounts.
/// </summary>
public class FakePublisher(Platform platform, TimeProvider time) : IPlatformPublisher
{
    public Platform Platform { get; } = platform;
    public string Name => $"Fake{Platform}";
    /// <summary>Test hook: make the next N publishes fail (retryable).</summary>
    public int FailNext { get; set; }
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (SimulatedDelay > TimeSpan.Zero) await Task.Delay(SimulatedDelay, ct);
        if (!File.Exists(request.Clip.VideoPath))
            return PublishResult.Fail($"video file missing: {request.Clip.VideoPath}", retryable: false);
        if (FailNext > 0) { FailNext--; return PublishResult.Fail("simulated failure (DryRun)", retryable: true); }

        var id = "dry-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Platform + request.Clip.HighlightId)))[..12].ToLowerInvariant();
        var url = Platform == Platform.TikTok
            ? $"https://www.tiktok.com/@vrlink/video/{id}"
            : $"https://www.instagram.com/reel/{id}/";
        var privacy = Platform == Platform.TikTok ? "SELF_ONLY" : null;
        return new PublishResult(true, false, ExternalId: "dry-publish-" + id, PostId: id, PostUrl: url + "#dryrun", PrivacyLevel: privacy);
    }

    public Task<IReadOnlyList<PostStats>> GetStatsAsync(IReadOnlyList<PlatformPost> posts, RuntimeSettings settings, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var list = new List<PostStats>();
        foreach (var p in posts)
        {
            if (p.PostId == null || p.PublishedUtc == null) continue;
            var seed = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(p.PostId)), 0);
            var potential = 200 + seed % 5000;                 // final views
            var hours = Math.Max(0, (now - p.PublishedUtc.Value).TotalHours);
            var views = (long)(potential * (1 - Math.Exp(-hours / 30.0)) + 5);  // saturating curve
            list.Add(new PostStats(p.Id, views, views / 12, views / 80, views / 150, views / 200, (long)(views * 0.8), p.PostUrl));
        }
        return Task.FromResult<IReadOnlyList<PostStats>>(list);
    }
}
