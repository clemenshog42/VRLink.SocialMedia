using VRLink.Composer;

namespace VRLink.Capture;

/// <summary>Footage for the Composer when it runs inside the Capture service (no HTTP hop).</summary>
public sealed class InProcessFootageSource(CaptureHost host, FootageService footage, TimeProvider time) : IFootageSource
{
    public async Task GetWindowAsync(string cam, DateTime fromUtc, DateTime toUtc, string outPath, CancellationToken ct) =>
        await footage.GetWindowAsync(cam, fromUtc, toUtc, outPath, ct);

    public Task<string?> PinAsync(string? cam, DateTime fromUtc, DateTime toUtc, string reason, CancellationToken ct) =>
        Task.FromResult<string?>(host.Pins.Add(cam, fromUtc, toUtc, reason, time.GetUtcNow().UtcDateTime).Id);

    public Task ReleaseAsync(string pinId, CancellationToken ct) { host.Pins.Release(pinId); return Task.CompletedTask; }

    public async Task<(float[] Samples, DateTime StartUtc)> GetAudioAsync(string cam, DateTime fromUtc, DateTime toUtc, int rate, CancellationToken ct)
    {
        var avail = footage.Available(cam, fromUtc, toUtc) ?? throw new FootageException("no footage for the session");
        return await footage.GetAudioAsync(cam, avail.From, avail.To, rate, ct);
    }
}

public static class ComposerHosting
{
    /// <summary>Registers the Composer worker in the Capture service when "Composer:Enabled" is true (one Windows service for both).</summary>
    public static IServiceCollection AddComposer(this IServiceCollection s, IConfiguration config)
    {
        var opt = config.GetSection("Composer").Get<ComposerOptions>() ?? new ComposerOptions { Enabled = false };
        s.AddSingleton(opt);
        if (!opt.Enabled) return s;
        s.AddSingleton<IFootageSource, InProcessFootageSource>();
        s.AddSingleton<Func<HighlightRecord, ComposeJob>>(sp => _ =>
            new ComposeJob(opt, sp.GetRequiredService<IFootageSource>(),
                           new UnityLayerRenderer(opt.UnityServerExe, TimeSpan.FromMinutes(opt.LayerTimeoutMinutes)),
                           sp.GetRequiredService<ILoggerFactory>().CreateLogger("Composer")));
        s.AddSingleton<Func<ReactionJob>>(sp => () =>
            new ReactionJob(opt, sp.GetRequiredService<IFootageSource>(),
                            new UnityLayerRenderer(opt.UnityServerExe, TimeSpan.FromMinutes(opt.LayerTimeoutMinutes)),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Reactions")));
        s.AddSingleton(sp => new ComposerWorker(opt, sp.GetRequiredService<IFootageSource>(), sp.GetRequiredService<Func<HighlightRecord, ComposeJob>>(),
                                                sp.GetRequiredService<ILogger<ComposerWorker>>(), sp.GetRequiredService<TimeProvider>(),
                                                opt.ReactionsEnabled ? sp.GetRequiredService<Func<ReactionJob>>() : null));
        s.AddHostedService(sp => sp.GetRequiredService<ComposerWorker>());
        return s;
    }
}
