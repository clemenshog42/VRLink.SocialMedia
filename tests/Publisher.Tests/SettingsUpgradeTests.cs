using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class SettingsUpgradeTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Writes a pre-V5 settings row (no version marker) and returns a fresh store that loads it.</summary>
    private static async Task<SettingsStore> OldInstallAsync(TestHost h, Action<RuntimeSettings> edit)
    {
        var old = new RuntimeSettings
        {
            TikTokSlots = SettingsUpgrade.V1TikTokSlots.ToList(), InstagramSlots = SettingsUpgrade.V1InstagramSlots.ToList(),
            CaptionTemplates = SettingsUpgrade.V1CaptionTemplates.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
        };
        edit(old);
        var json = JsonNode.Parse(JsonSerializer.Serialize(old))!.AsObject();
        json.Remove("SettingsVersion");   // written before the marker existed
        await using (var db = h.Db())
        {
            var row = await db.Settings.FindAsync("runtime");
            row!.Value = json.ToJsonString();
            await db.SaveChangesAsync();
        }
        return new SettingsStore(h.Get<IDbContextFactory<PublisherDb>>(), h.Get<IOptions<PublisherOptions>>(), NullLogger<SettingsStore>.Instance);
    }

    [Fact]
    public async Task Untouched_old_defaults_are_upgraded_once_and_logged()
    {
        await using var h = new TestHost(T0);
        var store = await OldInstallAsync(h, _ => { });
        var s = await store.GetAsync();
        Assert.Equal(["12:00", "19:00"], s.TikTokSlots);
        Assert.Equal(["11:30", "20:00"], s.InstagramSlots);
        Assert.True(s.CaptionTemplates.ContainsKey("irl.ace"));
        Assert.Equal(SettingsUpgrade.Current, s.SettingsVersion);
        await using var db = h.Db();
        var log = await db.Log.Where(l => l.Source == "Settings").ToListAsync();
        Assert.Single(log);
        Assert.Contains("hook-first", log[0].Message);
        // persisted with the marker → a second load does not upgrade again
        Assert.Equal(SettingsUpgrade.Current, SettingsUpgrade.StoredVersion((await db.Settings.FindAsync("runtime"))!.Value));
        var again = new SettingsStore(h.Get<IDbContextFactory<PublisherDb>>(), h.Get<IOptions<PublisherOptions>>(), NullLogger<SettingsStore>.Instance);
        await again.GetAsync();
        Assert.Single(await db.Log.Where(l => l.Source == "Settings").ToListAsync());
    }

    [Fact]
    public async Task Edited_values_are_kept()
    {
        await using var h = new TestHost(T0);
        var store = await OldInstallAsync(h, s =>
        {
            s.TikTokSlots = ["18:15"];
            s.CaptionTemplates["ace"] = ["My own ace caption {caption}"];
        });
        var s = await store.GetAsync();
        Assert.Equal(["18:15"], s.TikTokSlots);                         // edited → kept
        Assert.Equal(["11:30", "20:00"], s.InstagramSlots);             // untouched → upgraded
        Assert.Equal(["My own ace caption {caption}"], s.CaptionTemplates["ace"]);
        Assert.False(s.CaptionTemplates.ContainsKey("irl.ace"));
        await using var db = h.Db();
        var msg = (await db.Log.SingleAsync(l => l.Source == "Settings")).Message;
        Assert.Contains("TikTok slots kept", msg);
        Assert.Contains("caption templates kept", msg);
    }

    [Fact]
    public async Task New_installs_start_at_the_current_version_with_new_defaults()
    {
        await using var h = new TestHost(T0);
        var s = await h.Get<SettingsStore>().GetAsync();
        Assert.Equal(SettingsUpgrade.Current, s.SettingsVersion);
        Assert.Equal(["12:00", "19:00"], s.TikTokSlots);
        await using var db = h.Db();
        Assert.Empty(await db.Log.Where(l => l.Source == "Settings").ToListAsync());
    }

    [Fact]
    public void Reset_per_section_restores_only_that_section()
    {
        var s = new RuntimeSettings { TikTokSlots = ["01:00"], ScoreThreshold = 99, DefaultHashtags = "#x", MaxAttempts = 9 };
        s.CaptionTemplates = new() { ["*"] = ["x"] };
        SettingsUpgrade.Reset(s, "schedule");
        Assert.Equal(["12:00", "19:00"], s.TikTokSlots);
        Assert.Equal(99, s.ScoreThreshold);
        SettingsUpgrade.Reset(s, "checks");
        Assert.Equal(60, s.ScoreThreshold);
        Assert.Equal("#x", s.DefaultHashtags);
        SettingsUpgrade.Reset(s, "captions");
        Assert.True(s.CaptionTemplates.ContainsKey("reaction.*"));
        Assert.Equal(9, s.MaxAttempts);
        SettingsUpgrade.Reset(s, "stats");
        Assert.Equal(5, s.MaxAttempts);
        Assert.Throws<ArgumentException>(() => SettingsUpgrade.Reset(s, "nope"));
    }
}
