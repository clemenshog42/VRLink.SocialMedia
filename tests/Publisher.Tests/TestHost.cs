using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public sealed class ManualTime(DateTime utc) : TimeProvider
{
    public DateTime Utc { get; set; } = utc;
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
    public void Advance(TimeSpan t) => Utc += t;
}

/// <summary>Real DI graph (AddPublisherCore) with a temp SQLite file, temp ClipsRoot and a manual clock.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "vrlinkpub-tests", Guid.NewGuid().ToString("N"));
    public string ClipsRoot => Path.Combine(Dir, "clips");
    public ManualTime Time { get; }
    public ServiceProvider Sp { get; }

    public TestHost(DateTime utcNow, Action<IServiceCollection>? extra = null)
    {
        Directory.CreateDirectory(ClipsRoot);
        Time = new ManualTime(utcNow);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddPublisherCore(o => { o.ClipsRoot = ClipsRoot; o.DataDir = Dir; o.DryRunOnFirstStart = true; }, Path.Combine(Dir, "test.db"));
        services.AddSingleton<TimeProvider>(Time);
        extra?.Invoke(services);
        Sp = services.BuildServiceProvider();
        ServiceSetup.InitDatabaseAsync(Sp).GetAwaiter().GetResult();
        foreach (var p in Enum.GetValues<Platform>()) Sp.GetRequiredService<FakePublisherFactory>().Get(p).SimulatedDelay = TimeSpan.Zero;
    }

    public T Get<T>() where T : notnull => Sp.GetRequiredService<T>();
    public PublisherDb Db() => Get<IDbContextFactory<PublisherDb>>().CreateDbContext();

    public async Task<RuntimeSettings> SettingsAsync(Action<RuntimeSettings> edit)
    {
        var store = Get<SettingsStore>();
        var s = await store.GetAsync();
        edit(s);
        await store.SaveAsync(s);
        return s;
    }

    /// <summary>Writes a contract-conform placeholder clip (mp4 bytes are not a real video; DryRun trusts the json).</summary>
    public string WriteClip(string id, string type, double score, int width = 1080, int height = 1920, double dur = 20,
                            bool writeJson = true, bool writeMp4 = true, byte[]? mp4Bytes = null, string day = "2026-10-19",
                            bool? heroIsBot = null)
    {
        var d = Path.Combine(ClipsRoot, day);
        Directory.CreateDirectory(d);
        if (writeMp4) File.WriteAllBytes(Path.Combine(d, id + ".mp4"), mp4Bytes ?? System.Text.Encoding.UTF8.GetBytes("fake-mp4-" + id));
        File.WriteAllBytes(Path.Combine(d, id + ".jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        if (writeJson)
        {
            var json = new Dictionary<string, object?>
            {
                ["highlightId"] = id, ["type"] = type, ["score"] = score, ["matchId"] = "m1", ["round"] = 7, ["durationSec"] = dur,
                ["width"] = width, ["height"] = height, ["caption"] = $"{type} by Player", ["hashtags"] = new[] { "vrlink", "#clutch" },
                ["venue"] = "Home", ["createdUtc"] = "2026-10-19T10:00:00Z",
            };
            if (heroIsBot != null) json["heroIsBot"] = heroIsBot;
            File.WriteAllText(Path.Combine(d, id + ".json"), JsonSerializer.Serialize(json));
        }
        return d;
    }

    /// <summary>Simulates the owner clicking APPROVE on every clip that awaits approval.</summary>
    public async Task<int> OwnerApprovesAllAwaitingAsync()
    {
        await using var db = Db();
        var ids = await db.Clips.Where(c => c.Status == ClipStatus.AwaitingApproval).Select(c => c.Id).ToListAsync();
        return await Get<Pipeline>().ApproveSelectedAsync(ids, Pipeline.Owner);
    }

    public async ValueTask DisposeAsync()
    {
        await Sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { }
    }
}
