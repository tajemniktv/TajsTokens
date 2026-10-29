# Recorded activity and positive amounts (NEXT-001)

## Decision

Keep the existing activity/amount owner and live selection. Token-age grouping
contains useful reconstructed activity information; a larger logistic/activity
model is not justified by these comparisons. A last-eight-positive amount control
improves on the existing positive mean in several cases but does not consistently
beat simple recent-rate continuation. It remains an explicit research comparator.

This closes the current **evaluation/deferral decision**, not a claim that a full
habitual-activity forecast has shipped. Long idle periods and collection-qualified
negative labels remain unsupported. A future broader activity model needs evidence
of collection coverage through quiet targets and a frame including those idle
periods, followed by chronological validation. Adding model complexity cannot
supply missing observation semantics. No new source, service or telemetry retention
is introduced here.

## Method and scope

`usual-recorded-activity-evaluation/v1` calls the existing
`SessionWorkloadPredictionService` at fixed 5/15/30/120-minute horizons. Production
still uses its existing 30/60-minute horizons and selection. Positive amounts,
probabilities and range endpoints remain separate quantities.

- **Target:** recorded installation-local native tokens in `(origin, origin+horizon]`.
  Positive means some tokens were recorded, not personal presence or complete
  account activity. Cached input remains part of the native count; no TT or quota
  conversion is used.
- **Frame:** the existing disjoint UTC grid, after a two-hour initial context and
  20 matured training targets, with the latest positive token under two hours old.
  Origins outside this frame, including long idle periods, are not zero-filled or
  represented as evaluated habitual activity. Quiet target tails continue through
  the fixed target cutoff, independently of the last token event.
- **Activity owner:** last 120 matured targets; use a matching recent-token-age
  group when it has at least 20 targets, otherwise the global history. Beta(1,1)
  smoothing is unchanged. The existing empirical gate requires 64 earlier held-out
  outcomes in the same age group, binned calibration error at most 0.1 and Brier
  no worse than the earlier global-frequency control. Passing this gate in replay
  does not prove display, stable calibration or collection-time deployment.
  Research scores retain ungated activity/expected estimates; live presentation
  continues to require its existing empirical and current-recency gates.
- **Amounts:** the existing positive-target mean and historical positive 10th–90th
  percentiles require eight positive training targets. The challenger uses the
  latest eight positive targets from the same training set, not the eight largest.
  Expected tokens multiply activity estimate and positive mean; range endpoints
  are never multiplied. The global-activity control holds the amount model fixed.
- **Controls:** 30-minute/two-hour continuation and zero additional recorded work.
  Missing continuation context stays unavailable. Availability/all-point errors
  and shared-target errors are separate; candidates cannot win by dropping targets.
- **Time split:** last fifth of the declared timestamp range is final holdout.
  Boundary-crossing targets appear in audit data but neither score partition. All
  fits and empirical gates use earlier matured outcomes only. No parameters or
  candidate choice are tuned on final results.
- **Ranges:** positive-work historical ranges describe training outcomes, not
  calibrated unconditional bands, workload paths or exhaustion probabilities.
  Errors, coverage/width and denominators are reported separately. Non-overlapping
  signed sums and six-hour blocks use the study end as a grouping boundary; it is
  not an account reset, and spacing does not prove independence.

The two explicit cutoffs are target end and retained-evidence as-of. This is
reconstruction using selected retained events known by the evidence snapshot;
events collected after that snapshot are excluded. It does not reconstruct every
past database interpretation or relabel a retrospective result as issued output.
Necessary input-clock checks require earlier positive events to have known valid
collection times by origin. Positive target-clock checks use a five-minute lag
budget. Quiet targets have a nullable clock diagnostic, **not** a fabricated
coverage pass. Neither clock check proves complete collection. The prospective
forecast journal remains a separate evidence route.

## Fixed native comparisons

Both studies use retained evidence as of **2026-10-29T07:54:52.1151393Z**.

| Target range (UTC) | Input events | Positive events used | Evaluated origins across horizons |
|---|---:|---:|---:|
| October 22 00:00–October 28 16:40 | 14,478 | 11,683 | 1,900 |
| September 27 00:00–October 27 14:58 | 20,078 | 19,689 | 4,768 |

Unknown/invalid collection clocks were absent in these selected input snapshots;
late collection was common. Recent final origins at 5/15/30/120 minutes were
357/118/58/15; only 101/33/17/3 had all earlier positive-token clocks available by
origin. Their quiet targets numbered 68/20/9/0, without independent completeness
proof. Month final origins numbered 1,146/383/189/47, with only 68/23/10/3 passing
that necessary earlier-clock check. These samples overlap and are not independent
accounts, trials or deployments.

Lower Brier is better for the stated binary recorded-work target:

| Final horizon | Recent age-group / global Brier | Month age-group / global Brier |
|---|---:|---:|
| 5 minutes | 0.14130 / 0.16619 | 0.10863 / 0.18509 |
| 15 minutes | 0.13881 / 0.14339 | 0.07608 / 0.16065 |
| 30 minutes | 0.11264 / 0.13227 | 0.06717 / 0.15119 |
| 120 minutes | 0.00826 / 0.02180 | 0.03637 / 0.12652 |

Activity information does not establish accurate amounts. Matched token MAE below
is in **millions of native tokens**, on the same origins for all six controls:

| Final range / horizon | Matched origins | Existing hurdle | Recent-positive hurdle | Continue 30m | Continue 2h |
|---|---:|---:|---:|---:|---:|
| Recent / 5m | 318 | 1.889 | 1.389 | 1.363 | 1.724 |
| Recent / 15m | 105 | 5.167 | 4.398 | 3.671 | 4.830 |
| Recent / 30m | 51 | 9.329 | 8.080 | 7.111 | 8.757 |
| Recent / 120m | 13 | 34.156 | 35.144 | 27.741 | 28.597 |
| Month / 5m | 985 | 0.963 | 0.908 | 0.940 | 1.083 |
| Month / 15m | 329 | 2.506 | 2.392 | 2.501 | 2.829 |
| Month / 30m | 162 | 4.524 | 4.309 | 4.997 | 5.152 |
| Month / 120m | 39 | 21.177 | 15.106 | 18.951 | 15.845 |

The two ranges differ materially; retrospective amount gains are not a stable
promotion result. The recent age-group model's final binned calibration error is
about 0.13/0.12/0.10/0.07 across the four horizons, despite some earlier gates
passing. No calibrated-probability or path-distribution claim follows.

## Verification and reproduction

Six focused regressions cover fixed nominal horizons/splits, non-overlap and
matched denominators, future-data invariance, delayed/unknown evidence, quiet
targets, missing contexts, zero/sparse data, latest-eight selection and unchanged
production horizons/results. Independent Python reconstruction reproduced every
origin, training count, age group, frequency, positive mean/quantile, empirical
gate, continuation control, clock diagnostic and all 96 score/16 coverage rows.
The same-frame reports include all-point errors, shared-target MAE/WAPE, signed
sums, positive-range coverage/width and empirical-gate subsets.

Use the explicit commands in [DEVELOPMENT.md](../DEVELOPMENT.md#recorded-activity-research).
Audit output is closed numeric/time data without account/session/source IDs,
paths or content. It still describes local usage patterns; review before sharing.

The full Core suite passed **947 tests** (2m 2s). The Windows solution build
passed with zero warnings/errors (55s), installed normally and restarted the app
as PID **11236**. Backup is `20261029T080124673Z-84078297`. The build's pre-commit
identity is based on `8f6b111` plus this source patch; no live selection, schema,
TT weights or acquisition policy changed. Both fixed study reports reproduced
byte-for-byte through the installed tool, and installed assemblies match publish:

- Core: `cc9d8c20fa94f553b7ab5f5241778281febaeebf4c6da1ac0eb07e87be16430a`
- Infrastructure: `c0a4dbddf1cd765c8380a2b9b02712f4bb81a6b286247284d469e8a7a105727e`
- App: `eb719ecbd754f10af54dcc0383dfcd2a96d92655baac28e519a409d6e37e50f9`
- ForecastEvaluation: `daa0ed31c07bfd730e1df14b3b7dcfcebe2fe5fd83f5e1117ac15609fb036410`

After restart, quota collection advanced to 08:04:07 UTC and forecast confirmation
to 08:04:08 UTC. Host presentation checks remain separate.

Frozen numeric evidence is in `.codex/temp/stage7/`. SHA-256 identities:

| Study | Native audit | Independent aggregate proof |
|---|---|---|
| Recent | `793c3bef323e7b29eb3d66b8df4d524ea6ce0e6eb1d2fd06adbad039987d27fe` | `16454ae3fc93389b01a25141dd1a4084c935d87a51f97f1b3936c6aa59b89fb5` |
| Month | `e56e3d6d31d93cff0d69e3571cf42b9e22e90d2bd1a8686e66851eb8a93eeca4` | `06a94bf09562a99229c6884bd1c044c3769954ef4fd2e8e460199a30dcf7f10e` |

This explicit research slice adds no UI behavior or host acceptance claim.
PROJECT.md retains the separate tray, Planning restoration, alert and resource gates.
