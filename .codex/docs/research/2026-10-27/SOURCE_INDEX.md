# Accounting, forecasting and TT research evidence

Preserved 27 October 2026 for the user's implementation-planning request.
[PROJECT.md — Current work](../../../../PROJECT.md#current-work) is the only delivery
checklist. This index records provenance, source findings and their existing owners;
it is not another specification or work queue.

## Supplied sources and authority

The user requested an implementation plan and a checklist, explicitly distinguishing
instructions in attached documents from that request. Recommendations, historical
agreements, proposed experiments and reproduction commands inside these files are
source material. They do not independently authorize implementation, paid inference,
deployment, uploads, provider calls, remote operations or model promotion.

Both original files were copied byte-for-byte from `D:/TajemnikTV/Downloads/`.
SHA-256 was compared with the originals. They are retained unchanged alongside this
index, including their original language and external references.
The local `.gitattributes` disables line-ending conversion for the supplied memo,
so Git checkout preserves its recorded byte identity on Windows as well.

| Source | SHA-256 |
|---|---|
| [Consolidated memo](TajsTokens-accounting-forecasting-TT-research-2026-10-27.md) | `e405c49f98d6f5acd9f88f1ab77e35b65fcf3fac9469a7b68f674798228440d9` |
| [Original research ZIP](TajsTokens-model-research-2026-10-27.zip) | `9848199d95ef710893fa8c542d61b9d3a61af20da7af10d1480f75eadaae1f34` |

ZIP contents were read without executing them: `README.md`, `RESEARCH.md`,
`model_checks.py`, `wolfram_checks.wl`, and `check_results.json`, all under
`tajstokens-model-research/`. The ZIP's `RESEARCH.md` SHA-256 is
`d8239a85375da366bf0a0e6531e4a542dd45add8e902d7fae7105eb029c62a4d`,
matching the digest declared for the memo's preserved appendix. The appendix and
ZIP research are the same research source, not independent empirical corroboration.

The Python file requires Python 3.10+, NumPy and SciPy and writes its JSON result
beside itself when run. Any later reproduction should extract to `.codex/temp/`
and retain the supplied archive unchanged. Its synthetic examples verify algebra,
not a fitted production model. Python/Wolfram execution and the retained successful
result are the original author's claims; they were not rerun during planning.

## Verification boundary

Local `main` was clean at `e609aebee78712dd26c2eae040005e6dcf2377f3`, matching the
memo/addendum's reviewed TajsTokens revision. Targeted inspection confirmed the
following local source mechanisms. This is not an exhaustive implementation audit.

| Finding | Current source checked | Interpretation |
|---|---|---|
| Plain rollout discovery/reading and physical checkpoint hashing | [CodexObservatoryService](../../../../src/TajsTokens.Infrastructure/Services/CodexObservatoryService.cs), [reader](../../../../src/TajsTokens.Infrastructure/Ingestion/FileSystemCodexSessionEventProvider.cs), [ingestion](../../../../src/TajsTokens.Infrastructure/Ingestion/CodexSessionIngestionService.cs) | Compression needs coordinated discovery/decoding/identity/checkpoint changes; adding a stream alone is insufficient. |
| Existing response and provider-usage integration | [parser](../../../../src/TajsTokens.Infrastructure/Ingestion/CodexRolloutParser.cs), [app-server provider](../../../../src/TajsTokens.Infrastructure/Providers/CodexAppServerEvidenceProvider.cs) | Extend/reconcile existing owners instead of adding a second collector. Installed support remains unverified. |
| Observatory component 9 | [store](../../../../src/TajsTokens.Infrastructure/Persistence/SqliteCodexObservatoryStore.cs), [schema tests](../../../../tests/TajsTokens.Core.Tests/ObservatorySchemaCompatibilityTests.cs) | Compatible source and regression coverage exist; installed ingestion recovery is a distinct open gate. |
| Defective reset fallback remains selected by default | [calibration](../../../../src/TajsTokens.Core/Services/Forecast/QuotaForecastCalibration.cs), [incident evidence](../../../../docs/source-notes/WEEKLY_PACE_INVESTIGATION.md) | PACE-001 is reproduced/documented, not repaired. Existing elapsed-time alternatives are candidates, not already-proven replacements. |
| Combined missing-workload gate | [threshold study](../../../../src/TajsTokens.Core/Research/ForecastThresholdStudy.cs) | No token events, no recognized role counts and unknown-role sessions share one reason; the reported 67 need a dataset-specific breakdown. |
| Joint model/category information is absent from cost features | [accounting model](../../../../src/TajsTokens.Core/Services/Accounting/QuotaAccountingModel.cs) | Pooled categories plus separate model/effort masses cannot reconstruct arbitrary joint pairings. |
| Five-category immutable TT research basis | [TtWorkloadBasis](../../../../src/TajsTokens.Core/Models/TtWorkloadBasis.cs) | Reuse identity/normalization patterns; public model-specific tariff scoring needs a new contract. The two scoring overloads have different support checks. |
| Main-process cap, forced blocking GC and policy retry hazard | [WindowsResourcePolicy](../../../../src/TajsTokens.App/Services/WindowsResourcePolicy.cs), [App](../../../../src/TajsTokens.App/App.xaml.cs) | Concrete mechanisms warrant repair/measurement; this inspection does not establish live pause lengths or native failures. |
| Historical completion text drives repeated side effects | [App](../../../../src/TajsTokens.App/App.xaml.cs) | A generation-specific trigger/coalescing slice is appropriate; runtime query cost still needs measurement. |
| Broad accounting revision and ordered per-token writes | [batch writer](../../../../src/TajsTokens.Infrastructure/Persistence/SqliteCodexSemanticBatchWriter.cs) | Narrow invalidation; preserve reducer/checkpoint safety. P-01 batching remains measurement-led. |
| Calibration cooldown does not establish changed evidence | [CalibrationProcessService](../../../../src/TajsTokens.Infrastructure/Services/CalibrationProcessService.cs) | Keep one bounded worker; qualify preparation/replay reuse by evidence and time semantics. |
| Saturated outcomes excluded by ordinary bounded-delta loss | [observation builder](../../../../src/TajsTokens.Core/Services/Accounting/QuotaCostObservationBuilder.cs) | Censoring-aware evaluation is later work; exclusion from this loss is not proof of deleted evidence. |

No application, Core tests, C# build, telemetry replay, installed native data,
profiling session, provider response, screenshot workflow or remote device was
examined/executed for this plan. External publications, provider policy numbers and
upstream Codex behavior are attributed to the dated supplied research, not newly
verified current facts. The memo pins upstream Codex to
`3b600d056650850c1d2cfca7a86c4079cc538e7a`; deployment must compare actual installed
capabilities rather than assume that upstream version is installed.

Existing [resource measurements](../../BACKGROUND_RESOURCES.md),
[threshold study](../../../../docs/source-notes/FORECAST_THRESHOLD_STUDY.md),
[pace investigation](../../../../docs/source-notes/WEEKLY_PACE_INVESTIGATION.md),
[ownership audit](../../../../docs/source-notes/INTELLIGENCE_OWNERSHIP_AUDIT.md) and
[audit register](../../AUDIT.md) remain dated evidence. Prior test counts and sampled
latencies were not rerun and cannot close the new runtime/visual gates.

## Research-to-plan crosswalk

The checklist IDs below refer to PROJECT.md, where status is maintained.

| Research area | Planned IDs / stage | Existing implementation to preserve |
|---|---|---|
| Memo §11: scheduling, GC, restore and completion side effects | PERF-001–004; stage 1 | UI/tray ownership, lossless alerts, bounded calibration child |
| Memo §11: retry amplification, writes, invalidation, preparation, queries | PERF-005–008, P-01; stage 1 | Indexed limits, matching-file skips, prefix safety, WAL, fit cache, full polling delay |
| Memo §§2–3, 10A; addendum §§3–5 | A-01, EVID-001/002, ING-001/002; stage 2 | Native reducer, batch writer, source timestamps and schema refusal |
| Memo §4 and optional-evidence table | EVID-003/004; stage 2 | Existing structured support reasons and collection-time eligibility |
| Memo §5, PACE-001; addendum §5 | PACE-001–004; stage 3 | Existing pace competitors and reset/fixed-horizon distinction |
| Memo §§6, 12.1–12.2; addendum §§4, 7–8 | SCORE-001–003, T-01, D-01; stage 4 | Original token vector, immutable research artifacts, basis IDs |
| Memo §6 catalogs | CAT-001/002, DEC-002/005; stage 4 | Existing metadata and API-equivalent cost owners |
| Memo §§7, 12; addendum §§4–8, 12 | CAL-001–004; stage 5 | Nonnegative interval accounting, pooled baselines, chronology |
| Memo §§5, 7, 9, 10E–F; addendum §§9, 12–13 | FORE-001–005; stage 6 | Workload/cost/forecast owners, single Planning flow, shared product snapshot |
| Addendum §§9–11; memo §12.4 | NEXT-001–004; stage 7 | Separate activity/continuation meanings, source-qualified window policies, regime owner |
| Memo §6 state-space; addendum §§5–6, 12 | NEXT-005; stage 7 | Simpler competitors; offline/bounded experiment boundary |
| Memo §9 and §11E; addendum §12 | SHIP-001–004; every slice | Reproducible operational replay, separate installed and visual acceptance |

## Dispositions and unresolved choices

Adopt as implementation direction: repair existing owners; retain rich, reconciled
evidence; use answer-specific support; preserve joint model/category quantities;
define a frozen public TT ruler; keep direct/composed routes and one product snapshot.

The recommended first public convention is tariff-reference TT. Exact schedule,
weights/basket, speed treatment, pricing feed and basis-extension/activation policy
remain decisions. Context/estimator defaults and numerical performance budgets
require targeted evidence. A tariff-derived TT is proportional to fixed-vintage
valuation: it provides stable units, not additional predictive information.

Stage-7 statistical proposals are conditional research, not promised production
features. A generic solver on every tray update, universal latent TT, neural model,
new framework/service, paid contrast experiments, complete remote accounting and
execution-stop control are not adopted by this plan. Current evidence can support
some answers without resolving all these questions.

The existing evaluator ID `tt-lab/v4-blocks` identifies the research procedure/block
aggregation. `tt-category-basis/v1` identifies its five-category basis semantics.
Neither names an approved public joint-coordinate TT unit. Archived v3 results
remain dated v3 results.
