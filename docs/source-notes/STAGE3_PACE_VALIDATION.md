# Stage 3: elapsed-time conditional pace

## Policy and calculation receipt

The repaired reset policy is `quota-walk-forward/v5-elapsed-pace`; the fixed-horizon
policy is `quota-workload/v6-eligible-pace`. Both use the existing `time-ewma-2h`
fallback. `QuotaPaceModels.ProductionCandidates` excludes `legacy-ewma` from normal
and adaptive selection, including callers passing it as a fallback. The explicit
research candidate/replay still reproduces the [retained incident](WEEKLY_PACE_INVESTIGATION.md).
No incident-specific rate, minimum burn floor or relaxed calibration threshold was
introduced. Two hours is a provisional existing engineering choice, not an agreed
universal activity window or a conclusion selected from this follow-up's final data.

The fallback integrates interval rates against elapsed-time exposure. An unchanged
fresh later reading adds time, rather than a fixed per-poll vote. Same-clock cached
copies add no slope interval after segmentation. The method spreads displayed
movement over observation intervals, including collection gaps; it does not locate
the actual consumption within an integer meter step. Rounding and backend reporting
delay remain uncertain. A live app-server measurement time is when the response was
observed locally, not an invented backend usage-event timestamp.

Pace selection preserves source/account/limit identity and retains intervening
incompatible plan/window readings as boundaries. A→B→A cannot resurrect the first
A slope; independent buckets remain independent. The existing owners share that
interpretation in current/reset forecasts, cost-observation construction and study
origins. Original quota observations are unchanged. Cost observation/evaluation
versions advance to `quota-cost-observations/v4-elapsed-pace` and
`quota-cost-evaluation/v6-elapsed-pace` so the changed baseline is explicit.

New forecast evidence carries a closed `quota-pace-calculation/v1` receipt: anchor
and cohort, original measured/effective/collected clocks, selected interval/counts,
weighted numerator and exposure denominator, estimator/fallback/selection reason,
missing/later collection counts, scope and separate sustainable-budget arithmetic.
The fixed-horizon receipt describes the pace component; the existing numeric
inference retains any selected workload correction. Receipt prefixes share backing
arrays rather than copying whole histories per origin. Unsupported and persistence
cases do not present a fabricated measured-rate quotient. Invalid meters do not
produce a plausible remaining-budget amount.

Collection-qualified replay preserves the original measurement clock when moving
effective availability to collection time; old measurements do not become fresh
merely because they were imported later. Sustainable budget is **reported remaining
allowance / clock hours from generation to reset**, independent of inferred
consumption since the anchor. Receipts round-trip in persisted forecast diagnostics
and appear in Forecast history/telemetry details. Old captures remain readable with
an unavailable receipt; no original observation or old forecast is restated.

## Observation mechanics

The retained incident still reproduces the legacy 13.971596243 → 0.105023746 pp/h
decay as research. The repaired sparse-calibration reset path instead uses the
existing elapsed-time estimate: about 3.012 pp/h at the jump and 2.613 pp/h at the
final anchor. Those values are consequences of the retained observations, not
acceptance targets for arbitrary data or validated active-work consumption.

Regressions cover identical endpoints/time with sparse/dense flat polling,
same-clock cached copies, elapsed-time startup support, rounded jumps, all-flat
history, stale measurement with later collection, reset/drop/invalid boundaries,
returning regimes, independent buckets, ongoing local work, missing local metadata,
legacy exclusion even when its historical errors are smaller, receipt/budget
reconstruction and persisted receipt equality. A previously legacy-specific mixed
flat/moving assertion now checks its analytic elapsed-exposure result.

All-flat reset history remains unknown within meter precision, with no measured
zero, survival claim or calibrated probability. Conditional fixed-horizon points
remain separate from reset-horizon support. Reset bands retain the earlier
completed-compatible-cycle requirement (eight calibration groups by default);
poll volume or fixed-horizon blocks cannot replace those cycles.

## Chronological comparison

`quota-pace-validation/v1` uses the frozen app-owned numeric snapshot at
`.codex/temp/stage1/worker-fixture/telemetry.db`, requested range
`2026-09-27T00:00:00Z` through `2026-10-27T14:58:00Z`, content identity
`d1ae955775835120e6470aee564962dd1deed3afc027fad5fc50d2d284365972`.
The final fifth of candidate origins is later-period evaluation. No new window was
chosen using its outcomes. The report contains cohort aliases and aggregates,
rather than source/account/session identifiers.

Fixed-horizon rows compare predefined arithmetic candidates, **not** the full
workload/composed selector. `production-reset` replays the actual repaired selector.
Origins retain the study's 30-minute sampling after 15 minutes of compatible
history. Fixed targets require a reading within min(5 minutes, 20% of the horizon);
near-reset outcomes are within-five-minute proxies. Missing targets remain missing.
Local origin-time active/burst/quiet/missing-evidence labels are co-observation,
never account attribution or proven idle state. Burst means a recent 30-minute token
rate above twice the preceding 90-minute rate, with positive ten-minute activity;
that diagnostic rule and the 0–2h/2–24h/over-24h reset-lead bins are explicit
engineering partitions. They are not product thresholds or calibrated states.

The largest weekly final cohort has 116 candidates per target:

| Target | Corrected default points | Matched / non-overlapping | Corrected MAE | Legacy MAE | Corrected signed error |
|---|---:|---:|---:|---:|---:|
| 5 minutes | 116 | 1 / 1 | 0.794 | 0.972 | +0.794 |
| 15 minutes | 116 | 32 / 32 | 0.489 | 0.541 | −0.103 |
| 30 minutes | 116 | 78 / 78 | 0.338 | 0.375 | −0.022 |
| 2 hours | 116 | 59 / 20 | 0.963 | 1.075 | −0.557 |
| 24 hours | 116 | 7 / 1 | 31.988 | 25.470 | −31.988 |
| Near reset, actual production selector | 115 | 0 / 0 | unavailable | unavailable | unavailable |

Errors are percentage points of displayed remaining allowance; positive signed
error is optimistic. This supports neither universal forecast superiority nor
reset-horizon accuracy. The five-minute comparison is extremely sparse; the
24-hour default is worse and has one non-overlapping outcome. The small five-hour
final reset cohort supplies only two matched/one non-overlapping outcome; the
other weekly final cohort has no near-reset labels. No final reset bands are
reported. The correction removes invalid polling mechanics without promoting a
new window or inventing reset confidence.

Availability and signed/absolute/squared error remain separate by cohort, horizon,
local state and reset lead. Overall rows recompute non-overlapping support across
the cohort; support cannot be added across partitions or horizons. Counts of
zero-usage predictions and missed observed movement expose the persistence
baseline's limitations: in this cohort it misses 11, 27 and 24 displayed movements
at 15/30/120 minutes respectively. These are diagnostic counts, not causal burst
labels or proof of account-wide activity. Bands use only banded matched outcomes;
missing points/outcomes are never scored as zero error.

Reproduction uses `--pace-validation` in [development](../DEVELOPMENT.md#pace-validation).
Local reports are `.codex/temp/stage3-pace-validation.json` and
`stage3-installed-validation.json`; installed and development score arrays agree
across all 1,051 rows, with the same dataset identity and an empty installed error
stream. Historical threshold-study/eligibility artifacts keep their old versions;
new runs identify v3/v2 respectively because policy and boundary handling changed.

## Installed validation

The 774-test Core suite passed (1m49s); the Windows build passed with zero
warnings/errors (49.47s), installed the daily build and restarted PID 14864.
Installed Core SHA-256:
`E8206A5C239A0114BEB658E42E960639FC8A503752BFD7E635F24C298D30FB68`.

A read-only audit of the latest 100 retained forecasts found two new-policy live
rows at `2026-10-28T09:04:02.2427828Z` and `09:05:06.9894516Z`. Both selected
`time-ewma-2h`, carried the v1 receipt with known observed/collection clocks, and
reconstructed their stored rate and reported-budget quotients. Their confidence
remained zero. This verifies installed persistence/calculation, not future accuracy.

Backup/current comparison preserved all 96,307 prior native rows and 351 journal
rows without numeric/chronology changes or removals; normal ongoing collection
added seven rows of each kind. No generation was retired and component versions
remain telemetry 16, observatory 11, state-index 8. Runtime audit and preservation
aggregates are `.codex/temp/stage3-runtime-audit.json` and
`stage3-native-preservation.json`; no native Codex files or credentials were changed.

Visual acceptance of the new receipt remains a separate SHIP-004 gate. The last
inspection encountered a Windows Security authentication prompt; no authentication
action was automated. PERF-001 aggregate resource acceptance also remains open.
