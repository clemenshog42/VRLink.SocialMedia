using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Publishers;

public record PublishRequest(Clip Clip, PlatformPost Post, RuntimeSettings Settings);

public record PublishResult(
    bool Success,
    bool Retryable,
    string? ExternalId = null,
    string? PostId = null,
    string? PostUrl = null,
    string? PrivacyLevel = null,
    string? Error = null)
{
    public static PublishResult Fail(string error, bool retryable, string? externalId = null) =>
        new(false, retryable, ExternalId: externalId, Error: error);
}

public record PostStats(int PlatformPostId, long Views, long Likes, long Comments, long Shares, long Saves, long Reach, string? PostUrl = null);

/// <summary>One social platform. Implementations: TikTokPublisher, InstagramPublisher, FakePublisher (DryRun).</summary>
public interface IPlatformPublisher
{
    Platform Platform { get; }
    string Name { get; }
    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);
    /// <summary>Returns stats for the posts it could resolve (posts without PostId are skipped).</summary>
    Task<IReadOnlyList<PostStats>> GetStatsAsync(IReadOnlyList<PlatformPost> posts, RuntimeSettings settings, CancellationToken ct);
}

/// <summary>Real publisher, or FakePublisher when the runtime setting DryRun is on.</summary>
public class PublisherResolver(IEnumerable<IPlatformPublisher> all, FakePublisherFactory fakes)
{
    public IPlatformPublisher Get(Platform platform, RuntimeSettings settings) =>
        settings.DryRun ? fakes.Get(platform) : all.First(p => p.Platform == platform);
}

/// <summary>Thrown for API failures; Retryable tells the PublishWorker whether to back off and retry.</summary>
public class PlatformApiException(string message, bool retryable, int? httpStatus = null) : Exception(message)
{
    public bool Retryable { get; } = retryable;
    public int? HttpStatus { get; } = httpStatus;
}
