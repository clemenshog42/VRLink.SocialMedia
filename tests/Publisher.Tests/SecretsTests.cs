using Microsoft.EntityFrameworkCore;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Tests;

public class SecretsTests
{
    [Fact]
    public async Task Credentials_and_tokens_are_stored_encrypted()
    {
        await using var h = new TestHost(new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc));
        var a = h.Get<AccountService>();
        await a.SaveCredentialsAsync(Platform.TikTok, "client-key", "super-secret-value");
        await a.SaveConnectionAsync(new AccountConnection
        {
            Platform = Platform.TikTok, AccountId = "open-1", AccessExpiresUtc = h.Time.Utc.AddHours(24), RefreshExpiresUtc = h.Time.Utc.AddDays(365),
        }, "access-token-value", "refresh-token-value");

        Assert.Equal("super-secret-value", (await a.GetCredentialsAsync(Platform.TikTok))!.ClientSecret);
        Assert.Equal("access-token-value", await a.GetAccessTokenAsync(Platform.TikTok));

        await using var db = h.Db();
        var cred = await db.Credentials.SingleAsync();
        var conn = await db.Accounts.SingleAsync();
        Assert.DoesNotContain("super-secret-value", System.Text.Encoding.UTF8.GetString(cred.ClientSecretEnc!));
        Assert.DoesNotContain("access-token-value", System.Text.Encoding.UTF8.GetString(conn.AccessTokenEnc!));
        // Raw DB + WAL file must not contain the plain secret or token.
        using var ms = new MemoryStream();
        foreach (var f in Directory.GetFiles(h.Dir, "test.db*"))
        {
            try
            {
                await using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await fs.CopyToAsync(ms);
            }
            catch (FileNotFoundException) { } // -wal/-shm can vanish when SQLite checkpoints on connection close
        }
        var raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("client-key", raw); // sanity: data really is in these files
        Assert.DoesNotContain("super-secret-value", raw);
        Assert.DoesNotContain("access-token-value", raw);

        // Expired access token is not handed out; refresh token warning appears 7 days before expiry.
        h.Time.Advance(TimeSpan.FromHours(25));
        Assert.Null(await a.GetAccessTokenAsync(Platform.TikTok));
        h.Time.Advance(TimeSpan.FromDays(360));
        var warnings = await h.Get<Pipeline>().TokenWarningsAsync();
        Assert.Contains(warnings, w => w.Platform == Platform.TikTok && w.Message.Contains("expires"));
    }
}
