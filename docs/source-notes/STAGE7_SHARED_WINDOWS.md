# Aligned window costs and the history relationship marker

## Decision and supported behavior

`NEXT-004` compares separate conversions with proportional controls on the same
recorded workload and quota intervals. Current evidence cannot select a shared
cost law. Keep transfer/adaptation out of live selection. The existing frozen
regime owner finds no supported boundary in these frames; absence of a candidate
does not establish stability or a provider-policy explanation.

Overview now projects a compact **History relationship** marker from selected-history
analysis. Its link opens the existing accounting section without starting a fit.
Unassessed, filtered, insufficient, no-candidate and unexplained-change states are
distinct. Dates, known account, provider authority, training/trial support and
candidate scope qualify the result. The tooltip identifies range, reference,
manifest and evidence-read time when available. Replacing/unloading history clears
its assessment. This is retrospective evidence, separate from live quota alerts.
The frozen October 28 TT ruler and price policy remain unchanged.

Marker rendering, tooltip readability and physical navigation remain explicit host
checks in `PROJECT.md`; source/build evidence does not close them.

## Method and alternatives

Contract `aligned-window-cost/v1` reuses `QuotaTransferEvaluator`'s scalar
interval-distance solver and `QuotaAccountingModel`'s nonnegative frozen maps.
Pair exact origin/end/horizon and matching provider/profile/source/account/plan/
bucket, requiring known account and identical native total/category/joint workload.
The response's primary/secondary slots are not semantic window identities. Keep
both original durations and reset clocks; non-overlap/block scoring stops at either
reset. Ambiguous or absent weekly matches remain counted.

For total, categories and joint-categories, fit each window separately on the same
aligned rows. Directional controls fit a nonnegative scalar from the opposite
window's frozen prediction into the destination envelope. These are two controls,
not a globally optimized rank-one model. The scales need not be reciprocal and can
partly undo ridge shrinkage; they are not physical prices or quota exchange rates.
Flat references withhold scale. Unverified precision remains a qualification.

The final fifth of the caller-declared timestamp span is held out. Retain the
existing first-20 training comparator; it is not a sample-size theorem. Also fit
the entire development prefix and freeze at the split. It includes zero-work rows
and never scores earlier development origins using later training. Require 20
training rows, positive recorded training work, and complete richer coordinates.
Collection-qualified replay additionally requires known timely labels/training.
Reconstructed replay is reported separately. Bound examination at 2,048 aligned
rows per group and expose source/examined counts.

Actual target workload is an oracle cost input, not an end-to-end forecast or
account-attribution proof. Separate/shared/zero losses use matched available rows;
non-overlap, block counts, envelope loss and displayed-delta MAE remain distinct.
Positive final-work counts prevent equal zero-input predictions from becoming
evidence of proportionality. No automatic winner or model promotion follows.

Regime results reuse `CodexRegimeModel` and its frozen held-out cost reference,
including existing time-ablation checks. Known plan groups, requested tier labels
and quality flags accompany the study. Requested tier metadata does not establish
billed tier. Plan/tier, mixture, local coverage, attribution, meter precision and
backend lag remain alternatives; this study does not measure all of them away.

## Fixed installed-data findings

All ranges are UTC, with reconstructed and collection-qualified reports kept
separate. Native data was read without writes or source acquisition.

- Recent: `2026-10-22T00:00:00Z`–`2026-10-28T16:40:00Z`.
  No ordinary FiveHour intervals, hence no aligned pairs.
- Month: `2026-09-27T00:00:00Z`–`2026-10-27T14:58:00Z`.
  300 ordinary FiveHour intervals, 237 exact pairs, 63 unmatched.
- Paired-report era: `2026-10-07T23:49:39.4161790Z`–
  `2026-10-09T00:30:41.2278267Z`. The endpoints come from 1,430 raw paired
  reported-window clocks, selected by availability timestamps without target/work
  values. This additional range retains the original month/recent controls.
  Its fixed split is `2026-10-08T19:34:28.8654971Z`.

| Horizon | Month/era aligned | Positive recorded work | Era development-prefix training | Era final / positive final |
|---|---:|---:|---:|---:|
| 5 minutes | 179 | 19 | 148 | 30 / 0 |
| 15 minutes | 41 | 3 | 34 | 6 / 0 |
| 30 minutes | 13 | 1 | 13, insufficient | 0 / 0 |
| 120 minutes | 4 | 1 | 4, insufficient | 0 / 0 |

The original month final span has no aligned outcomes. First-20 prefixes at 5/15
minutes have no positive recorded work; 30/120-minute prefixes are too small.
Using the full development prefix enables fits but cannot invent positive-work
final support. Era final rows occupy two chronological blocks per horizon/window.
Every total/category/joint separate/shared conversion predicts zero on their zero
recorded inputs, matching the zero baseline:

| Horizon | FiveHour block envelope loss | Weekly block envelope loss |
|---|---:|---:|
| 5 minutes | 1.93213pp | 0.02941pp |
| 15 minutes | 9.75000pp | 1.00000pp |

Equal losses here do not support a shared law. Positive quota movement with zero
recorded local work also does not identify free usage, an intercept or external
usage. All six reports have zero supported retrospective regime candidates.
The month frame contains two known plan groups and two requested-tier labels
(757 labelled workload rows); recent history contains one of each (474 rows).
Separate cohorts preserve plan boundaries; these metadata counts do not remove
mixture, collection or reporting-delay alternatives.

## Validation and reproducibility

Seven regressions cover exact alignment/scope, different mixtures, future-label
isolation, first-20 versus development-prefix causality, flat/late/sparse evidence,
zero-work prefixes, and all marker states/selection guards. Full Core suite:
**969 passed** (2m 2s). Windows build: zero warnings/errors (59s).

An independent numerical implementation recomputes 948 exported aligned-frame
rows, 48 frozen fits, 216 trial rows, 96 score rows and 16,128 existing-reference
envelope losses across six reports. It checks split/training causality, ridge/joint
coordinates, scales, paired predictions and block arithmetic. It does not
independently prove source completeness, precision, backend lag or provider causality.
The CLI marker lacks a live evidence-read timestamp and says so; fixed dataset
identity and cutoff remain present. Audit output uses cohort aliases and numeric/
time/model metadata, without raw account/source/session IDs, paths or content.

Reproduction commands are in [development documentation](../DEVELOPMENT.md#aligned-shared-window-and-regime-research).
Local audit reports and independent proofs are under `.codex/temp/stage7/shared-windows/`:

| Study | Native audit SHA-256 |
|---|---|
| Recent reconstructed | `49cbcdf5a18dcf6338694a451399d51801e5cf315b4308480fc96b6c0678e119` |
| Recent collection-qualified | `c1817552a995f08a1915c66b03499c4adfb605b273951ce5130eba27068b75dd` |
| Month reconstructed | `790fa32d049276dc2220a4aa387629b0484ad56bc0af7f6200c9faa5f2e1079a` |
| Month collection-qualified | `768769a51da9aac5e16baa14a0d065e888d9417c9383af5a0a708e4088ee49a0` |
| Paired era reconstructed | `e74998ac00616878dde7e52524cb32abf3d54477b363bf4e84a0f5cb421a77cf` |
| Paired era collection-qualified | `4ad2125fdb8cb77ef0a371eb98fd8e2faa7aa8ac3999d168a53be3a878a06ba5` |

The normal build installed and restarted daily app PID **29256** at 10:56:44 local
on October 29. Backup: `20261029T095616079Z-3e1ccaeb`. Pre-commit identity:
`1.0.0+077369b3a0be1ef2405a646a828573467a712954.dirty`, built 09:56:40 UTC.
Installed paired-era-qualified and recent-reconstructed reports match byte-for-byte.
Installed assemblies match normal publish output:

- Core: `8976974e7782edac21c30b74d8c0db623a85162a282d9fa46df8dc1cd14521cd`
- Infrastructure: `5d4054bbf71efa626bdacf84fcb4a21fd440daf44d4f9feeab42b3253ca9e334`
- App: `db3da027941175b5c936ae54291a04569de64d27c26f2e8e0c966d8d9c75ddad`
- ForecastEvaluation: `20f0ccbe53c9c521d18aec05ee57b8836a290d83cc0cecd29acda904f8aa65f6`

After restart, quota collection advanced to 10:00:05 UTC and forecast confirmation
to 10:00:06 UTC. Host interaction and resource acceptance remain separate.

An optional Qodo auto review was submitted against remote base `a4d2380b8`, which
also includes earlier unpushed slices. Operation `2ede62a8-3d30-489b-b16a-963e333a2c1e`
failed in the managed review service without findings. It is not completed review
coverage; no repeat submission was made for the unchanged snapshot.
