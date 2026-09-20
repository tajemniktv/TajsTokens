# Intelligence ownership audit — 2026-09-20

This is an implementation/caller audit, not another roadmap. PROJECT.md owns delivery status.
Classification is by current callers, not by a class's age or whether its name says “evaluation”.

## Runtime owners

| Question | Owner | Supporting implementations (not separate product models) |
| --- | --- | --- |
| Future local workload | `TokenWorkloadPredictionService` | Session/activity and composition heads; recent-rate and fitted candidates |
| Completed workload → quota cost | `QuotaAccountingModel` | Frozen nonnegative fits, compatible observations and training policy |
| Current quota forecast | `QuotaPredictionService` | Pace, matured workload correction, composed-policy validation, reset outlook |
| Structural accounting change | `CodexRegimeModel` | Persistent block residual checks and retrospective within-cycle segmentation |
| Hypothetical user scenario | `QuotaPredictionService.Simulate` | Query against compatible observed history; no separately instantiated planner |

`ICodexIntelligence` orchestrates these answers. It is not another model or repository.
`CodexCurrentState` carries current meters, freshness and source health to tray/alerts; the product
snapshot no longer embeds the collector's `TelemetrySnapshot`.

## Prediction/evaluation inventory

The runtime namespace remains stable for callers. Files are grouped under `Services/Workload`,
`Accounting`, `Forecast`, `Regime` and `Statistics`. Explicit experiments use `Core.Research`.

| Class | Classification | Caller / disposition |
| --- | --- | --- |
| `TokenWorkloadPredictionService` | runtime | Intelligence service; owns local workload prediction and its chronological selection |
| `SessionWorkloadPredictionService` | runtime helper | Workload owner's conditional activity/amount head; research can replay the same implementation |
| `WorkloadCompositionPrediction` | runtime helper | Workload owner; composition, not an independent volume model |
| `CodexNowcastActivity` | runtime helper | Workload owner and evaluation; observed state, never a fabricated probability |
| `QuotaAccountingModel` | runtime | Sole cost fitting/scoring implementation; runtime enumerates only supported pooled/category/model candidates and references |
| `QuotaCostObservationBuilder` | runtime helper | Accounting and validation; preserves cohort/reset/measurement contracts |
| `QuotaCostTrainingPolicy` | runtime helper | Accounting and composed prediction; preceding compatible training only |
| `QuotaEvaluationCoverageBuilder` | runtime helper | Accounting diagnostics; explicit missingness and attribution |
| `QuotaPredictionService` and its ResetOutlook/Scenarios partials | runtime | Intelligence service; one forecast owner, different questions/heads |
| `QuotaForecastCalibration` (formerly `QuotaForecastBacktester`) | runtime helper | Forecast selection and empirical calibration actually require chronological replay; not research-only code |
| `QuotaWorkloadCorrection` | runtime helper | Forecast owner's fitted correction; extracted from ablation backtester |
| `QuotaPaceModels` | runtime helper | Internal pace candidate implementations shared with research |
| `ComposedQuotaPolicy` | runtime helper | Forecast owner; strict evidence, comparison and fallback policy |
| `ComposedQuotaValidation` | runtime helper | Chronological joint validation required by live selection; research invokes the same calculation |
| `ComposedQuotaBreakdowns` | runtime helper | Validation diagnostics, not a predictor |
| `CodexForecastFeatureBuilder` | runtime helper | Origin-time feature construction shared by all heads |
| `CodexScenarioHistoryBuilder`, `RecentScenarioPatternBuilder` | runtime helper | Canonical scenario inputs and user-selected recent pattern |
| `CodexRegimeModel` | runtime | Owns both residual-change algorithms; reset timestamp is not a change-point constraint |
| `ChronologicalEvidence` | runtime helper | Shared non-overlap/block/support calculation, separate from accounting-window identity |
| `AccountLocalRidge` | runtime helper | Numerical primitive used by runtime heads; not a product model |
| `QuotaCostEvaluation` | research | Explicit cost-only candidate comparison; delegates fitting to accounting owner |
| `QuotaCostAblations` | research | Context/activity/runtime/time feature extensions, never supplied by runtime |
| `QuotaForecastEvaluation` | research | Aggregate comparison driver called by Model Lab/CLI |
| `ComposedQuotaEvaluator` | research | Explicit 5/15/30/120-minute experiment over shared validation implementation |
| `QuotaWorkloadBacktester` | research | Extracted candidate-ablation driver; cannot own the live correction |
| `QuotaTransferEvaluator` | research | Cross-cohort scaling experiment; never live account transfer |
| `TtEvaluator` | research | Frozen-basis TT experiment; no runtime TT promotion |
| `SessionQuotaEvaluator` | research | Conditional session-to-quota comparison, not another runtime predictor |
| `ApiPriceWorkload` | runtime derived quantity | Ledger API-equivalent display plus research weighting; not actual price/credit/quota |
| `QuotaHistoryPolicy`, `QuotaResetGenerationPolicy`, `QuotaResetDetector` | runtime evidence policy | Eligibility, window identity and observed reset transitions; not statistical independence or regime models |
| `RolloutAccountAssociationPolicy` | runtime evidence policy | Shared bounded assertion matching for quota and ledger; conflicting ownership stays unknown |
| `CodexEvidenceDrift`, `CodexDailyPairing`, `CodexProviderReconciliation` | runtime evidence diagnostics | Explicit report/metadata changes and compatibility; do not substitute for structural drift inference |
| `CodexIntelligenceProjection` | runtime presentation | Selection, grouping and manifests; no fitting or acquisition |
| `ScenarioPlannerService` | dead owner removed | Its supported scenario query moved into the forecast owner |
| `ForecastingService`, `IForecastingService` | dead, removed previously | Reset outlook already belongs to the forecast owner |

`QuotaAlertEngine`, `SystemTrayStatusPresenter`, `TelemetryDiagnosticsPresenter` and
`CodexThreadItemPresenter` are runtime consumers/presenters, not prediction/evaluation classes.
Report records and inference artifacts remain shared data contracts. No historical evidence tables
or saved TT/forecast artifacts are deleted by this cleanup.

## Read ownership

- `SqliteForecastDatasetReader` implements `ICodexForecastDatasetReader`. Its existing token query
  optionally projects ledger provenance and native thread/project identity in the same transaction
  as forecast inputs. It does not perform a second token scan for the product engine.
- `CodexServerEvidenceService` implements `ICodexServerEvidenceReader`, retaining bounded history
  reads, malformed-capture counts and activity-bucket hydration beside its existing storage owner.
- `CodexIntelligenceEngine` receives those contracts. It contains no SQL, database path or SQLite
  dependency. Cross-source capture times remain explicit; separate provider reads are not atomic
  with the local dataset.
- Ledger attribution uses the same provider/profile/source/session/time/assertion policy as quota.
  Unknown ownership is not assigned to the logged-in account. Ledger range remains `[from,to)`;
  predictive lookback and endpoint observations retain their existing semantics.

## Reset boundaries versus evidence support

`chronological-blocks/v1` groups valid non-overlapping outcomes into blocks beginning at the first
eligible outcome and spanning at least six hours before a new block begins. A reset always starts
a new block; a cross-reset outcome is rejected. This spacing is a provisional engineering assumption,
not measured independence or calibrated coverage. All calibration outcomes must already be available
at the prediction origin; strict composed replay still enforces collection-time availability.

Cost/composed validation, fixed-horizon pace calibration, scenarios, TT and transfer comparisons now
use block summaries instead of treating a whole weekly cycle as one statistical sample. Actual cycle
counts remain diagnostic fields. Cost/composed reports expose raw/non-overlapping counts, block count,
distinct days/cycles, span and a bounded initial-positive autocorrelation ESS estimate. ESS is diagnostic,
not a new automatic promotion guarantee; irregular spacing and sparse/constant errors limit it.
No distinct-chat count is invented for meter intervals without attributable chat labels.

The current direct short-horizon forecast already selected on matured non-overlapping outcomes before
this change. It was not waiting eight weeks. End-of-window bands still require completed compatible
cycles and retain the “near-reset proxy” caveat. Short-horizon evidence cannot prove weekly coverage.
The provisional 8-block calibration / 8-block composed-selection / 12-block asserted-selection
thresholds preserve the previous numerical safety floors in a different grouping unit; they are not
empirically optimal thresholds. Matched comparisons, precision/coverage gates and fallback remain.

## Product subtraction

- Deleted `OverviewPage` and its collector-bound `OverviewViewModel`. Current quota, even-burn
  explanation, workload history/nowcast and planning belong on Codex. Source failures, setup problems,
  refresh/cancel and collection events remain in Diagnostics/Settings rather than another overview.
- Extracted the still-used quota card presenter for the existing planner. Deleted unreferenced
  `PlaceholderPage`, overview-only card records and the unused synthetic `AgentNode` contract.
- Retained Forecasts/Model Lab: recent-pattern planning and detailed research comparisons are not
  fully covered by Codex. Retained Usage and Analytics for their detailed filters/history; native
  thread, source, rollout and database explorers remain diagnostics. They are not declared obsolete
  merely because a new navigation item exists.

Architecture regression tests reject runtime imports of Research, direct SQLite access in the product
engine and re-embedding collector state. Behavioral tests cover canonical ledger ownership and
within-cycle calibration, duplicate/overlap rejection, reset boundaries and future-label exclusion.

Validation: 545 Core tests passed; CLI and Windows app builds passed without warnings/errors.
The read-only real-history cost backtest constructed 2,035 separate cohort/horizon targets and 3,290
candidate rows, 120 with held-out outcomes. No single-cycle score in this capture contained multiple
six-hour blocks; the new capability is proven by fixtures, not a claimed empirical win on this capture.
No new model was promoted. The app build deployed/restarted the daily version; native visual
acceptance remains a separate user check.
