using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Oauth;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;
using VRLinkPublisher.Workers;

namespace VRLinkPublisher;

public static class ServiceSetup
{
    /// <summary>Everything except the web UI and the hosted workers (shared with the tests).</summary>
    public static IServiceCollection AddPublisherCore(this IServiceCollection services, Action<PublisherOptions> configure, string? dbPathOverride = null)
    {
        services.Configure(configure);
        var tmp = new PublisherOptions();
        configure(tmp);
        var dbPath = dbPathOverride ?? Path.Combine(tmp.ResolveDataDir(), "publisher.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        services.AddDbContextFactory<PublisherDb>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpClient("tiktok", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient("instagram", c => c.Timeout = TimeSpan.FromMinutes(10));

        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<EventLog>();
        services.AddSingleton<IMediaProbe, FfprobeMediaProbe>();
        services.AddSingleton<ClipImporter>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<OAuthService>();
        services.AddSingleton<IPlatformPublisher, TikTokPublisher>();
        services.AddSingleton<IPlatformPublisher, InstagramPublisher>();
        services.AddSingleton<FakePublisherFactory>();
        services.AddSingleton<PublisherResolver>();
        services.AddSingleton<Pipeline>();
        services.AddScoped<DashboardQueries>();
        return services;
    }

    public static IServiceCollection AddPublisherWorkers(this IServiceCollection services)
    {
        services.AddHostedService<ClipWatcher>();
        services.AddHostedService<ClipChecker>();
        services.AddHostedService<Scheduler>();
        services.AddHostedService<PublishWorker>();
        services.AddHostedService<StatsPoller>();
        services.AddHostedService<TokenRefresher>();
        return services;
    }

    public static async Task InitDatabaseAsync(IServiceProvider sp)
    {
        var dbf = sp.GetRequiredService<IDbContextFactory<PublisherDb>>();
        await using var db = await dbf.CreateDbContextAsync();
        // EF Core migrations (Data/Migrations): applies pending migrations, keeps existing data.
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            // A DB from the pre-migration skeleton (EnsureCreated) has tables but no history: refuse with a clear message.
            cmd.CommandText = "SELECT (SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Clips'), " +
                              "(SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory')";
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync() && r.GetInt64(0) > 0 && r.GetInt64(1) == 0)
                throw new InvalidOperationException(
                    $"Database '{conn.DataSource}' was created before EF migrations were introduced. Delete it (only demo data) and restart.");
        }
        await conn.CloseAsync();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}
