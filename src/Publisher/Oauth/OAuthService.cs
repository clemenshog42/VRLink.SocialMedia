using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VRLinkPublisher.Domain;
using VRLinkPublisher.Publishers;
using VRLinkPublisher.Services;

namespace VRLinkPublisher.Oauth;

/// <summary>
/// OAuth connect flows. The owner clicks "Connect" in the web UI and logs in himself; we never see passwords.
/// TikTok: Login Kit v2 for Desktop with PKCE (code_challenge = HEX(SHA256(verifier)), S256), localhost redirect with port allowed.
///   https://developers.tiktok.com/doc/login-kit-desktop , https://developers.tiktok.com/doc/oauth-user-access-token-management
/// Instagram: Facebook Login (manual flow) → long-lived user token (fb_exchange_token, ~60 days) → /me/accounts Page token (no set expiry)
///   https://developers.facebook.com/docs/facebook-login/guides/advanced/manual-flow
///   https://developers.facebook.com/docs/facebook-login/guides/access-tokens/get-long-lived
/// YouTube: Google OAuth 2.0 for web server apps, authorization code (+ PKCE S256) with access_type=offline and prompt=consent -> refresh token;
///   access tokens live ~1 h and are renewed with grant_type=refresh_token. Docs checked 2026-11-04:
///   https://developers.google.com/youtube/v3/guides/auth/server-side-web-apps , https://developers.google.com/identity/protocols/oauth2
/// </summary>
public class OAuthService(
    IHttpClientFactory http, AccountService accounts, IMemoryCache cache, IOptions<PublisherOptions> options,
    TimeProvider time, EventLog log, IServiceProvider sp)
{
    public const string TikTokScopes = "user.info.basic,video.upload,video.publish,video.list";
    public const string FacebookScopes = "instagram_basic,instagram_content_publish,instagram_manage_insights,pages_show_list,pages_read_engagement"; // VERIFY: exact set in App Review

    public const string YouTubeUploadScope = "https://www.googleapis.com/auth/youtube.upload";
    public const string YouTubeReadonlyScope = "https://www.googleapis.com/auth/youtube.readonly";
    /// <summary>upload = videos.insert; readonly = videos.list statistics (also for private videos) and channels.list (channel name).</summary>
    public const string YouTubeScopes = YouTubeUploadScope + " " + YouTubeReadonlyScope;
    public const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";

    private record PendingAuth(Platform Platform, string Verifier, string RedirectUri);

    public static string TikTokRedirectUri(string baseUrl) => baseUrl.TrimEnd('/') + "/oauth/tiktok/callback/";
    public static string FacebookRedirectUri(string baseUrl) => baseUrl.TrimEnd('/') + "/oauth/instagram/callback/";
    public static string YouTubeRedirectUri(string baseUrl) => baseUrl.TrimEnd('/') + "/oauth/youtube/callback/";

    private string Ver => options.Value.GraphApiVersion;
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static string NewVerifier() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); // 64 unreserved chars

    /// <summary>TikTok desktop PKCE uses hex encoding of SHA256 (not base64url) per Login Kit desktop docs.</summary>
    public static string TikTokChallenge(string verifier) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).ToLowerInvariant();

    /// <summary>RFC 7636 S256: BASE64URL(SHA256(verifier)) (Google; TikTok desktop uses hex instead).</summary>
    public static string S256Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string NewState(Platform p, string verifier, string redirect)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set("oauth:" + state, new PendingAuth(p, verifier, redirect), TimeSpan.FromMinutes(15));
        return state;
    }

    private PendingAuth? TakeState(string? state)
    {
        if (string.IsNullOrEmpty(state) || !cache.TryGetValue("oauth:" + state, out PendingAuth? p)) return null;
        cache.Remove("oauth:" + state);
        return p;
    }

    // ---------------- TikTok ----------------

    public async Task<string> TikTokAuthorizeUrlAsync(string baseUrl)
    {
        var clientKey = await accounts.GetClientIdAsync(Platform.TikTok) ?? throw new InvalidOperationException("Enter the TikTok client key first.");
        var redirect = TikTokRedirectUri(baseUrl);
        var verifier = NewVerifier();
        var state = NewState(Platform.TikTok, verifier, redirect);
        return "https://www.tiktok.com/v2/auth/authorize/?" + Query(
            ("client_key", clientKey), ("response_type", "code"), ("scope", TikTokScopes), ("redirect_uri", redirect),
            ("state", state), ("code_challenge", TikTokChallenge(verifier)), ("code_challenge_method", "S256"));
    }

    public async Task<string> TikTokCallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var pending = TakeState(state);
        if (pending == null || pending.Platform != Platform.TikTok) return "Invalid or expired state. Please click Connect again.";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return "TikTok login cancelled: " + error;
        var cred = await accounts.GetCredentialsAsync(Platform.TikTok, ct) ?? throw new InvalidOperationException("TikTok credentials missing");

        var resp = await http.CreateClient("tiktok").PostAsync("https://open.tiktokapis.com/v2/oauth/token/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_key"] = cred.ClientId, ["client_secret"] = cred.ClientSecret, ["code"] = code,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = pending.RedirectUri, ["code_verifier"] = pending.Verifier,
        }), ct);
        var n = await ApiJson.ReadAsync(resp, "TikTok token", ct);
        await SaveTikTokTokenAsync(n, connected: true, ct);

        // Fill creator nickname/username for the UI (UX guideline: show the creator).
        try
        {
            var tt = sp.GetServices<IPlatformPublisher>().OfType<TikTokPublisher>().First();
            var info = await tt.QueryCreatorInfoAsync(ct);
            await accounts.UpdateDisplayAsync(Platform.TikTok, info.Nickname, info.Username, ct);
        }
        catch (Exception ex) { await log.WarnAsync("OAuth", "TikTok connected but creator_info failed", ex.Message); }
        await log.InfoAsync("OAuth", "TikTok connected");
        return "ok";
    }

    private async Task SaveTikTokTokenAsync(JsonNode n, bool connected, CancellationToken ct)
    {
        if (n["access_token"] == null)
            throw new PlatformApiException("TikTok token response without access_token: " + (n["error_description"] ?? n["error"] ?? n.ToJsonString()), false);
        var access = n["access_token"]!.GetValue<string>();
        var refresh = n["refresh_token"]?.GetValue<string>();
        await accounts.SaveConnectionAsync(new AccountConnection
        {
            Platform = Platform.TikTok,
            AccountId = n["open_id"]?.GetValue<string>(),
            Scopes = n["scope"]?.GetValue<string>(),
            AccessExpiresUtc = Now.AddSeconds(ApiJson.Long(n["expires_in"]) is > 0 and var e ? e : 86400),
            RefreshExpiresUtc = ApiJson.Long(n["refresh_expires_in"]) is > 0 and var r ? Now.AddSeconds(r) : null,
            ConnectedUtc = connected ? Now : null,
            LastRefreshUtc = Now,
        }, access, refresh, ct); // a new refresh_token may be returned on refresh: always store it
    }

    public async Task RefreshTikTokAsync(CancellationToken ct)
    {
        var cred = await accounts.GetCredentialsAsync(Platform.TikTok, ct) ?? throw new InvalidOperationException("TikTok credentials missing");
        var refresh = await accounts.GetRefreshTokenAsync(Platform.TikTok, ct) ?? throw new InvalidOperationException("No TikTok refresh token; reconnect");
        var resp = await http.CreateClient("tiktok").PostAsync("https://open.tiktokapis.com/v2/oauth/token/", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_key"] = cred.ClientId, ["client_secret"] = cred.ClientSecret, ["grant_type"] = "refresh_token", ["refresh_token"] = refresh,
        }), ct);
        await SaveTikTokTokenAsync(await ApiJson.ReadAsync(resp, "TikTok refresh", ct), connected: false, ct);
    }

    // ---------------- Instagram (Facebook Login) ----------------

    public async Task<string> FacebookAuthorizeUrlAsync(string baseUrl)
    {
        var appId = await accounts.GetClientIdAsync(Platform.Instagram) ?? throw new InvalidOperationException("Enter the Meta app id first.");
        var redirect = FacebookRedirectUri(baseUrl);
        var state = NewState(Platform.Instagram, "", redirect);
        return $"https://www.facebook.com/{Ver}/dialog/oauth?" + Query(
            ("client_id", appId), ("redirect_uri", redirect), ("state", state), ("response_type", "code"), ("scope", FacebookScopes));
    }

    public async Task<string> FacebookCallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var pending = TakeState(state);
        if (pending == null || pending.Platform != Platform.Instagram) return "Invalid or expired state. Please click Connect again.";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return "Facebook login cancelled: " + error;
        var cred = await accounts.GetCredentialsAsync(Platform.Instagram, ct) ?? throw new InvalidOperationException("Meta app credentials missing");
        var c = http.CreateClient("instagram");
        var g = $"https://graph.facebook.com/{Ver}";

        var shortTok = await ApiJson.ReadAsync(await c.GetAsync($"{g}/oauth/access_token?" + Query(
            ("client_id", cred.ClientId), ("redirect_uri", pending.RedirectUri), ("client_secret", cred.ClientSecret), ("code", code)), ct), "FB code exchange", ct);
        var longTok = await ApiJson.ReadAsync(await c.GetAsync($"{g}/oauth/access_token?" + Query(
            ("grant_type", "fb_exchange_token"), ("client_id", cred.ClientId), ("client_secret", cred.ClientSecret),
            ("fb_exchange_token", shortTok["access_token"]!.GetValue<string>())), ct), "FB long-lived exchange", ct);
        var userToken = longTok["access_token"]!.GetValue<string>();
        var userExp = ApiJson.Long(longTok["expires_in"]);

        var pages = await ApiJson.ReadAsync(await c.GetAsync($"{g}/me/accounts?" + Query(
            ("fields", "id,name,access_token,instagram_business_account{id,username}"), ("access_token", userToken)), ct), "FB /me/accounts", ct);
        var page = pages["data"]?.AsArray().FirstOrDefault(p => p?["instagram_business_account"]?["id"] != null);
        if (page == null) return "No Facebook Page with a linked Instagram Professional account was found for this login.";

        await accounts.SaveConnectionAsync(new AccountConnection
        {
            Platform = Platform.Instagram,
            AccountId = page["instagram_business_account"]!["id"]!.ToString(),
            Username = page["instagram_business_account"]!["username"]?.GetValue<string>(),
            DisplayName = page["name"]?.GetValue<string>(),
            PageId = page["id"]!.ToString(),
            Scopes = FacebookScopes,
            AccessExpiresUtc = null,  // Page token from a long-lived user token: no set expiry (checked via debug_token)
            RefreshExpiresUtc = userExp > 0 ? Now.AddSeconds(userExp) : Now.AddDays(60), // long-lived user token (~60 d)
            ConnectedUtc = Now,
            LastRefreshUtc = Now,
        }, page["access_token"]!.GetValue<string>(), userToken, ct);
        await log.InfoAsync("OAuth", "Instagram connected via Facebook Page " + page["name"]);
        return "ok";
    }

    /// <summary>Checks the Page token with debug_token; returns (valid, expiresUtc or null = never).</summary>
    public async Task<(bool Valid, DateTime? ExpiresUtc)> CheckInstagramTokenAsync(CancellationToken ct)
    {
        var cred = await accounts.GetCredentialsAsync(Platform.Instagram, ct) ?? throw new InvalidOperationException("Meta app credentials missing");
        var token = await accounts.GetAccessTokenAsync(Platform.Instagram, ct) ?? throw new InvalidOperationException("Instagram not connected");
        // VERIFY: debug_token with app access token "{app-id}|{app-secret}"; data.expires_at = 0 means never.
        var n = await ApiJson.ReadAsync(await http.CreateClient("instagram").GetAsync($"https://graph.facebook.com/{Ver}/debug_token?" + Query(
            ("input_token", token), ("access_token", cred.ClientId + "|" + cred.ClientSecret)), ct), "FB debug_token", ct);
        var valid = n["data"]?["is_valid"]?.GetValue<bool>() ?? false;
        var exp = ApiJson.Long(n["data"]?["expires_at"]);
        return (valid, exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null);
    }

    // ---------------- YouTube (Google OAuth) ----------------

    public async Task<string> YouTubeAuthorizeUrlAsync(string baseUrl)
    {
        var clientId = await accounts.GetClientIdAsync(Platform.YouTube) ?? throw new InvalidOperationException("Enter the Google OAuth client id first.");
        var redirect = YouTubeRedirectUri(baseUrl);
        var verifier = NewVerifier();
        var state = NewState(Platform.YouTube, verifier, redirect);
        // access_type=offline + prompt=consent: Google returns a refresh token on every consent (it is otherwise only sent the first time).
        return GoogleAuthUrl + "?" + Query(
            ("client_id", clientId), ("redirect_uri", redirect), ("response_type", "code"), ("scope", YouTubeScopes),
            ("access_type", "offline"), ("prompt", "consent"), ("state", state),
            ("code_challenge", S256Challenge(verifier)), ("code_challenge_method", "S256"));
    }

    public async Task<string> YouTubeCallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        var pending = TakeState(state);
        if (pending == null || pending.Platform != Platform.YouTube) return "Invalid or expired state. Please click Connect again.";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return "Google login cancelled: " + error;
        var cred = await accounts.GetCredentialsAsync(Platform.YouTube, ct) ?? throw new InvalidOperationException("YouTube (Google) credentials missing");

        var resp = await http.CreateClient("youtube").PostAsync(GoogleTokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = cred.ClientId, ["client_secret"] = cred.ClientSecret, ["code"] = code,
            ["grant_type"] = "authorization_code", ["redirect_uri"] = pending.RedirectUri, ["code_verifier"] = pending.Verifier,
        }), ct);
        var n = await ApiJson.ReadAsync(resp, "Google token", ct);
        var scopes = (n["scope"]?.GetValue<string>() ?? "").Split(' ');
        if (!scopes.Contains(YouTubeUploadScope))
            return "Google did not grant the YouTube upload permission (granular consent: tick all boxes). Click Connect again.";
        if (n["refresh_token"] == null)
            return "Google returned no refresh token. Remove the app at myaccount.google.com/permissions and click Connect again.";
        if (!scopes.Contains(YouTubeReadonlyScope))
            await log.WarnAsync("OAuth", "YouTube connected without the read-only permission: no statistics will be available");
        await SaveYouTubeTokenAsync(n, connected: true, ct);

        // Channel name for the UI (needs youtube.readonly); best effort.
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", n["access_token"]!.GetValue<string>());
            var ch = await ApiJson.ReadAsync(await http.CreateClient("youtube").SendAsync(req, ct), "YouTube channels.list", ct);
            var item = ch["items"]?.AsArray().FirstOrDefault();
            if (item != null)
                await accounts.SaveConnectionAsync(new AccountConnection
                {
                    Platform = Platform.YouTube, AccountId = item["id"]?.GetValue<string>(),
                    DisplayName = item["snippet"]?["title"]?.GetValue<string>(), Username = item["snippet"]?["customUrl"]?.GetValue<string>(),
                    AccessExpiresUtc = Now.AddSeconds(ApiJson.Long(n["expires_in"]) is > 0 and var e ? e : 3600),
                }, null, null, ct);
        }
        catch (Exception ex) when (ex is PlatformApiException or HttpRequestException) { await log.WarnAsync("OAuth", "YouTube connected but channels.list failed", ex.Message); }
        await log.InfoAsync("OAuth", "YouTube connected");
        return "ok";
    }

    private async Task SaveYouTubeTokenAsync(JsonNode n, bool connected, CancellationToken ct)
    {
        if (n["access_token"] == null)
            throw new PlatformApiException("Google token response without access_token: " + (n["error_description"] ?? n["error"] ?? n.ToJsonString()), false);
        await accounts.SaveConnectionAsync(new AccountConnection
        {
            Platform = Platform.YouTube,
            Scopes = n["scope"]?.GetValue<string>(),
            AccessExpiresUtc = Now.AddSeconds(ApiJson.Long(n["expires_in"]) is > 0 and var e ? e : 3600),
            RefreshExpiresUtc = ApiJson.Long(n["refresh_token_expires_in"]) is > 0 and var r ? Now.AddSeconds(r) : null,
            ConnectedUtc = connected ? Now : null,
            LastRefreshUtc = Now,
        }, n["access_token"]!.GetValue<string>(), n["refresh_token"]?.GetValue<string>(), ct); // refresh responses carry no new refresh_token: the stored one is kept
    }

    /// <summary>grant_type=refresh_token. Throws a PlatformApiException (non-retryable for invalid_grant = login revoked/expired).</summary>
    public async Task RefreshYouTubeAsync(CancellationToken ct)
    {
        var cred = await accounts.GetCredentialsAsync(Platform.YouTube, ct) ?? throw new PlatformApiException("YouTube credentials missing", false);
        var refresh = await accounts.GetRefreshTokenAsync(Platform.YouTube, ct) ?? throw new PlatformApiException("No YouTube refresh token; click Connect YouTube again", false);
        var resp = await http.CreateClient("youtube").PostAsync(GoogleTokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = cred.ClientId, ["client_secret"] = cred.ClientSecret, ["grant_type"] = "refresh_token", ["refresh_token"] = refresh,
        }), ct);
        try { await SaveYouTubeTokenAsync(await ApiJson.ReadAsync(resp, "Google refresh", ct), connected: false, ct); }
        catch (PlatformApiException ex) when (ex.Message.Contains("invalid_grant"))
        {
            throw new PlatformApiException("Google refused the saved YouTube login (invalid_grant). A Google Cloud app in status \"Testing\" only keeps the login for 7 days: " +
                                           "click Connect YouTube again (or set the OAuth consent screen to \"In production\"). " + ex.Message, false, ex.HttpStatus);
        }
    }

    private static string Query(params (string K, string V)[] kv) =>
        string.Join('&', kv.Select(x => Uri.EscapeDataString(x.K) + "=" + Uri.EscapeDataString(x.V)));
}
