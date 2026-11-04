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

The Publisher import is not wired yet; `matchId` is null and `round` 0 for montages. Approval stays manual (every upload needs Clemens' APPROVE).

## Known weaknesses

- Timing comes from the end card position only (manifests have no peak time yet); an optional `peakSec` / `endCardSec` in the clip json would make the cut exact.
- Source clips are 1080x1920 with burned-in title, kill feed and counters; our rank card sits in the middle and can cover the clip's own moment labels for 1.3 s.
- End card detection (scene cut > 0.3) falls back to the 1.6 s default when the cut is soft.
- Bot test material only so far; the real montage needs approved player clips (`--ids`).
