# TajsTokens delivery plan

**Updated:** 2026-09-08

**Stage:** product integration and dogfooding; the foundation reset is finished.

[PROJECT.md](PROJECT.md) owns product direction, source semantics, retention and architecture.
[AGENTS.md](AGENTS.md) owns contributor/build instructions. This plan records delivered work,
concrete remaining work and the next useful action—not another source specification.

## Acceptance standard: practical dogfood quality

**Correct under the conditions we've actually observed, conservative when uncertain, diagnosable
when wrong.** This is a solo local application, not a distributed database.

- Check the changed behavior with representative observed inputs and relevant failure cases.
  Reuse existing tests; do not re-audit unchanged paths or invent an exhaustive failure matrix.
- Run the normal Core tests and Windows build at a code milestone. Backtest material prediction
  changes, not labels, plumbing, or the same sparse historical dataset again.
- Give concrete data-loss, double-counting and privacy risks stronger attention. Keep unknown
  values explicit and failures understandable rather than silently substituting convenient data.
- A useful implementation can be done with documented limits. Reserved user visual feedback is
  a separate follow-up, not an indefinitely open implementation gate.
- Stop when supported behavior works and no known in-scope defect remains. New hardening work
  needs an observed problem or a specific product need.

## Next-agent handoff

1. **Read the current worktree, not an old commit checkpoint.** This delivery work is not committed;
   preserve existing changes. Do not reset the checkout or recreate the completed slices below.
2. **The former next steps are implemented:** alternate-rollout comparison, backend-account quota
   isolation, and explanatory quota/forecast UI. There is no pending alternate-file research task
   and no permission to automatically ingest, deduplicate or delete those files.
3. **Diagnostics is now a real page**, not a placeholder. It reuses the shared collector snapshot
   and refresh owner. The retention/recovery policy is explicit in `PROJECT.md` and Settings.
4. **Runtime/visual feedback remains with the user.** Use `-p:DogfoodEnabled=false` for isolated
   builds while that reservation applies. Do not launch/restart the app or migrate its live data.
   Temporary work stays under `.codex/temp`.
5. **Next action:** act on a concrete dogfood
   issue or requested feature. Do not reconstruct historical account ownership, rerun old
   forecasting studies without a reason, or turn the delivered checklist into a new audit.

### Existing owners to reuse

| Area | Owners |
| --- | --- |
| Current quota, shared refresh, freshness and alerts | `CodexAppServerQuotaProvider`, `TelemetryCoordinator`, `TelemetrySnapshot`, `QuotaAlertEngine` |
| Collection, source identity and checkpoints | `CodexSessionIngestionService`, `CodexRolloutParser`, observatory/semantic batch stores |
| State acceleration and alternate-file inspection | `CodexStateCatalog`, `CodexObservatoryService`, `CodexRolloutComparisonReader` |
| Native work navigation and detail | `ICodexThreadReadModel`, `CodexThreadObservabilityService`, `CodexThreadReadModelPolicy` |
| Burn history, forecasts, scenarios and evaluation | `SqliteIntelligenceService`, `SqliteForecastDatasetReader`, Core forecasting/scenario services |
| Product diagnostics | `TelemetryDiagnosticsPresenter`, `DiagnosticsPage`; no separate polling loop or incident database |
| Owned storage and deployment | `SqliteTelemetryRepository`, `AppDataLocation`, `RuntimeSettingsStore`, `tools/dogfood` |

## Delivered implementation

Checked items mean the stated implementation exists and has relevant automated/build evidence.
They do not claim exhaustive native-source coverage or user visual approval.

### 1. Source and storage boundaries

- [x] Map the current daily workflows to their native readers, owned evidence and read-model policies
  in `PROJECT.md`; distinguish direct content inspection from durable collection and export.
- [x] Preserve native identities, source generations, event/collection times, missing fields and
  alternatives in the supported paths. Mutable current state is not backfilled as historical context.
- [x] Separate response-reported backend-account quota from installation-wide workload. Retain only
  a versioned account pseudonym; legacy/ambiguous scope remains unknown.
- [x] Document retention and rebuildability by owned-data group. There is no automatic age-based
  cleanup or blanket safe-delete cache; active token/bookkeeping generations are retired on path
  replacement rather than promised as an immutable archive.

### 2. Selective collection and coverage

- [x] Collect content-free token/context/workload evidence through the existing rollout pipeline,
  with source-qualified identity and observation/checkpoint batch ownership.
- [x] Support normal append/replay and observed replacement/interruption cases without recounting
  completed records; preserve historical evidence when native files disappear.
- [x] Repair eligible historical effort metadata without changing token totals or inventing old
  collection timestamps.
- [x] Reconcile state fingerprints at startup, on selected-database change and after five minutes;
  changed paths are detected without reopening every unchanged rollout.
- [x] Report timestamped indexed/discovered path coverage without equating counts with unique work.
- [x] Provide bounded, cancellable alternate-rollout comparison under **Codex > Rollout coverage**:
  exact bytes, owned-stream equality, prefix overlap, divergence and unresolved inputs remain distinct.
  Inspection performs no owned writes or automatic alternate import.

Evidence includes `CodexSessionIngestionServiceTests`, state-index consistency/ingestion tests,
rollout/semantic batch tests, `CodexWorkloadEvidenceTests` and `CodexRolloutInspectionTests`.
The native comparison probe returned two identical pairs and six unresolved results on its first
bounded page without creating an owned database. That is a sample, not proof about every file.

### 3. Native work and accounting read models

- [x] Navigate workspaces, roots and recursive subagents with search, archive filtering and detail.
  Missing parents and cycles remain visible instead of becoming invented root relationships.
- [x] Keep current state, historical turns, realtime items and source alternatives distinct;
  expose selection rationale and bounded-read limitations.
- [x] Present supported native content through tolerant detail cards; unknown/malformed items remain
  inspectable rather than silently disappearing.
- [x] Use native rollout accounting as the default. Optional Tokscale comparison/fallback is explicit;
  state totals and overlapping rollout counters are not added together.
- [x] Reuse native read-model/presentation owners rather than creating a parallel generic schema.

Evidence includes thread navigation, observability, presentation and native accounting tests.
Raw explorers and the explicitly invoked CLI harness remain available for investigation.

### 4. Everyday quota and diagnostics UI

- [x] Overview shows reported windows, remaining quota, reset/pace outlook, freshness and omission.
  Same-time alternate observations cannot borrow another anchor's freshness or forecast.
- [x] Keep account/source/reset streams separate in Quota Burn, forecast history, alerts, scenarios
  and evaluation. Co-observed local activity is not presented as account membership or causal cost.
- [x] Distinguish saved outlooks from current quota; keep history selection stable across density/filter
  changes and label the saved account scope.
- [x] Explain withheld forecasts and unsupported scenarios using their actual reason. Missing current
  account scope requests a fresh quota read; one-window scenario support is not called a total failure.
- [x] Put Overview forecast methodology behind an evidence expander, leaving the main message concise.
- [x] Replace the Diagnostics placeholder with source health, per-window forecast explanations,
  bounded recent events, shared refresh/cancellation, and links to coverage, native sources and Settings.
  Opening Diagnostics does not trigger acquisition or export.
- [x] Cover relevant waiting, omitted, stale, unavailable and error states in shared presentation tests;
  preserve user-owned visual feedback as a separate follow-up.

### 5. Quota intelligence

- [x] Persist authoritative quota observations with source/account scope and isolate reset generations.
  A current forecast cannot borrow legacy unknown-account or another account's history.
- [x] Evaluate five-hour and weekly streams separately with chronological outcomes and sensible
  baselines, including sparse, stale, quantized and reset/re-anchor behavior.
- [x] Report fitted origins, independent reset generations, remaining-quota errors, available
  exhaustion/ETA labels and interval coverage; keep unsupported confidence claims unavailable.
- [x] Use the evaluated production policy. Retain the simpler baseline where historical results do
  not justify promotion; no advanced-model implementation is required merely because it is possible.
- [x] Keep scenarios within observed support and separate local workload features from account identity.
  Learning/unknown states are intentional when usable evidence is insufficient.

The dated experiments and model policy are in `PROJECT.md`. Newly scoped accounts need new
observations. More independent outcomes are an evidence dependency, not unfinished algorithm code.

### 6. Storage, recovery and local operation

- [x] Use the owned database and stable data folder; migrations preserve legacy evidence, and the
  account-scope migration has representative rollback/retry coverage.
- [x] Keep collection/query work off the UI thread, support cancellation and bounded reads, and publish
  quota independently of slower background intelligence work.
- [x] Publish normal local builds through the existing dogfood deployment workflow with stopped-app
  backups, retained binary generations, startup acknowledgement and rollback. CI/test-only builds skip it.
- [x] Document explicit stopped-app data recovery in `README.md`; binary rollback does not downgrade
  the live database. Preserve the current data directory before restoring a matching backup.
- [x] Keep exports explicit and separate from raw local inspection. Diagnostics adds no export/upload path.
- [x] Expose data paths, retention posture, source failures and non-destructive recovery guidance.

Existing deployment evidence includes replacement/rollback and migration checks documented in
`README.md` and earlier task results. This task does not reimplement or repeat deployment.

## Validation and remaining follow-ups

**Current milestone:** all 291 Core tests pass, including four new Diagnostics presentation cases.
The isolated Windows solution build passes with zero warnings/errors (`DogfoodEnabled=false`),
and `git diff --check` is clean. The evaluation tool built successfully in the preceding account-scope
milestone; this follow-up changes no prediction algorithm. The installed app and live database
were not replaced, restarted or migrated.

- [ ] User dogfood/visual feedback on the updated quota, coverage and Diagnostics surfaces.
  The implementation is not blocked by this reserved pass; do not claim it has happened.
- [ ] Observe new account-scoped history before reconsidering model promotion or calibrated bands.
  Do not attach older unknown-account data to the current account to make learning disappear.

There are no additional known implementation tasks hidden behind the old handoff instructions.
If validation or dogfooding reveals a concrete defect, record and fix that defect here. Performance
budgets, broader recovery tooling, additional durable sources and automatic retention are not
implicit requirements; add them when an observed need justifies the work.
