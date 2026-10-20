using VRLinkPublisher.Domain;
using VRLinkPublisher.Oauth;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class SchedulingTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
    private static readonly List<TimeOnly> Slots = SlotPlanner.ParseSlots(new[] { "19:00", "12:30", "bad", "12:30" });

    private static DateTime Utc(int y, int mo, int d, int h, int mi) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), Tz);

    [Fact] public void Parses_and_sorts_slots() => Assert.Equal(new[] { new TimeOnly(12, 30), new TimeOnly(19, 0) }, Slots);

    [Fact]
    public void Due_slot_within_grace()
    {
        var s = SlotPlanner.DueSlot(Utc(2026, 10, 19, 12, 45), Slots, Tz, TimeSpan.FromMinutes(60));
        Assert.NotNull(s);
        Assert.Equal("2026-10-19 12:30", s!.Key);
        Assert.Equal(Utc(2026, 10, 19, 12, 30), s.Utc);
    }

    [Fact]
    public void No_slot_before_time_or_after_grace()
    {
        Assert.Null(SlotPlanner.DueSlot(Utc(2026, 10, 19, 12, 29), Slots, Tz, TimeSpan.FromMinutes(60)));
        Assert.Null(SlotPlanner.DueSlot(Utc(2026, 10, 19, 13, 31), Slots, Tz, TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void Late_evening_slot_carries_over_midnight()
    {
        var slots = SlotPlanner.ParseSlots(new[] { "23:30" });
        var s = SlotPlanner.DueSlot(Utc(2026, 10, 20, 0, 10), slots, Tz, TimeSpan.FromMinutes(60));
        Assert.Equal("2026-10-19 23:30", s!.Key);
    }

    [Fact]
    public void Upcoming_lists_next_slots_in_order()
    {
        var up = SlotPlanner.Upcoming(Utc(2026, 10, 19, 13, 0), Slots, Tz, 3);
        Assert.Equal(new[] { "2026-10-19 19:00", "2026-10-20 12:30", "2026-10-20 19:00" }, up.Select(x => x.Key));
    }

    [Fact]
    public void Picker_prefers_best_score_of_other_type()
    {
        var c = new[]
        {
            new QueueCandidate(1, "ace", 95, DateTime.UtcNow),
            new QueueCandidate(2, "clutch", 70, DateTime.UtcNow),
            new QueueCandidate(3, "clutch", 80, DateTime.UtcNow),
        };
        Assert.Equal(1, ClipPicker.Pick(c, null, true)!.PostId);
        Assert.Equal(3, ClipPicker.Pick(c, "ace", true)!.PostId);
        Assert.Equal(1, ClipPicker.Pick(c, "clutch", true)!.PostId);
    }

    [Fact]
    public void Picker_strict_skips_when_only_same_type()
    {
        var c = new[] { new QueueCandidate(1, "ace", 95, DateTime.UtcNow) };
        Assert.Null(ClipPicker.Pick(c, "ace", strictAlternation: true));
        Assert.Equal(1, ClipPicker.Pick(c, "ace", strictAlternation: false)!.PostId);
        Assert.Null(ClipPicker.Pick(Array.Empty<QueueCandidate>(), null, true));
    }

    [Fact]
    public void Captions_rotate_per_type_and_append_hashtags()
    {
        var s = new RuntimeSettings();
        var clip = new Clip { Type = "ace", Caption = "Max wins", Venue = "Home", Round = 7, Hashtags = "#clutch" };
        var a = CaptionComposer.Compose(clip, s, 0, 2200);
        var b = CaptionComposer.Compose(clip, s, 1, 2200);
        var a2 = CaptionComposer.Compose(clip, s, s.CaptionTemplates["ace"].Count, 2200);
        Assert.NotEqual(a, b);
        Assert.Equal(a, a2);
        Assert.Contains("#VRLink", a);
        Assert.Contains("#clutch", a);
        Assert.StartsWith("ACE! Max wins", a);
        Assert.True(CaptionComposer.Compose(clip, s, 0, 10).Length <= 10);
    }

    [Fact]
    public void Unknown_type_uses_fallback_templates()
    {
        var t = CaptionComposer.Compose(new Clip { Type = "ninja", Caption = "x" }, new RuntimeSettings(), 0, 2200);
        Assert.StartsWith("x", t);
    }

    [Fact]
    public void Stats_poll_interval_3h_for_14_days_then_daily()
    {
        var s = new RuntimeSettings();
        var pub = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(pub.AddDays(2).AddHours(3), Pipeline.NextPoll(pub, pub.AddDays(2), s));
        Assert.Equal(pub.AddDays(16), Pipeline.NextPoll(pub, pub.AddDays(15), s));
    }

    [Fact]
    public void TikTok_chunk_plan_follows_media_transfer_guide()
    {
        Assert.Equal((3_000_000L, 1), TikTokPublisher.PlanChunks(3_000_000));                // < 5 MB: single chunk = file size
        Assert.Equal((10_000_000L, 5), TikTokPublisher.PlanChunks(50_000_123, 10_000_000)); // docs example: 5 chunks, last absorbs remainder
        var (chunk, count) = TikTokPublisher.PlanChunks(12L * 1024 * 1024);
        Assert.Equal(1, count); // floor(12/10) = 1: the single chunk carries the whole file (last chunk may be up to 128 MB)
        Assert.Equal(TikTokPublisher.DefaultChunk, chunk);
    }

    [Fact]
    public void TikTok_privacy_forced_private_until_audited()
    {
        var s = new RuntimeSettings { TikTokPrivacyLevel = "PUBLIC_TO_EVERYONE" };
        Assert.Equal("SELF_ONLY", TikTokPublisher.ChoosePrivacy(s, new[] { "PUBLIC_TO_EVERYONE", "SELF_ONLY" }));
        s.TikTokAppAudited = true;
        Assert.Equal("PUBLIC_TO_EVERYONE", TikTokPublisher.ChoosePrivacy(s, new[] { "PUBLIC_TO_EVERYONE", "SELF_ONLY" }));
        Assert.Throws<PlatformApiException>(() => TikTokPublisher.ChoosePrivacy(s, new[] { "FOLLOWER_OF_CREATOR", "SELF_ONLY" }));
        s.TikTokPrivacyLevel = "";
        Assert.Throws<PlatformApiException>(() => TikTokPublisher.ChoosePrivacy(s, new[] { "SELF_ONLY" }));
    }

    [Fact]
    public void TikTok_pkce_challenge_is_hex_sha256()
    {
        var v = OAuthService.NewVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", v);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", OAuthService.TikTokChallenge("abc"));
    }
}
