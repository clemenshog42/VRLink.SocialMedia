using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

/// <summary>The checker never approves: a valid clip only becomes AwaitingApproval (owner must click APPROVE).</summary>
public enum ApprovalOutcome { AwaitingApproval, Reject }

public record ApprovalDecision(ApprovalOutcome Outcome, string Reason);

/// <summary>Pure rules of the ClipChecker (unit tested).</summary>
public static class ApprovalRules
{
    /// <param name="probe">Measured file properties; null = probe unavailable/skipped, the clip.json values are trusted.</param>
    /// <param name="duplicateOf">HighlightId of an existing clip with the same content hash, if any.</param>
    public static ApprovalDecision Evaluate(Clip clip, ProbeResult? probe, RuntimeSettings s, string? duplicateOf)
    {
        if (duplicateOf != null)
            return new(ApprovalOutcome.Reject, $"duplicate of {duplicateOf}");
        if (clip.HeroIsBot && !s.AllowBotHighlights)
            return new(ApprovalOutcome.Reject, "hero is a bot (Settings: allow bot highlights is off)");
        if (clip.Score < s.ScoreThreshold)
            return new(ApprovalOutcome.Reject, $"score {clip.Score:0.#} < threshold {s.ScoreThreshold:0.#}");

        var dur = probe?.DurationSec ?? clip.DurationSec;
        var w = probe?.Width ?? clip.Width;
        var h = probe?.Height ?? clip.Height;

        if (dur < s.MinDurationSec || dur > s.MaxDurationSec)
            return new(ApprovalOutcome.Reject, $"duration {dur:0.#}s outside {s.MinDurationSec:0.#}-{s.MaxDurationSec:0.#}s");
        if (w != s.RequiredWidth || h != s.RequiredHeight)
            return new(ApprovalOutcome.Reject, $"resolution {w}x{h} != {s.RequiredWidth}x{s.RequiredHeight}");
        if (clip.FileSizeBytes <= 0)
            return new(ApprovalOutcome.Reject, "empty video file");
        if (clip.FileSizeBytes > s.MaxFileSizeMb * 1024 * 1024)
            return new(ApprovalOutcome.Reject, $"file {clip.FileSizeBytes / 1048576.0:0.#} MB > {s.MaxFileSizeMb:0.#} MB");
        if (clip.FileSizeBytes < s.MinFileSizeKb * 1024)
            return new(ApprovalOutcome.Reject, $"file {clip.FileSizeBytes / 1024.0:0.#} KB < {s.MinFileSizeKb:0.#} KB");

        var src = probe == null ? "json" : "ffprobe";
        return new(ApprovalOutcome.AwaitingApproval, $"checks ok ({src}), awaiting owner approval");
    }
}
