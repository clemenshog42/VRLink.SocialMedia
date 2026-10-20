using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Data;
using VRLinkPublisher.Domain;

namespace VRLinkPublisher.Services;

public record AppCredentials(string ClientId, string ClientSecret);

/// <summary>Reads/writes DPAPI-protected credentials and tokens. Plain secrets never leave this service except to API calls.</summary>
public class AccountService(IDbContextFactory<PublisherDb> dbf, ISecretProtector protector, TimeProvider time)
{
    public async Task<AppCredentials?> GetCredentialsAsync(Platform p, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var c = await db.Credentials.FindAsync([p], ct);
        var secret = protector.Unprotect(c?.ClientSecretEnc);
        return c?.ClientId is { Length: > 0 } id && secret is { Length: > 0 } ? new AppCredentials(id, secret) : null;
    }

    public async Task<string?> GetClientIdAsync(Platform p, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return (await db.Credentials.FindAsync([p], ct))?.ClientId;
    }

    /// <param name="clientSecret">null/empty = keep the stored secret.</param>
    public async Task SaveCredentialsAsync(Platform p, string clientId, string? clientSecret, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var c = await db.Credentials.FindAsync([p], ct);
        if (c == null) db.Credentials.Add(c = new AppCredential { Platform = p });
        c.ClientId = clientId.Trim();
        if (!string.IsNullOrWhiteSpace(clientSecret)) c.ClientSecretEnc = protector.Protect(clientSecret.Trim());
        await db.SaveChangesAsync(ct);
    }

    public async Task<AccountConnection?> GetConnectionAsync(Platform p, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Platform == p, ct);
    }

    public async Task<List<AccountConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Accounts.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>Plain access token if present and not expired, else null.</summary>
    public async Task<string?> GetAccessTokenAsync(Platform p, CancellationToken ct = default)
    {
        var a = await GetConnectionAsync(p, ct);
        if (a == null) return null;
        if (a.AccessExpiresUtc is { } exp && exp <= time.GetUtcNow().UtcDateTime) return null;
        return protector.Unprotect(a.AccessTokenEnc);
    }

    public async Task<string?> GetRefreshTokenAsync(Platform p, CancellationToken ct = default)
    {
        var a = await GetConnectionAsync(p, ct);
        return protector.Unprotect(a?.RefreshTokenEnc);
    }

    public async Task SaveConnectionAsync(AccountConnection update, string? accessToken, string? refreshToken, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Accounts.FindAsync([update.Platform], ct);
        if (a == null) db.Accounts.Add(a = new AccountConnection { Platform = update.Platform });
        a.AccountId = update.AccountId ?? a.AccountId;
        a.DisplayName = update.DisplayName ?? a.DisplayName;
        a.Username = update.Username ?? a.Username;
        a.PageId = update.PageId ?? a.PageId;
        a.Scopes = update.Scopes ?? a.Scopes;
        a.AccessExpiresUtc = update.AccessExpiresUtc;
        a.RefreshExpiresUtc = update.RefreshExpiresUtc ?? a.RefreshExpiresUtc;
        a.ConnectedUtc = update.ConnectedUtc ?? a.ConnectedUtc;
        a.LastRefreshUtc = update.LastRefreshUtc ?? a.LastRefreshUtc;
        if (accessToken != null) a.AccessTokenEnc = protector.Protect(accessToken);
        if (refreshToken != null) a.RefreshTokenEnc = protector.Protect(refreshToken);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateDisplayAsync(Platform p, string? displayName, string? username, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Accounts.FindAsync([p], ct);
        if (a == null) return;
        a.DisplayName = displayName ?? a.DisplayName;
        a.Username = username ?? a.Username;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkWarnedAsync(Platform p, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Accounts.FindAsync([p], ct);
        if (a == null) return;
        a.LastWarningUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task DisconnectAsync(Platform p, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Accounts.FindAsync([p], ct);
        if (a != null) { db.Accounts.Remove(a); await db.SaveChangesAsync(ct); }
    }
}
