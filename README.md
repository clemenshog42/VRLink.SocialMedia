# VRLink.SocialMedia

Social media tooling for VRLink (separate from the Unity game). Projects: `src/Publisher` (below); planned: `src/Capture`, `src/Composer`, `src/Shared`.

## VRLink Publisher

Local Windows app that picks up rendered VRLink highlight clips, checks them, lets the owner review and
**approve every video by hand**, posts approved clips to **TikTok** and **Instagram Reels** on a daily schedule, and shows post statistics in a
small web UI at **http://localhost:5080**. It is separate from the Unity project: the only coupling
is the clip folder contract below.

- .NET 10, ASP.NET Core + Blazor Server (interactive server components), EF Core + SQLite
- Runs as a console app or as a Windows service (`UseWindowsService`)
- Kestrel listens on `localhost` only. There is no login on the UI, so do not expose the port.
- Charts are inline SVG. No external JS or CDN.

```
VRLink.SocialMedia.slnx
src/Publisher/            web app + workers
  Domain/        entities, options, runtime settings
  Data/          EF Core DbContext (SQLite)
  Services/      ClipImporter, ApprovalRules, SchedulingRules (slots/picker/captions), Pipeline (worker steps),
                 AccountService (DPAPI secrets), SettingsStore, EventLog, DashboardQueries, MediaProbe (ffprobe)
  Workers/       ClipWatcher, ClipChecker, Scheduler, PublishWorker, StatsPoller, TokenRefresher (BackgroundService)
  Publishers/    IPlatformPublisher, TikTokPublisher, InstagramPublisher, FakePublisher (DryRun)
  Oauth/         OAuthService (TikTok Login Kit PKCE, Facebook Login)
  Components/    Blazor pages: Overview, Clips, Queue & schedule, Stats by type, Accounts, Settings, Error log
tests/Publisher.Tests/    xUnit: clip check rules, manual-approval gate (incl. DB tampering), migrations, slots/type alternation/captions, importer idempotency,
                                DryRun end-to-end, DPAPI storage, TikTok chunk plan + PKCE
samples/clips/2026-10-19/       3 placeholder clips for DryRun (the mp4 files are NOT real videos)
```

## Clip contract (Unity ClipRenderer → Publisher)

The renderer writes three files per highlight into one folder per day:

```
<ClipsRoot>/<yyyy-MM-dd>/<highlightId>.mp4    H.264/AAC, 1080x1920 (9:16), 5-60 s
<ClipsRoot>/<yyyy-MM-dd>/<highlightId>.jpg    thumbnail (any size, 9:16 recommended)
<ClipsRoot>/<yyyy-MM-dd>/<highlightId>.json   metadata, written LAST
```

Default `ClipsRoot` = `C:\vr\VRCsGo\Builds\Highlights\clips` (`Publisher:ClipsRoot` in appsettings.json).

`<highlightId>.json`:

```json
{
  "highlightId": "m20261019-1830-r07-ace-p3",
  "type": "ace",
  "score": 92.5,
  "matchId": "m20261019-1830",
  "round": 7,
  "durationSec": 18.5,
  "width": 1080,
  "height": 1920,
  "caption": "Ace in the B site retake",
  "hashtags": ["ace", "#vrlink"],
  "venue": "VRLink Home",
  "createdUtc": "2026-10-19T18:30:00Z",
  "heroIsBot": false
}
```

Rules:
- `highlightId` is unique forever, and the file names use it. A clip is imported once only; re-writing it does nothing.
- `type` is a free lower-case string (`ace`, `clutch`, `multikill`, `defuse`, ...). It drives type alternation,
  caption templates and the "Stats by type" page.
- `score` is compared with the check threshold (Settings, default 60); lower scores are rejected before review.
- Write the mp4 + jpg first and the json **last**, ideally as a temp file plus a rename. A json without a
  finished mp4 (missing or still locked) is retried on the next scan.
- `hashtags` may contain the `#` or leave it out. Default hashtags (Settings) are appended.
- `heroIsBot` (optional, default false): true when the highlight's main player is a practice bot. Such clips are
  rejected unless Settings → "Allow highlights whose hero is a bot" is on (default off).
- **Operator veto:** an (empty) file `<highlightId>.rejected` next to the mp4 rejects the clip. If it exists at import
  the clip is imported as `Rejected`; if it appears later, the clip becomes `Rejected` and its unpublished queue
  entries are cancelled (already published posts are not touched). The marker wins over a manual APPROVE:
  approving is refused while it exists, and the PublishWorker re-checks it right before every upload.
- Unknown extra fields are ignored. Missing `createdUtc` falls back to the file time.

## Pipeline

1. **ClipWatcher**: a FileSystemWatcher plus a full rescan every `RescanSeconds` (default 60). The import is idempotent
   (unique HighlightId) and stores a SHA-256 of the mp4. The clip gets status `New`.
2. **ClipChecker** (never approves): score ≥ threshold, duration 5-60 s, 1080x1920, file size ≤ max, no bot hero
   (unless allowed) and no duplicate (same SHA-256 as an older clip). It uses ffprobe when found (`Publisher:FfprobePath`
   or PATH) and DryRun is off; otherwise it trusts the json. Valid clips become **Awaiting approval**, invalid ones
   `Rejected` with the reason.
   **Owner approval (hard rule):** every video must be approved by a human before upload. The Clips page opens on
   "Awaiting approval" with the video playable inline (HTML5 player, served from the clip file by `/media/video/{id}`),
   editable caption and hashtags, and APPROVE / REJECT buttons. Bulk approve exists only as an explicit multi-select
   click. APPROVE stores `ApprovedAtUtc` + `ApprovedBy = "owner"` and creates one queue entry per enabled platform.
   Overview shows the pending count. Defense in depth: Scheduler and PublishWorker refuse (cancel + error log) any
   clip without `ApprovedAtUtc`, even if its status says Approved (e.g. edited in the DB), and the PublishWorker
   re-checks the `.rejected` marker right before the upload. Clips auto-approved by older versions are moved back to
   "Awaiting approval" by the `ManualApproval` migration (already published posts keep their history).
3. **Scheduler**: daily slots per platform in local time (default TikTok 12:30/19:00, Instagram 13:00/19:30).
   When a slot is due (until `SlotGraceMinutes` late), it assigns the best-scoring queued clip whose type differs from the
   platform's previous post. If only clips of the previous type are waiting, the slot is skipped (strict mode, configurable).
   Captions come from per-type templates that rotate post by post.
4. **PublishWorker**: posts through `IPlatformPublisher`. TikTok has two modes (Settings → TikTok posting mode):
   **Send to TikTok inbox (finish in app)** = default until the audit decision: `POST /v2/post/publish/inbox/video/init/`
   (scope `video.upload`, only `source_info`), chunked PUT, status until `SEND_TO_USER_INBOX`. The owner opens the
   TikTok notification, pastes the caption (shown on the Queue page) and posts. Max 5 pending inbox uploads per 24 h;
   no stats for inbox posts (no post id). **Direct post (needs audit)** = Direct Post API as described below.
   Failures are retried with backoff (2, 4, 8, 16 min, up to
   `MaxAttempts`), then the post becomes `Failed`. The UI has Retry and Post now.
5. **StatsPoller**: first poll 30 min after the post, then every 3 h for 14 days, then daily (stops after 90 days).
   It stores a time series (`StatSnapshot`).
6. **TokenRefresher**: refreshes the TikTok access token (24 h) before it expires with the refresh token (365 days),
   checks the Instagram Page token daily via `debug_token`, and shows a warning banner from 7 days before any
   login expires.

**Pause all** stops scheduling and publishing. **DryRun** (default ON on first start) swaps both real publishers
for the FakePublisher: posts are simulated and stats grow on a fake curve, with no network calls.

## Run (development / DryRun demo)

```powershell
cd C:\vr\VRLink.SocialMedia
dotnet build
dotnet test
# DryRun demo with the sample clips and a throw-away data dir:
dotnet run --project src\Publisher -- --Publisher:ClipsRoot=C:\vr\VRLink.SocialMedia\samples\clips --Publisher:DataDir=C:\vr\VRLink.SocialMedia\.demo-data
# open http://localhost:5080 → Queue → "Post now" → Overview shows the simulated posts
```

The data dir (SQLite `publisher.db`) defaults to `%ProgramData%\VRLinkPublisher`. The schema is managed with
**EF Core migrations** (`src/Publisher/Data/Migrations`); pending migrations are applied at startup
(`Database.Migrate()`), so updates keep the owner's data. A DB from the pre-migration skeleton (demo data only) is
refused with a clear message: delete it once.

Schema change workflow (dotnet-ef is a **local** tool, see `dotnet-tools.json`; no global install):

```powershell
cd C:\vr\VRLink.SocialMedia
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project src\Publisher --output-dir Data/Migrations
dotnet test   # MigrationTests fails if the model has changes without a migration
```

## Run as a Windows service

```powershell
dotnet publish src\Publisher -c Release -o C:\vr\VRLink.SocialMedia\publish
# elevated PowerShell:
sc.exe create VRLinkPublisher binPath= "C:\vr\VRLink.SocialMedia\publish\VRLinkPublisher.exe" start= delayed-auto DisplayName= "VRLink Publisher"
sc.exe description VRLinkPublisher "VRLink highlight clips -> TikTok/Instagram, web UI http://localhost:5080"
sc.exe start VRLinkPublisher
# stop / remove:
sc.exe stop VRLinkPublisher
sc.exe delete VRLinkPublisher
```

The service runs as LocalSystem by default. It reads `appsettings.json` from the publish folder. Tokens are
encrypted with DPAPI scope `LocalMachine` (`Publisher:DpapiScope`), so tokens connected while you ran the console
version still work in the service. If you switch to `CurrentUser`, the service must run under the same Windows
account that clicked Connect.

## What the owner (Clemens) must do himself

We never create accounts, log in for you, or enter passwords.

1. **Accounts**
   - Create the VRLink TikTok account.
   - Create an Instagram **Professional** (Business or Creator) account and link it to a **Facebook Page** you administer.
2. **TikTok developer app** (developers.tiktok.com)
   - Create an app and add the products **Login Kit** (platform **Desktop**) and **Content Posting API**, with **Direct Post** enabled.
   - Scopes: `user.info.basic`, `video.upload` (inbox mode), `video.publish` (direct post), `video.list`.
   - Redirect URI: `http://localhost:5080/oauth/tiktok/callback/`. The Desktop platform allows only localhost or 127.0.0.1, and the port is required.
   - Enter the client key and secret on the Accounts page, then click **Connect TikTok** and log in.
   - On the Accounts page: load the creator info, pick the privacy level (it has no default, per TikTok UX rules), choose the interaction toggles, and tick the Music Usage Confirmation consent.
   - **Audit:** until TikTok audits the app, every Direct Post is forced to `SELF_ONLY` (private). Unaudited apps are limited to 5 posting users per 24 h, and TikTok may require the account itself to be private.
   - **Important audit risk:** TikTok's Content Sharing Guidelines list as *not acceptable*:
     "A utility tool to help upload contents to the account(s) you or your team manages."
     An internal VRLink auto-poster is exactly that, so the audit (= public posting through the API) may be refused.
     If so, the options are: (a) keep posting SELF_ONLY and switch posts to public in the TikTok app by hand,
     (b) use TikTok's Upload/inbox mode (built, and the default: the creator finishes each post in the app),
     or (c) post TikTok by hand from the clip folder while Instagram keeps its schedule.
3. **Meta app** (developers.facebook.com)
   - Create an app (Business type) with **Facebook Login for Business** and the Instagram Graph API.
   - Permissions: `instagram_basic`, `instagram_content_publish`, `instagram_manage_insights`, `pages_show_list`, `pages_read_engagement`.
     In development mode they work for app roles (your own account); App Review is only needed for other users.
   - Valid OAuth redirect URI: `http://localhost:5080/oauth/instagram/callback/`. Meta allows `http://localhost` redirects for apps in development mode. If it refuses, a small HTTPS callback page on the VRLinkWeb site is the fallback.
   - Enter the app id and secret on the Accounts page, then click **Connect Instagram** and log in. The first Page with a linked Instagram Professional account is used.
4. **ffmpeg/ffprobe** (optional for the publisher): with `ffprobe.exe` on PATH (or `Publisher:FfprobePath`), clip duration and resolution are measured instead of trusted.
5. **First real test:** turn DryRun off, enable only one platform, use "Post now" on one clip, and check the post (private) in the app.

## Security notes

- App secrets and OAuth tokens are stored only in the SQLite DB, encrypted with Windows DPAPI
  (`ProtectedData`, app-specific entropy). `appsettings.json` holds no secrets. Never commit `publisher.db`.
- The UI has no authentication. Keep `Publisher:Urls` on `http://localhost:5080`, and never bind it to 0.0.0.0 or open the firewall port.
- Media endpoints serve only files of imported clips (the path comes from the DB, never from the request).
- OAuth uses a random `state` (15-min, single-use) and PKCE for TikTok.
- The FakePublisher (DryRun) never touches the network.
- Posted content is public on the platforms after the audit. Player names in captions follow the player opt-in (later phase).

## VERIFY list (re-check against the official docs before the first real post)

Verified against official docs on 2026-10-19: TikTok direct post init, chunk rules, status fetch, creator_info, video/query,
token endpoint and lifetimes, desktop PKCE with hex SHA-256 and localhost redirect; Instagram Reels container,
rupload, status_code, media_publish, the 100 posts/24 h limit, insights metrics; Facebook Login code exchange,
long-lived token, Page token. Still open (also marked `// VERIFY:` in code):

1. TikTok `video/query` response list name `data.videos` (TikTokPublisher.GetStatsAsync).
2. TikTok: SELF_ONLY posts return no `publicaly_available_post_id`, so private posts get **no stats** until they are public.
3. TikTok `brand_organic_toggle` stays false. The owner decides whether VRLink clips need the "promotional content" disclosure.
4. TikTok: whether a Login Kit **Desktop** app may use the Content Posting API (otherwise a Web app with an HTTPS redirect on VRLinkWeb is needed).
5. TikTok: whether unaudited Direct Post also requires the TikTok account itself to be private (error `unaudited_client_can_only_post_to_private_accounts`).
6. Instagram: `share_to_feed` still accepted for REELS; rupload Content-Type header (`application/octet-stream`).
7. Instagram insights: `views` is marked "in development" for REELS. Drop it from the metric list if the API rejects it.
8. Facebook Login: localhost redirect accepted for the app mode in use; the exact permission set for App Review.
9. Facebook `debug_token` with the app token `{app-id}|{app-secret}` to read the Page token expiry (`expires_at` 0 = never).
10. Instagram caption limit 2200 characters / 30 hashtags (CaptionComposer.InstagramMaxChars).
11. Graph API version `v26.0` (`Publisher:GraphApiVersion`) — bump when Meta deprecates it.
12. TikTok inbox mode: whether status later turns `PUBLISH_COMPLETE` with a post id once the owner posts from the inbox (then stats could be fetched).
