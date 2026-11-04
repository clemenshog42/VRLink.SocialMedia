# Montage: "TOP 5 PLAYS THIS WEEK"

Weekly compilation (9:16, 1080x1920, 30 fps, H.264 + AAC, 25-45 s) built only with ffmpeg from clips the Unity ClipRenderer already rendered.
Code: `src/Composer/MontageSelection.cs` (pick), `MontageEdl.cs` (timing), `MontageJob.cs` (end card detection, ffmpeg graphs, manifest). Tests: `tests/Composer.Tests/MontageTests.cs`.

## CLI

```
dotnet run --project src/Composer.Cli -- montage --clips <dir> --from yyyy-mm-dd --to yyyy-mm-dd --top 5 --out <dir> [--ids file] [--allow-bots] [--dry-run] [--rendered-endcard] [--work dir]
```

- `--clips`: folder searched recursively for `<id>.json` + `<id>.mp4` (json without video is skipped, one clip per highlightId).
- `--from/--to`: inclusive, by `createdUtc` date (else the folder date). `--top` 3..7 (default 5, clamped).
- **Production path: `--ids <file>`** - one clip id per line (`#` comments allowed). Only these clips can be used, so only Clemens-approved clips end up in a montage.
  Without `--ids` every clip in the range is a candidate (test material). `--allow-bots` is for bot-match test material only; the default skips `heroIsBot=true`.
- `--dry-run` prints the selection only. `--rendered-endcard` renders our own VRLink end card instead of reusing the #1 clip's.

## Selection

Score descending; at most 2 clips per type; different heroes (matchId + heroLabel) preferred (a second pass relaxes only the hero rule if there are too few);
earlier montages (`format=montage` / `WeeklyTop`) are ignored. Fewer than 3 usable clips = error; fewer than N (>= 3) = a smaller "TOP k" montage.

## Edit (EDL)

`hook 1.2 s` (the #1 play's decisive moment, big "TOP N / PLAYS THIS WEEK") -> `#N ... #1` (4-6 s each, up to 8 s when N=3 would be shorter than 25 s)
-> `ONE VRLink end card` (the #1 clip's own end card, 1.6 s; a rendered card when it has none). Consecutive pieces overlap by 0.2 s (xfade `fadewhite` / `slideleft` / `slideright`,
audio `acrossfade`), so there is no dead air. Each play shows a big rank card ("#3" + title, 1.3 s, pop animation) on the clip itself, then a small "#3" badge; "VRLink" watermark top-right.
Same fonts as `TextPlan` (`Fonts.Resolve`), text via `textfile`, inside the safe area.

- Source end card (Unity ClipRenderer: ~1.6 s, older clips ~1.5 s) is cut away: manifest `endCardSec` if present, else detected (ffmpeg scene cut in the last 3 s), else duration - 1.6 s; 0.1 s extra margin.
- Strongest part = the last seconds before the end card (the decisive slow-mo sits there); manifest `peakSec` (optional) puts the peak at 70 % of the window instead.
- Audio: game audio of each clip (clips without audio get silence), mixed, then the existing two-pass loudnorm to -14 LUFS / -1 dBTP (`AudioBuilder.NormalizeAsync`, extracted from `RenderAsync`).

## Output contract (for the Publisher import)

Files in `<out>`: `<montageId>.mp4`, `<montageId>.jpg` (cover = hook frame: #1 play + "TOP N"), `<montageId>.json`. `montageId` = `weekly-top<N>-<from yyyyMMdd>-<to yyyyMMdd>-<6 hex of the clip ids>`.
The json is a normal clip manifest (`ClipManifest`: `highlightId`=montageId, `video`, width/height, durationSec, caption, hashtags, venue, players, createdUtc, ...) with:

| field | value |
|---|---|
| `type` / `format` | `WeeklyTop` / `montage` |
| `score` | mean of the plays' scores |
| `heroIsBot` | true if ANY play has a bot hero (the Publisher's bot rule then also holds back the montage) |
| `musicFree` | true (add a trending sound in the app) |
| `sources` | `["montage","game","gameAudio","clip:<id>", ...]` |
| `players` | union of the plays' players (label+team+isBot) |
| `caption` | "Top N plays this week at VRLink - which one is your favourite? Free-roam VR, book your squad." |
| `hashtags` | `#VRLink`, `#topN`, `#highlights`, then the source hashtags by frequency (max 12) |
| `parts` | extra (not in `ClipManifest`): `[{rank, clipId, type, title, score, heroLabel, heroIsBot, srcStartSec, durationSec, outStartSec}]`, rank 1 = best |

`matchId` is null and `round` 0 for montages. The Publisher imports the montage like any clip (see "Automatic weekly montage"); approval stays manual (every upload needs Clemens' APPROVE).

## Known weaknesses

- Timing comes from the end card position only (manifests have no peak time yet); an optional `peakSec` / `endCardSec` in the clip json would make the cut exact.
- Source clips are 1080x1920 with burned-in title, kill feed and counters; our rank card sits in the middle and can cover the clip's own moment labels for 1.3 s.
- End card detection (scene cut > 0.3) falls back to the 1.6 s default when the cut is soft.
- Bot test material only so far; the real montage needs approved player clips (`--ids`).

## Automatic weekly montage

The Publisher (`src/Publisher/Services/WeeklyMontage.cs`, worker `WeeklyMontageWorker`, ticks every 15 min) builds the montage by itself once per ISO week; Clemens does nothing but APPROVE the result.

- **When:** settings (Settings page, section "Weekly montage"): `WeeklyMontageEnabled` (default on), `WeeklyMontageDay` (Sunday), `WeeklyMontageTime` (local "10:00", time zone of the Publisher settings), `WeeklyMontageTop` (5, clamped 3..7). Due = the slot of the current ISO week (Monday..Sunday) has been reached and the week is not done. A start later in the same week still runs it; a whole missed week is not caught up.
- **Which clips:** the clips Clemens approved in the last 7 days (`ApprovedAtUtc`, same check as `Pipeline.IsApprovedForUpload`, so an operator `.rejected` marker vetoes; already posted clips count), with a video file, never `format=montage` / `type=weeklytop`, never `heroIsBot`. The ids go to the composer as the `--ids` production path (the file is kept as `<DataDir>/Montage/weekly-<yyyy>-W<ww>.ids.txt`), no date range (clips can be rendered earlier than they were approved). The composer then picks the best N (see Selection).
- **Minimum 3 clips**, else one EventLog line "weekly montage skipped: only N approved clips" and nothing more that week.
- **Run:** `MontageJob.RunAsync` in-process (interface `IMontageRunner`, tests replace it; needs ffmpeg like the CLI). Output goes to `<ClipsRoot>/<yyyy-MM-dd>/`, then `ClipImporter.ScanAsync` imports it: a new clip with format `montage`, type `weeklytop`, status New -> AwaitingApproval and **no approval** (the own click of Clemens is needed like for every clip; the Scheduler and PublishWorker refuse it otherwise).
- **No second montage in a week:** the done week is stored in the DB (`weeklymontage.doneweek` in the settings table) before the import, so a restart does not build another one. A failed run is logged as an error (never crashes the app), retried after 1 h, at most 3 times per week (counter resets at restart).
- **Learning:** format `montage` has its own cold-start prior 0.0 (between split and game), label "Montage"; type `weeklytop` shows as "Top 5" in Insights and the monthly report. Caption templates: the `montage` group (settings).
