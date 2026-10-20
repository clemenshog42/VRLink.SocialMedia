using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class MigrationTests
{
    private static ServiceProvider Build(string dir)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddPublisherCore(o => { o.DataDir = dir; o.ClipsRoot = Path.Combine(dir, "clips"); }, Path.Combine(dir, "publisher.db"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Migrations_create_db_and_restart_keeps_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkpub-tests", "mig-" + Guid.NewGuid().ToString("N"));
        try
        {
            // First start: empty folder → migrations create the schema; owner data is written.
            await using (var sp = Build(dir))
            {
                await ServiceSetup.InitDatabaseAsync(sp);
                await using var db = await sp.GetRequiredService<IDbContextFactory<PublisherDb>>().CreateDbContextAsync();
                Assert.Contains("InitialCreate", string.Join(",", await db.Database.GetAppliedMigrationsAsync()));
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                db.Clips.Add(new Clip { HighlightId = "keep-me", Type = "ace", Score = 90, VideoPath = "x.mp4", JsonPath = "x.json" });
                await db.SaveChangesAsync();
                await sp.GetRequiredService<AccountService>().SaveCredentialsAsync(Platform.TikTok, "key", "secret");
                var settings = sp.GetRequiredService<SettingsStore>();
                var s = await settings.GetAsync();
                s.ScoreThreshold = 77;
                await settings.SaveAsync(s);
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Restart: Migrate() again on the existing DB → nothing re-created, data still there.
            await using (var sp = Build(dir))
            {
                await ServiceSetup.InitDatabaseAsync(sp);
                await using var db = await sp.GetRequiredService<IDbContextFactory<PublisherDb>>().CreateDbContextAsync();
                Assert.Equal("keep-me", (await db.Clips.SingleAsync()).HighlightId);
                Assert.Equal("secret", (await sp.GetRequiredService<AccountService>().GetCredentialsAsync(Platform.TikTok))!.ClientSecret);
                Assert.Equal(77, (await sp.GetRequiredService<SettingsStore>().GetAsync()).ScoreThreshold);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Pre_migration_database_is_refused_with_clear_message()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrlinkpub-tests", "old-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var sp = Build(dir);
            await using (var db = await sp.GetRequiredService<IDbContextFactory<PublisherDb>>().CreateDbContextAsync())
                await db.Database.EnsureCreatedAsync(); // simulates the old skeleton DB
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceSetup.InitDatabaseAsync(sp));
            Assert.Contains("before EF migrations", ex.Message);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Model_has_no_unmigrated_changes()
    {
        using var db = new DesignTimeDbFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges(), "Run: dotnet tool run dotnet-ef migrations add <Name> --project src/Publisher --output-dir Data/Migrations");
    }
}
