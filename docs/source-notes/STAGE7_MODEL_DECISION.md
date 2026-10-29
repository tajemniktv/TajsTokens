# Further model machinery: current selection decision

`NEXT-005` closes with **defer additional machinery**. This is a decision based on
the existing comparisons and their limits, not evidence that richer models can
never help. No new slope/state-space/lag/conformal layer, always-on service or
forecast default is introduced.

## Existing simpler controls and remaining failures

| Question | Existing owner/evidence | Decision |
|---|---|---|
| Can a more local slope repair the reproduced polling decay? | `QuotaPaceModels` already compares elapsed-time EWMA, recent 30m/2h/6h/24h brackets, epoch, persistence and damped controls. Stage 3 repairs per-poll decay without choosing an incident-matching floor. | Keep the repaired owner; no remaining polling-mechanics failure calls for another smoother. Long-horizon extrapolation error remains a separate question. |
| Does adaptation/composition beat the simpler head? | The Stage 6 rolling comparison retains the released head in every development group across recent/month and reconstructed/collection-qualified studies. Final joint-policy gains are mixed by horizon; final labels cannot retrospectively select a winner. | Keep the incumbent and existing mature-error selector. Do not add a state model to recover a cherry-picked final win. |
| Does an activity model justify a richer amount process? | Stage 7 age-group activity improves the recorded-work Brier control, but amount errors and calibration vary across ranges. Quiet local records lack an independent completeness proof. | Keep recent-work continuation and explicit research; a better activity score is insufficient evidence for a richer amount/path law. |
| Can a latent lag/state model explain quota versus local work? | Aligned final intervals have zero recorded work; distinct plan/tier, reporting precision, coverage and account attribution remain alternatives. Collection clocks do not measure backend delay. | A latent lag parameter would not resolve those alternatives from this evidence. First establish a target and independent lag/coverage information. |
| Should another uncertainty layer fix missing bands? | `QuotaForecastCalibration` already calibrates from earlier completed compatible groups; reset targets require completed cycles. Current prospective support at the fixed cutoff is one block per horizon, with reset outcomes immature. | Do not relabel conditional path spread or identification ranges as predictive coverage. No additional conformal guarantee is established by dependent residuals or sparse cycles. |

The existing evidence includes a real concern: the fixed prospective two-hour
remaining-quota error is optimistic (+1.677254pp mean), but it comes from one
non-overlapping outcome in one block. It cannot discriminate persistent slope
bias, burst activity, lag or coverage. Retrospective longer-horizon comparisons
also vary by cohort. Preserve that concern without fitting a correction to it.

The first-20 training control is not treated as a theorem or a permanent refusal:
`NEXT-004` also tests all development-prefix rows, retaining zero work and freezing
before final labels. That releases supported fits but leaves the positive-work
holdout gap visible. More flexible parameters would not fill it.

## Conditions to revisit

Reuse the installed computation journal and scorer for outputs actually recorded
before their targets. Obtain multiple temporally separated compatible blocks,
reset outcomes for reset claims, and matched detailed selected/composed outputs.
Check coverage and worker completion separately from nominal time passing: an
October 29 10:04 UTC read contained 268 compact computations and **no detailed
computations**. The detailed writer exists in the calibration owner; this read
does not prove a successful completed detailed run or diagnose its absence.

If a repeatable residual failure survives those checks and the existing simpler
controls, define one bounded challenger and its hypothesis in the owning model.
Freeze training/selection before final outcomes, preserve availability and head
fallback on identical targets, and compare horizon-specific loss, signed error,
band width/coverage and warning outcomes with non-overlap/block support. Treat
unknown lag/coverage and sparse outcomes as unresolved, rather than learned facts.
Use the existing validation gates; their engineering thresholds are not statistical
guarantees. A consequential live-default or observation-contract change remains a
separate decision.

## Evidence used and validation boundary

This decision reuses the validated [Stage 3 pace comparison](STAGE3_PACE_VALIDATION.md),
[Stage 6 rolling/prospective comparisons](STAGE6_CONTINUATION.md#rolling-joint-comparison-fore-005),
[activity/amount comparison](STAGE7_ACTIVITY.md),
[coherent-path sensitivity](STAGE7_THRESHOLDS.md),
[censoring selection audit](STAGE7_CENSORING.md) and
[aligned-window comparison](STAGE7_SHARED_WINDOWS.md).
Their source versions, cutoffs, counts, numeric proof and limitations remain
attached to those studies. Source reinspection confirms the existing candidate,
adaptive-selection, calibration and detailed-journal owners; it does not create
new predictive validation.

This is documentation-only reconciliation. No tests, build, installation or
runtime manipulation are needed for the decision note. Host checks, resource
acceptance and prospective outcome support remain open in `PROJECT.md`.
