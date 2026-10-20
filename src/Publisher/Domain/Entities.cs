namespace VRLinkPublisher.Domain;

public enum Platform { TikTok, Instagram }

public enum ClipStatus
{
    /// <summary>Imported, not yet evaluated by the AutoApprover.</summary>
    New,
    /// <summary>Passed the checks; waits for the owner's APPROVE click in the web UI (hard rule: no automatic approval).</summary>
    AwaitingApproval,
    /// <summary>Approved by a human. Only valid together with Clip.ApprovedAtUtc.</summary>
    Approved,
    Rejected,
}

public enum PostStatus
{
    /// <summary>Approved clip waiting for a slot.</summary>
    Queued,
    /// <summary>Assigned to a slot; the PublishWorker picks it up when ScheduledUtc is reached.</summary>
    Scheduled,
    Publishing,
    Published,
    Failed,
    Cancelled,
}

/// <summary>One imported highlight clip (see README "Clip contract").</summary>
public class Clip
{
    public int Id { get; set; }
    public string HighlightId { get; set; } = "";
    public string Type { get; set; } = "";
    public double Score { get; set; }
    public string? MatchId { get; set; }
    public int Round { get; set; }
    public double DurationSec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Caption { get; set; }
    /// <summary>Space separated, each with leading '#'.</summary>
    public string Hashtags { get; set; } = "";
    public string? Venue { get; set; }
    /// <summary>Optional contract field "heroIsBot": the highlight's main player is a practice bot.</summary>
    public bool HeroIsBot { get; set; }
    /// <summary>V2 contract: "irl" | "split" | "game" (null = older clip = game).</summary>
    public string? Format { get; set; }
    /// <summary>V2 contract: comma separated sources (arenaCam:cam1, overlay, pov, gameAudio, roomAudio).</summary>
    public string? Sources { get; set; }
    public bool? MusicFree { get; set; }
    /// <summary>V2 contract: anonymous player labels, comma separated (hero first).</summary>
    public string? Players { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ImportedUtc { get; set; }

    public string VideoPath { get; set; } = "";
    public string? ThumbPath { get; set; }
    public string JsonPath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    /// <summary>SHA-256 of the mp4 (hex). Used for the duplicate check.</summary>
    public string? ContentHash { get; set; }

    public ClipStatus Status { get; set; } = ClipStatus.New;
    public string? StatusReason { get; set; }
    public DateTime? DecidedUtc { get; set; }
    /// <summary>Set ONLY by a human APPROVE click (Pipeline.ApproveManuallyAsync). Scheduler/PublishWorker refuse clips without it.</summary>
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }

    public List<PlatformPost> Posts { get; set; } = new();
}

/// <summary>One clip on one platform (queue entry, then the published post).</summary>
public class PlatformPost
{
    public int Id { get; set; }
    public int ClipId { get; set; }
    public Clip? Clip { get; set; }
    public Platform Platform { get; set; }
    public PostStatus Status { get; set; } = PostStatus.Queued;

    /// <summary>Slot key "yyyy-MM-dd HH:mm" (local slot time) this post was assigned to.</summary>
    public string? SlotKey { get; set; }
    public DateTime? ScheduledUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public string? Caption { get; set; }
    public string? PrivacyLevel { get; set; }
    /// <summary>TikTok publish_id or Instagram container id.</summary>
    public string? ExternalId { get; set; }
    /// <summary>TikTok video id / Instagram media id (null while unknown, e.g. TikTok SELF_ONLY).</summary>
    public string? PostId { get; set; }
    public string? PostUrl { get; set; }

    public DateTime? NextStatsPollUtc { get; set; }
    public long? LastViews { get; set; }
    public long? LastLikes { get; set; }

    public List<StatSnapshot> Stats { get; set; } = new();
}

/// <summary>Time series point for one post.</summary>
public class StatSnapshot
{
    public int Id { get; set; }
    public int PlatformPostId { get; set; }
    public PlatformPost? PlatformPost { get; set; }
    public DateTime TakenUtc { get; set; }
    public long Views { get; set; }
    public long Likes { get; set; }
    public long Comments { get; set; }
    public long Shares { get; set; }
    public long Saves { get; set; }
    public long Reach { get; set; }
}

/// <summary>OAuth connection per platform. Secrets are DPAPI-encrypted blobs.</summary>
public class AccountConnection
{
    public Platform Platform { get; set; }
    /// <summary>TikTok open_id / Instagram business account id.</summary>
    public string? AccountId { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    /// <summary>Facebook Page id (Instagram only).</summary>
    public string? PageId { get; set; }
    public byte[]? AccessTokenEnc { get; set; }
    public byte[]? RefreshTokenEnc { get; set; }
    public DateTime? AccessExpiresUtc { get; set; }
    public DateTime? RefreshExpiresUtc { get; set; }
    public string? Scopes { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public DateTime? LastRefreshUtc { get; set; }
    public DateTime? LastWarningUtc { get; set; }
}

/// <summary>App credentials (client key/secret) entered by the owner, DPAPI-encrypted.</summary>
public class AppCredential
{
    public Platform Platform { get; set; }
    public string? ClientId { get; set; }
    public byte[]? ClientSecretEnc { get; set; }
}

public class KeyValueSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum LogLevelKind { Info, Warning, Error }

public class ErrorLogEntry
{
    public int Id { get; set; }
    public DateTime Utc { get; set; }
    public LogLevelKind Level { get; set; }
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
    public int? ClipId { get; set; }
}
