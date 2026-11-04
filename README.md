# VRLink.SocialMedia

Social media tooling for VRLink (separate from the Unity game). Projects: `src/Publisher`, `src/Capture`, `src/Shared` (below),
`src/Composer` + `src/Composer.Cli` (IRL + overlay / split-reality clips, see [docs/V2.md](docs/V2.md)). Build/test: `dotnet build VRLink.SocialMedia.slnx`, `dotnet test VRLink.SocialMedia.slnx`
(the Capture integration tests need ffmpeg/ffprobe, ~30 s). Pre-commit secret check: `git config core.hooksPath tools/githooks`.

## Arena camera (V1): Capture service, footage access, clap sync, calibration

```
src/Shared/        VRLink.Shared: clip contract (ClipManifest), .vrdemo reader (port of Unity Highlights/DemoReader, v1–v3),
                   clap sync (audio onsets, controller claps, offset solver), camera model + pose solver
src/Capture/       VRLink.Capture: Windows service/console, ffmpeg recorder per camera, segment index, retention/pins,
                   footage API (window cut, frame grab, audio), clap test endpoint — http://localhost:5090
src/Publisher/     + "Arena camera" page (calibration + clap test UI)
tests/Shared.Tests, tests/Capture.Tests (real ffmpeg with the TEST source), tests/data (golden .vrdemo files)
```

### Coordinate system (arena space)
Same as the game (`Docs/ARCHITECTURE.md` arena space, Unity world space): **left-handed, metres, +Y up, +X right, +Z forward**.
Demo positions are used unchanged. The camera pose is a Unity transform: position + Euler angles (Pitch = X, Yaw = Y, Roll = Z,
degrees, Unity order Z→X→Y, i.e. R = Ry·Rx·Rz). The camera looks along its local +Z; positive pitch looks down, positive yaw turns right.
Image pixels: (0,0) top-left, u right, v down; principal point = image centre; `VFovDeg` = vertical field of view;
optional radial distortion `K1` (normalised coordinates scaled by 1 + K1·r²). Saved calibrations carry this description in the json.

### Time
- Demo v3 stores a UTC anchor plus `UtcSync` records; `DemoData.UtcAt(t)` uses the last sync before `t` (as the Unity reader).
  v1/v2 demos have no anchor (CreatedUtc ≈ start; approximate, not good enough for the clap test).
- Camera segments are stamped with the PC wall clock by ffmpeg (`-use_wallclock_as_timestamps 1` + `-copyts`); the segment
  list gives each segment's first-frame UTC, files are renamed `<cam>_yyyyMMddTHHmmss.fffZ.mp4` and listed in `<cam>/index.jsonl`.
- **Camera offset** (clap test) = footage time − demo time. The footage API applies it: footage for true time t is taken at t + offset.

### Hardware (Clemens)
- One **4K wide-angle USB webcam with a microphone** (e.g. Logitech Brio 4K, OBSBOT Meet 2), on **USB 3** directly at the server PC
  (no unpowered hub). For 4K MJPEG set `"InputArgs": "-vcodec mjpeg"`; with an NVIDIA GPU use `"VideoCodecArgs": "-c:v h264_nvenc -preset p4 -cq 23"`.
- Mount it **high in a corner (~2.5 m)** looking diagonally across the room so the whole play area is visible, slightly tilted down.
  Don't move it after calibration (re-calibrate if it moves). Keep it out of direct sunlight; fixed focus/exposure if the camera software allows.

### Run the Capture service
```powershell
cd C:\vr\VRLink.SocialMedia
dotnet run --project src\Capture                      # console, http://localhost:5090 (TEST source cam1 by default)
# list the camera + mic names, then put them into src\Capture\appsettings.json ("Source": "dshow", VideoDevice, AudioDevice):
curl http://localhost:5090/api/devices
curl http://localhost:5090/health                     # 200 = every camera records, last segment fresh; else 503
```
Windows service (elevated PowerShell):
```powershell
dotnet publish src\Capture -c Release -o C:\vr\VRLink.SocialMedia\publish\capture
sc.exe create VRLinkCapture binPath= "C:\vr\VRLink.SocialMedia\publish\capture\VRLink.Capture.exe" start= delayed-auto
sc.exe start VRLinkCapture
```
- ffmpeg is found via `Capture:FfmpegPath`, PATH (process/machine/user) or the WinGet links folder. It runs at BelowNormal priority
  inside a kill-on-close job (no orphaned ffmpeg holding the camera), and is restarted with backoff (2 s … 60 s) if it stops.
- Data: `%ProgramData%\VRLinkCapture\<cam>\segments\*.mp4`, `index.jsonl`, `pins.json`, `offsets.json`, `footage-cache\`.
- Retention: unpinned segments older than `RetentionMinutes` (15) are deleted; pinned windows stay until released; `DiskCapGb` (20)
  deletes the oldest unpinned first (pinned only as a last resort, logged). 10 s 1080p segments ≈ 7 MB → ~0.7 GB per 15 min.
- The TEST source (`"Source": "test"`) needs no camera: testsrc2 1080p30 with the UTC time burned in and a 1 kHz beep at every UTC
  multiple of 5 s on the audio track. Note it costs some CPU; disable the camera entry when not needed.

API (localhost only, no authentication):
| | |
|---|---|
| `GET /health`, `GET /api/cameras` | status: fps, dropped frames, speed, last segment age, restarts, size |
| `GET /api/devices` | DirectShow devices (`ffmpeg -list_devices`) |
| `GET /api/segments?cam=&from=&to=` | index entries |
| `GET /api/pins`, `POST /api/pins {camera?, fromUtc, toUtc, reason}`, `DELETE /api/pins/{id}` | keep footage beyond retention |
| `GET /api/footage?cam=&from=&to=` | mp4 of a true-UTC window (offset applied), re-encoded, frame/sample accurate |
| `GET /api/frame?cam=&utc=&w=` | jpeg at a true UTC |
| `GET /api/offsets`, `PUT /api/offsets/{cam}?ms=` | camera time offsets |
| `POST /api/clapsync?cam=&demo=<path>&save=` | clap test on a demo + the raw camera audio |

### Clap test (time offset) — Clemens
1. Capture service running with the real camera. On the operator dashboard start a **calibration recording** (demo v3).
2. One person stands **in view of the camera, near the mic**, and bangs the two controllers together firmly **4–6 times, ~2 s apart**
   (irregular spacing is best). Quiet room otherwise.
3. Publisher → **Arena camera** → pick the camera and that demo → **Run clap test**. It finds the bangs in the demo (controllers
   meet with high speed) and in the room audio, pairs them and shows offset, matched count and max residual. Done when ≥3 claps
   match and the residual is ≤ 2 frames (66 ms); then **Save offset**. Repeat once to confirm it's stable.

### Calibration (camera pose) — Clemens
1. Record a **30 s calibration demo**: one person walks around the whole play area holding a controller **up high**, visiting the
   corners and the middle (different heights help).
2. Publisher → **Arena camera** → camera + demo → **Keep this footage (pin)**.
3. Scrub the slider to 4–6 moments spread over the room. Choose the player and **right controller** (square marker) and click the
   controller in the picture. Optionally also click the **headset** in the same frames (more points = better).
4. **Solve**. If the camera's vertical FOV is known (spec sheet or a previous calibration), tick **lock FOV**: then 6 clicks give
   millimetre accuracy. Check that the projected head rings and controller markers sit on the person in other frames and that the
   yellow venue outline matches the floor; fine-tune with the sliders (reprojection error updates live).
5. **Save for venue …** → `%ProgramData%\VRLinkPublisher\arena-camera\<venue>\<cam>.json` (pose, FOV, k1, offset, clicked points).

Solver accuracy (synthetic 4K corner camera, 0.3 px click noise, tests in `CameraSolverTests`): FOV locked, 6 clicks → 1.5–4.5 mm,
≤0.05°; FOV free, 6 clicks → 2–19 mm, ≤0.09° (depth and zoom trade off); noise-free → exact. UI end-to-end (6 simulated clicks on the
1280 px preview) → 0.47 px RMS, position within 1 cm with FOV locked.

## VRLink Publisher

Local Windows app that picks up rendered VRLink highlight clips, checks them, lets the owner review and
**approve every video by hand**, posts approved clips to **TikTok**, **Instagram Reels** and **YouTube Shorts** on a daily schedule, and shows post statistics in a
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
  Publishers/    IPlatformPublisher, TikTokPublisher, InstagramPublisher, YouTubePublisher, FakePublisher (DryRun)
  Oauth/         OAuthService (TikTok Login Kit PKCE, Facebook Login, Google OAuth for YouTube)
  Components/    Blazor pages: Overview, Clips, Queue & schedule, Stats by type, Accounts, Settings, Error log
tests/Publisher.Tests/    xUnit: clip check rules, manual-approval gate (incl. DB tampering), migrations, slots/type alternation/captions, importer idempotency,
                                DryRun end-to-end, DPAPI storage, TikTok chunk plan + PKCE, YouTube (fake Google API: resumable upload, resume, tokens, gates, stats)
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
3. **Scheduler**: daily slots per platform in local time (default TikTok 12:00/19:00, Instagram 11:30/20:00, YouTube 13:00/18:30; YouTube is OFF until switched on in Settings).
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
   renews the YouTube access token (about 1 h; the publisher also renews it on demand), checks the Instagram Page token daily via `debug_token`, and shows a warning banner from 7 days before any
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

## YouTube Shorts

Third platform next to TikTok and Instagram. It uses exactly the same gates: a clip is only queued after **your APPROVE** on the Clips page,
the Scheduler and the PublishWorker refuse clips without a recorded manual approval, and `YouTubePublisher` checks it once more itself.
YouTube is **off by default** (Settings, "YouTube Shorts") and uploads as **private** until you choose unlisted/public (Accounts page).

What the code does (docs checked 2026-11-04):

- OAuth 2.0 authorization code flow for a Google "Web application" client with PKCE (S256), `access_type=offline`, `prompt=consent`; scopes
  `https://www.googleapis.com/auth/youtube.upload` (upload) and `https://www.googleapis.com/auth/youtube.readonly` (statistics of private/unlisted videos, channel name).
  Client id/secret and the access/refresh tokens are stored DPAPI-encrypted in the SQLite DB like the other platforms. Access tokens (about 1 h) are renewed with the refresh token.
- `videos.insert` resumable upload: `POST https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status`, then one `PUT` of the file.
  After a dropped connection or 500/502/503/504 the publisher asks the server how far it got (`PUT` with `Content-Range: bytes */total`, answer `308` + `Range`),
  sends only the rest, and backs off exponentially. If the attempt still fails, the session URI is kept in the post (`ExternalId`) so the next attempt resumes
  the same upload instead of creating a duplicate video.
- Metadata: title = first caption line (no hashtags) shortened so that `... #Shorts` fits into 100 characters; description = caption + hashtags + `#Shorts` + optional booking link (max 5000 bytes);
  `categoryId` 20 (Gaming); `privacyStatus` from the setting (default `private`); `selfDeclaredMadeForKids=false`; `containsSyntheticMedia=false`.
- Checked before any upload: vertical clip (height > width) and at most 180 s (Shorts can be up to 3 minutes). Anything else fails the post without a network call.
- Statistics: `videos.list?part=statistics` (up to 50 ids per call, 1 quota unit) into the same series as TikTok/Instagram, so the V5 learning loop (per-platform normalisation, slots, caption variants) treats
  YouTube like the others. Videos uploaded as `private` are posted and measured but **not learned from** (only you can watch them, so the views say nothing about the audience).
- Posting slots default to 13:00 + 18:30 (Settings, learned from data after 10 measured posts like the other platforms).

### What Clemens has to do for YouTube

1. **Channel:** use (or create) the YouTube channel for VRLink. If it is a Brand Account channel, pick that channel on Google's consent screen.
2. **Google Cloud project** (console.cloud.google.com): create a project, then **APIs and services > Library > "YouTube Data API v3" > Enable**.
3. **OAuth consent screen** (menu "Google Auth Platform" or "OAuth consent screen"): User type **External**, app name (e.g. "VRLink Publisher"), support/contact email.
   Add the scopes `youtube.upload` and `youtube.readonly` (Data Access).
   - **Publishing status "Testing":** only the listed **test users** (add your own Google account, max 100) can connect, and Google lets the login **expire after 7 days**
     (the refresh token too). The Publisher then shows "invalid_grant ... click Connect YouTube again". Fine for the first tests, not for daily operation.
   - **Publishing status "In production"** (Audience page > Publish app): the login does not expire after 7 days. Google shows a "Google hasn't verified this app" screen when you connect
     (click Advanced > continue); unverified apps with sensitive scopes are capped at 100 users, which is enough for one owner account. You do not have to submit the app for verification for this single-account use.
4. **Credentials > Create credentials > OAuth client ID**, application type **Web application**. Authorised redirect URI exactly (with the trailing slash):
   `http://localhost:5080/oauth/youtube/callback/` (Google allows http for localhost). Copy the **client id** and **client secret**.
5. Publisher > **Accounts** > YouTube Shorts: paste client id and secret, **Save credentials**, **Connect YouTube**, log in with the channel's Google account and tick **all** permission boxes.
   Optional: booking link for the descriptions. Privacy stays **private** until you switch it to unlisted/public on the same page.
6. Publisher > **Settings**: tick **YouTube Shorts** and check the slots. First real test: DryRun off, only YouTube enabled, "Post now" on one approved clip, look at it in YouTube Studio (private).

### Quota and audit (verified on Google's pages on 2026-11-04, they changed recently)

- Default quota: 10,000 units/day per project for most methods (`videos.list` = 1 unit). **`videos.insert` has its own bucket of 100 calls per day, each costing 1 unit**
  (https://developers.google.com/youtube/v3/determine_quota_cost). The older "1600 units per upload" figure is no longer in the documentation. At 2 posts/day the quota is irrelevant.
  A quota error (`quotaExceeded`) fails the post with a hint; the quota resets at midnight Pacific Time.
- **Private lock:** since 2020-07-28 videos uploaded through `videos.insert` by unverified API projects were locked to private until a compliance audit. **Google reversed this on 2026-11-02**
  (revision history of the YouTube Data API): "Videos uploaded through the videos.insert method from unverified API projects are no longer restricted to private viewing mode."
  Without a `privacyStatus` YouTube would default to public, so the Publisher always sends one (default `private`). If a Short still shows up locked as private although you chose public/unlisted,
  YouTube Studio / Google's email says so, and the API audit ("YouTube API Services - Audit and Quota Extension Form") is the way to lift it.
- An audit is only required for **more quota than the default** (the form: https://support.google.com/youtube/contact/yt_api_form).

## Security notes

- App secrets and OAuth tokens are stored only in the SQLite DB, encrypted with Windows DPAPI
  (`ProtectedData`, app-specific entropy). `appsettings.json` holds no secrets. Never commit `publisher.db`.
- The UI has no authentication. Keep `Publisher:Urls` on `http://localhost:5080`, and never bind it to 0.0.0.0 or open the firewall port.
- Media endpoints serve only files of imported clips (the path comes from the DB, never from the request).
- OAuth uses a random `state` (15-min, single-use) and PKCE for TikTok and YouTube (Google).
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
13. YouTube (docs checked 2026-11-04, not yet run against the real API): Google's web-server OAuth page does not mention PKCE; the Publisher sends `code_challenge` (S256) together with the client secret.
    If Google ever rejects the parameter, remove it in `OAuthService.YouTubeAuthorizeUrlAsync` / `YouTubeCallbackAsync`.
14. YouTube: the resumable upload is a single `PUT` of the whole file (documented); chunked upload (multiples of 256 KB) is not implemented.
15. YouTube: `videos.list` with the `youtube.readonly` scope returns statistics of the owner's private/unlisted videos (the scope table of the method was not readable in the docs fetch).
16. YouTube: the `#Shorts` hint and "vertical, up to 3 minutes" are what YouTube Help describes; YouTube decides after processing whether a video is shown as a Short.
