# VRLink social media — monthly report (2026-09-20 – 2026-10-20)

- Measured posts (48 h views available): **49** in the last 30 days, 72 in total.
- Not measurable: 1 post(s) (TikTok inbox/private posts have no stats; learning uses Instagram + direct TikTok only).
- Score = log views at 48 h vs the platform's rolling 30-day median (0 = typical, +1 ≈ one spread better). ×median = typical multiple of your median views.
- Recommended daily mix: 1 × Reaction (or another reaction/IRL clip) + 1 × Split kills4

## By format
| | posts | score | ×median | engagement | median views 48 h |
|---|---:|---:|---:|---:|---:|
| Split | 8 | +0.44 | 1.38× | 9.6 % | 766 |
| Reaction | 8 | +0.41 | 1.16× | 9.9 % | 9048 |
| IRL | 17 | +0.26 | 1.35× | 9.8 % | 2364 |
| Game | 16 | -0.63 | 0.63× | 9.4 % | 327 |

## By highlight type
| | posts | score | ×median | engagement | median views 48 h |
|---|---:|---:|---:|---:|---:|
| reaction | 8 | +0.41 | 1.16× | 9.9 % | 9048 |
| clutch | 13 | +0.22 | 1.22× | 9.7 % | 1115 |
| kills4 | 8 | -0.05 | 0.97× | 9.6 % | 503 |
| kills3 | 8 | -0.18 | 0.88× | 9.5 % | 327 |
| ace | 12 | -0.26 | 0.92× | 9.6 % | 943 |

## By format + type
| | posts | score | ×median | engagement | median views 48 h |
|---|---:|---:|---:|---:|---:|
| IRL clutch | 9 | +0.56 | 1.60× | 9.8 % | 2364 |
| Split kills4 | 4 | +0.44 | 1.38× | 9.6 % | 686 |
| Split kills3 | 4 | +0.44 | 1.37× | 9.6 % | 803 |
| Reaction reaction | 8 | +0.41 | 1.16× | 9.9 % | 9048 |
| IRL ace | 8 | -0.08 | 1.11× | 9.8 % | 2788 |
| Game kills4 | 4 | -0.53 | 0.68× | 9.5 % | 346 |
| Game clutch | 4 | -0.56 | 0.66× | 9.5 % | 354 |
| Game ace | 4 | -0.62 | 0.64× | 9.2 % | 385 |
| Game kills3 | 4 | -0.80 | 0.56× | 9.3 % | 292 |

## By caption variant
| | posts | score | ×median | engagement | median views 48 h |
|---|---:|---:|---:|---:|---:|
| split.*#1 | 8 | +0.44 | 1.38× | 9.6 % | 766 |
| reaction.*#1 | 8 | +0.41 | 1.16× | 9.9 % | 9048 |
| irl.*#0 | 17 | +0.26 | 1.35× | 9.8 % | 2364 |
| game.*#0 | 8 | -0.59 | 0.65× | 9.3 % | 381 |
| game.*#1 | 8 | -0.67 | 0.61× | 9.4 % | 315 |

## By posting hour (local)
| | posts | score | ×median | engagement | median views 48 h |
|---|---:|---:|---:|---:|---:|
| 09:00 | 8 | -0.67 | 0.61× | 9.4 % | 315 |
| 11:00 | 8 | -0.59 | 0.65× | 9.3 % | 381 |
| 12:00 | 8 | -0.63 | 0.80× | 9.9 % | 6301 |
| 19:00 | 8 | +0.44 | 1.38× | 9.6 % | 766 |
| 20:00 | 8 | +0.41 | 1.16× | 9.9 % | 9048 |
| 21:00 | 9 | +1.04 | 2.14× | 9.7 % | 976 |

## By platform
| | posts | score | ×median | engagement | median views 48 h |
|---|---:|---:|---:|---:|---:|
| Instagram | 33 | +0.09 | 1.06× | 9.5 % | 535 |
| TikTok | 16 | -0.11 | 0.96× | 9.9 % | 7810 |

## Suggested detector score weights (Unity HighlightRules)
Multipliers for the per-type highlight score, from all measured posts (shrunk towards 1×, Kills3 = 1.0).
Applying them is a separate manual step by the PO in the Unity project.

```json
{
  "typeScoreMultipliers": {
    "Ace": 1.26,
    "Kills4": 1.18,
    "Kills3": 1,
    "Clutch": 1.27,
    "OneTapStreak": 1,
    "LastSecondDefuse": 1,
    "GrenadeMultiKill": 1,
    "NinjaDefuse": 1
  },
  "basedOnPosts": 72,
  "generatedUtc": "2026-10-20T09:35:12.4626800Z"
}
```
