using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class ApprovalRulesTests
{
    private static Clip Good() => new() { HighlightId = "h1", Type = "ace", Score = 80, DurationSec = 20, Width = 1080, Height = 1920, FileSizeBytes = 10_000_000 };
    private static readonly RuntimeSettings S = new() { ScoreThreshold = 60 };

    [Fact] public void Good_clip_only_awaits_approval() => Assert.Equal(ApprovalOutcome.AwaitingApproval, ApprovalRules.Evaluate(Good(), null, S, null).Outcome);

    [Fact]
    public void Rejects_low_score()
    {
        var c = Good(); c.Score = 59.9;
        var d = ApprovalRules.Evaluate(c, null, S, null);
        Assert.Equal(ApprovalOutcome.Reject, d.Outcome);
        Assert.Contains("score", d.Reason);
    }

    [Theory]
    [InlineData(4.9)]
    [InlineData(60.1)]
    public void Rejects_duration_outside_5_to_60(double dur)
    {
        var c = Good(); c.DurationSec = dur;
        Assert.Equal(ApprovalOutcome.Reject, ApprovalRules.Evaluate(c, null, S, null).Outcome);
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(60.0)]
    public void Accepts_duration_bounds(double dur)
    {
        var c = Good(); c.DurationSec = dur;
        Assert.Equal(ApprovalOutcome.AwaitingApproval, ApprovalRules.Evaluate(c, null, S, null).Outcome);
    }

    [Fact]
    public void Rejects_wrong_resolution()
    {
        var c = Good(); c.Width = 1920; c.Height = 1080;
        Assert.Contains("resolution", ApprovalRules.Evaluate(c, null, S, null).Reason);
    }

    [Fact]
    public void Probe_overrides_json_values()
    {
        var d = ApprovalRules.Evaluate(Good(), new ProbeResult(70, 1080, 1920), S, null);
        Assert.Equal(ApprovalOutcome.Reject, d.Outcome);
        Assert.Contains("duration", d.Reason);
    }

    [Fact]
    public void Rejects_too_large_and_empty_files()
    {
        var c = Good(); c.FileSizeBytes = 300L * 1024 * 1024;
        Assert.Equal(ApprovalOutcome.Reject, ApprovalRules.Evaluate(c, null, S, null).Outcome);
        c.FileSizeBytes = 0;
        Assert.Equal(ApprovalOutcome.Reject, ApprovalRules.Evaluate(c, null, S, null).Outcome);
    }

    [Fact]
    public void Rejects_duplicate()
    {
        var d = ApprovalRules.Evaluate(Good(), null, S, "other-id");
        Assert.Equal(ApprovalOutcome.Reject, d.Outcome);
        Assert.Contains("duplicate", d.Reason);
    }

}
