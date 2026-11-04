using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

/// <summary>Captions 2026-11-04: type groups match the game's types, placeholder venue never posted, call to action, local tags + per-platform limit, v3 upgrade.</summary>
public class CaptionGrowthTests
{
    [Theory]
    [InlineData("game", "kills4", "multikill#")]
    [InlineData("game", "kills3", "multikill#")]
    [InlineData("game", "ninjadefuse", "defuse#")]
    [InlineData("game", "lastseconddefuse", "defuse#")]
    [InlineData("game", "ace", "ace#")]
    [InlineData("irl", "kills4", "irl.*#")]
    [InlineData("irl", "ace", "irl.ace#")]
    [InlineData("montage", "weeklytop", "montage#")]
    [InlineData("game", "unknowntype", "*#")]
    public void Game_types_find_their_group_templates(string format, string type, string idPrefix)
    {
        var v = CaptionComposer.Variants(new RuntimeSettings(), format, type);
        Assert.StartsWith(idPrefix, v[0].Id);
    }

    [Fact]
    public void Placeholder_venue_never_reaches_a_post()
    {
        var s = new RuntimeSettings { CallToAction = "" };
        var clip = new Clip { Type = "ace", Caption = "Max wins", Venue = "TacticalLBE" };
        var a = CaptionComposer.ComposeWith(clip, s, "Highlight from VRLink {venue}: {caption}", 2200);
        var b = CaptionComposer.ComposeWith(clip, s, "{caption} | VRLink {venue}", 2200);
        Assert.StartsWith("Highlight from VRLink: Max wins", a);
        Assert.DoesNotContain("TacticalLBE", a + b);
        Assert.StartsWith("Max wins | VRLink", b);
        var real = CaptionComposer.ComposeWith(new Clip { Type = "ace", Caption = "x", Venue = "Linz" }, s, "VRLink {venue}: {caption}", 2200);
        Assert.StartsWith("VRLink Linz: x", real);
    }

    [Fact]
    public void Call_to_action_follows_the_hook_and_hashtags_keep_brand_and_local_first()
    {
        var s = new RuntimeSettings { LocalHashtags = "#mytown #myregion" };
        var clip = new Clip { Type = "ace", Caption = "Max wins", Hashtags = "#vr #virtualreality #fps #tacticalshooter #ace #clutch" };
        var ig = CaptionComposer.ComposeWith(clip, s, "ACE! {caption}", 2200, Platform.Instagram);
        var lines = ig.Split('\n');
        Assert.Equal("ACE! Max wins", lines[0]);
        Assert.Equal(s.CallToAction, lines[1]);
        var tags = lines[^1].Split(' ');
        Assert.Equal(5, tags.Length);
        Assert.Equal("#VRLink", tags[0]);
        Assert.Contains("#mytown", tags);
        Assert.Contains("#myregion", tags);
        var noLimit = CaptionComposer.ComposeWith(clip, s, "{caption}", 2200);
        Assert.Contains("#tacticalshooter", noLimit);
        Assert.Single(noLimit.Split(' '), t => t.Equals("#vr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Long_text_is_cut_before_the_hashtags()
    {
        var s = new RuntimeSettings { CallToAction = "" };
        var clip = new Clip { Type = "ace", Caption = new string('a', 500) };
        var t = CaptionComposer.ComposeWith(clip, s, "{caption}", 120, Platform.TikTok);
        Assert.True(t.Length <= 120);
        Assert.EndsWith("#vrgaming", t);
    }

    [Fact]
    public void Upgrade_v2_to_v3_replaces_untouched_templates_and_keeps_edited_ones()
    {
        var old = new RuntimeSettings { CaptionTemplates = SettingsUpgrade.V2CaptionTemplates.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) };
        var changes = SettingsUpgrade.Apply(old, 2);
        Assert.Equal(SettingsUpgrade.Current, old.SettingsVersion);
        Assert.True(SettingsUpgrade.SameTemplates(old.CaptionTemplates, RuntimeSettings.DefaultCaptionTemplates()));
        Assert.Contains(changes, c => c.Contains("question variants"));

        var edited = new RuntimeSettings { CaptionTemplates = new() { ["*"] = ["mine {caption}"] } };
        SettingsUpgrade.Apply(edited, 2);
        Assert.Equal("mine {caption}", edited.CaptionTemplates["*"][0]);
    }
}
