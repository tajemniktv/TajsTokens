# TajsTokens agent and architecture guide

This file is the repository-level source of truth for coding-agent instructions and the current architectural contract.

TajsTokens is a local-first native Windows observability application for Codex and AI coding-agent usage. The product is deliberately deeper than a token counter: it correlates subscription quota, token accounting, local Codex sessions/agents, context/compaction behavior, storage growth, forecasting and attribution while keeping ordinary conversation contents out of its telemetry store.

## Product state

The application currently has:

- a real-data WinUI 3 dashboard;
- native Codex local-history token accounting as the displayed/default accounting source;
- optional Tokscale reconciliation/fallback, disabled by default;
- provider-authoritative Codex quota through the local `codex app-server`;
- SQLite quota/history persistence;
- a process-lifetime telemetry coordinator shared by Overview, tray, alerts, Observatory and intelligence refresh;
- background collection, close-to-tray lifecycle, notifications, runtime settings, Start with Windows, and a self-contained Windows x64 portable publish path;
- direct incremental Codex rollout ingestion with privacy-safe session/root/subagent, token, quota, context/compaction, activity, and storage metadata;
- explicit local-only accounting coverage until remote/cloud-only Codex sessions have their own source;
- merged Phase 4 intelligence for persisted forecast history, reset/re-anchor events, bounded historical aggregates, interval-based quota attribution and account-local scenario estimation;
- real Usage, Forecasts and Analytics pages over normalized intelligence/query contracts.

Phase 3.5, Phase 4, and Phase 4.5 are merged. The empirical Windows profile confirmed the UI-thread hardening and state-indexed incremental ingestion. Phase 4.7 cuts displayed local-history accounting over to the native SQLite projection while retaining Tokscale as an opt-in reconciliation oracle/fallback. Native cutover does not magically imply account-global coverage: remote/cloud-only sessions remain a distinct source problem.

## Solution layout and dependency direction

### `TajsTokens.Core`

Owns provider-independent domain models, interfaces, accounting semantics, alerts, forecasting and inference semantics.

- Must not reference App or Infrastructure.
- Quota percentages and token counters are independent telemetry streams. Never invent a universal token-to-subscription-quota conversion.
- Provider adapters and native accounting must normalize inclusive counters into disjoint buckets so cache/reasoning are never double-counted.
- Attribution/reset/scenario models must encode uncertainty and keep observed facts distinct from inference.

### `TajsTokens.Infrastructure`

Implements Core contracts for SQLite, local Codex files/state, provider processes/RPC, settings, ingestion, process-lifetime coordination and historical intelligence queries.

- Depends on Core only.
- UI must not parse provider JSON, rollout JSONL, Codex-private SQLite, invoke Tokscale/Codex directly, or query SQLite directly.
- Prefer provider-owned/local read paths over copied credentials or undocumented web scraping.
- Large historical queries must be bounded/downsampled before crossing into App.
- Codex-private SQLite schemas are optional acceleration inputs, never public contracts. Validate recognized fingerprints and fail open to the rollout filesystem path when unknown/unavailable.

### `TajsTokens.App`

Windows composition/UI layer using WinUI 3 / Windows App SDK.

- May depend on Core + Infrastructure.
- Owns navigation, presentation, native Windows shell integration, and application lifecycle.
- UI code mutates XAML state only on the WinUI dispatcher; expensive provider/file/SQLite/drawing/analytics work belongs elsewhere.

### `TajsTokens.Core.Tests`

Regression suite for accounting, forecasting, reset detection, scenario planning, historical intelligence, provider contracts, settings/alerts, coordinator behavior, incremental ingestion, SQLite migrations, privacy, and threading/cancellation boundaries.

## Current data flow

```text
Codex local app-server quota ----------------------\
                                                     \
Codex state SQLite -> changed threads ---------------> TelemetryCoordinator -> immutable TelemetrySnapshot
                         |                           /       |                 |        |        |
                         v                          /        |                 |        |        +-> alerts
Codex rollout JSONL -> incremental ingestion ------+         |                 |        +----------> tray
                         |                         |         |                 +-------------------> Overview
                         v                         |         |
normalized native token events -> cached projection+         +-> Observatory refresh/status
                                                           |
                                                           +-> intelligence refresh
                                                                -> forecast history
                                                                -> reset/re-anchor events

Tokscale CLI (optional reconciliation/fallback) ---> native-first accounting policy

normalized history -------------------------------> SQLite
                                                       |
                                                       +-> bounded Usage/Forecasts/Analytics queries
```

Codex state SQLite is used only as a read-only discovery/metadata/reconciliation index when its private schema is recognized. Rollout JSONL remains authoritative for event-level accounting, quota/context/activity observations, and TajsTokens-owned byte-checkpoint semantics. Provider quota/interim telemetry may publish before a potentially long historical rollout import. Native accounting is projected only after the Observatory writer completes the current generation; failed/incomplete ingestion must keep token freshness stale rather than promoting a readable but incomplete SQLite projection as live. Intelligence is derived only after fresh normalized persistence and must fail independently of provider freshness.

## Execution, threading, and concurrency contract

`async` is not synonymous with background execution. Thread ownership must be intentional.

### WinUI dispatcher

Allowed:

- XAML property/collection mutation;
- navigation/focus work;
- tiny shell coordination that is proven cheap.

Not allowed:

- provider process/RPC calls;
- rollout/state discovery/read/parse;
- SQLite reads/writes;
- token/context aggregation;
- attribution/reset scans/scenario fitting;
- expensive icon/font rendering;
- large list transformation or analytics.

Any reproducible synchronous UI freeze above roughly 250 ms is a performance bug worth profiling. Normal dispatcher callbacks should usually stay below roughly 16-30 ms; navigation/selection should react within roughly 50 ms before asynchronous detail work begins.

### Provider I/O

Tokscale and Codex app-server operations run outside the caller's `SynchronizationContext`. They are cancellable and failures are isolated. A failed provider must not erase another provider's fresh data or make stale data look live. Tokscale is not invoked on the ordinary default accounting path.

### Telemetry coordinator

One process-lifetime coordinator is authoritative for startup, periodic, manual, resume, retry, and later event-driven refreshes.

- Refreshes are serialized.
- Manual refresh may supersede stale non-manual work.
- Provider/interim snapshots may publish before the longer Observatory import, then a final snapshot follows.
- Native token freshness requires both a successful accounting projection and a fresh Observatory generation; an ingestion error preserves the last complete displayed token generation as stale.
- Intelligence refresh runs only against fresh persisted normalized telemetry and its failure cannot invalidate provider/Observatory data.
- Snapshot consumers must tolerate multiple snapshots per refresh and reject stale asynchronous results.
- Subscriber callbacks must remain cheap; heavy subscriber work is queued/coalesced outside producer paths.

### Rollout ingestion pipeline

The intended shape is bounded and backpressured:

1. query a recognized Codex state catalog for changed/reconciliation-needed threads, or fall back to filesystem discovery;
2. read only complete JSONL records from selected rollouts at TajsTokens-owned byte checkpoints;
3. parse/normalize with limited concurrency only where semantic ordering permits;
4. serialize/batch durable SQLite mutations and accounting/checkpoint state;
5. publish aggregate progress.

Do not create arbitrary parallel SQLite writers. One intentional writer path per database/profile is preferred. Parsing may become parallel, but durable mutations/checkpoints remain ordered where accounting semantics depend on observation order.

If the same logical rollout path reappears with a different filesystem/source identity, retire its superseded native token/counter/parser/rollout generation atomically before activating the replacement. Same-identity replay remains idempotent.

### Cancellation and lifecycle

- App-lifetime cancellation owns background services.
- Page-lifetime cancellation owns page/detail queries.
- Superseded selection/query generations cannot overwrite current UI.
- Shutdown/sleep/navigation must not leave abandoned tasks or advance a durable source checkpoint past unapplied telemetry.
- Intentionally fire-and-forget tasks must have observed/classified exceptions.

## Performance contract

TajsTokens is itself an observability tool, so it should not become the workload being observed.

### Tray

- Cache badge/icon variants by visible quota state.
- Skip `Shell_NotifyIcon`/drawing work when the visible state and tooltip have not changed.
- Reuse expensive drawing resources safely.
- Coalesce bursty snapshot updates.
- Unchanged/cached tray handling should normally be around single-digit milliseconds rather than reconstructing fonts/icons on every snapshot.

### SQLite / rollout history

- A warm idle Observatory refresh with a recognized Codex state catalog should not recursively enumerate/open historical rollouts merely to rediscover EOF.
- Use `threads.updated_at_ms` plus bounded overlap/fingerprints for changed-thread selection; cumulative `threads.tokens_used` is reconciliation evidence, not detailed accounting.
- Prefer bounded transactions and prepared/reused operations over opening a connection/transaction for every normalized fragment when practical.
- Keep correctness-critical cumulative accounting state ordered.
- Advance checkpoints only after corresponding normalized writes have committed.
- The expensive native model/hour projection must be cached/incremental across unchanged warm refreshes. A writer-owned accounting revision plus cheap event-table shape metadata invalidates the cache; model/hour reads for one generation share one SQLite read transaction.
- WAL/synchronous configuration may be tuned only with an explicit durability rationale.
- Large first-run imports may take time, but remain progressive, cancellable, and non-blocking to UI/provider freshness.

### Historical intelligence

- Query/filter/aggregate at the SQLite layer rather than returning raw histories to XAML.
- Minute/hour/day resolution may be coarsened automatically to satisfy a bounded result budget.
- Quota-burn detail queries are scoped to the selected interval.
- Scenario fitting uses bounded historical intervals and never runs synchronously on the dispatcher.

### Large UI collections

- Query/filter/page at the repository layer instead of materializing arbitrary history into XAML.
- Prefer incremental/virtualized lists and on-demand detail tabs.
- Avoid replacing an entire `ItemsSource` for every background snapshot if rows can remain stable.

## Forecasting semantics

Forecasting is reset-window-aware, not generic linear extrapolation.

- Provider `resetsAt` + window duration/identity define a forecasting epoch.
- Do not carry a terminal slope across a reset/re-anchor.
- Show exhaustion ETA only when exhaustion is projected before the current authoritative reset.
- Otherwise report that the window survives, with projected remaining margin at reset.
- Compute sustainable pace from remaining quota/time to reset and compare observed pace against it as burn pressure.
- Treat rounded/flat meter samples as quantized uncertainty, not perfect evidence of exactly zero burn.
- Short-window and weekly estimators may use different horizons/strategies.
- Sparse/stale data lowers confidence or suppresses the forecast.
- Never claim token count deterministically predicts quota consumption.
- Phase 4 persists these complete forecast semantics so history cannot degrade to a less expressive model after restart.

## Phase 4 intelligence semantics

The detailed evidence contract lives in `docs/PHASE4_INTELLIGENCE.md`. These rules are architectural, not presentation suggestions.

### Evidence levels

Keep separate:

1. provider-observed quota facts;
2. locally observed normalized activity;
3. inferred attribution/reset classification/scenario estimates.

Do not phrase an inferred contributor score as provider-confirmed quota billing.

### Quota-burn intervals

- Build intervals only from adjacent positive quota movement within the same provider/profile/window/reset identity.
- Never bridge a reset/re-anchor boundary to manufacture burn.
- Correlate local activity in `(previous observation, current observation]`.
- Meter rounding/delay means attribution is interval-based, not event-exact.
- Root and subagent contribution remain inspectable separately and as aggregate activity.

The initial contributor heuristic is transparent and intentionally simple: 55% native token share, 25% uncached-input share, 15% cache-read share, 5% compaction share, each normalized within the selected interval. It is a ranking score, not a probability.

### Reset/re-anchor history

- Backend `resetsAt`/window identity wins over `previous reset + duration` arithmetic.
- Expected resets, rolling-window re-anchors, unusual-reset evidence and full-reset evidence are distinct classifications.
- Persist before/after meter state, previous/current reset timestamps, source, confidence and explanation.
- Event identity must be deterministic so repeated scans are idempotent.

### Scenario planning

- Use only this account/profile/window's observed quota movement and content-free workload context.
- Fit five-hour and weekly windows independently.
- Report sample count, expected movement, uncertainty/range and confidence.
- If history is insufficient, return `not enough data` rather than importing assumptions from another account/model/provider.
- Optional model/reasoning cohorts may refine estimates only when enough matching samples exist; fallback must reduce confidence explicitly.

## Information architecture

Top-level navigation is organized around user questions rather than data-source implementation details:

- **Overview**: quota health, reset-aware pace/forecast, current Codex work, recent important activity/anomalies, source health.
- **Usage**: bounded historical totals/time buckets, token classes, model/repository/agent-role breakdowns and heatmap-ready data.
- **Codex / Observatory**: sessions, agents, timeline, context/compactions, native accounting, rollout/storage diagnostics.
- **Forecasts**: short-window/weekly pacing, persisted forecast history/confidence and account-local scenario planning.
- **Analytics**: quota-burn microscope, estimated contributors, reset/re-anchor timeline and deeper cross-signal analysis.
- **Diagnostics**: sources, app-server/Tokscale/rollout/SQLite health, refresh/import progress, failures, doctor data, later incidents/announcements.
- **Settings**: general, collection, notifications, startup/background, providers, privacy/storage.

Overview is not a dumping ground for every new metric. High-signal summaries deep-link to detail surfaces.

### Observatory/session explorer

Use a scalable master-detail shape:

- master: search/filter/sort/group session/root trees, stable selection, virtualized/incremental loading;
- detail: focused Overview, Agents, Timeline, Context, Tokens/accounting, and Storage sections/tabs loaded on demand;
- never render every long timeline/context/storage domain simultaneously merely because the database can return it.

## Provider contracts

### Native Codex accounting

Native accounting is the default displayed source for local normalized Codex history.

- Project only TajsTokens-owned normalized disjoint token events; do not reinterpret provider quota as token accounting.
- Preserve model/hour generation coherence by reading both projections inside one SQLite read transaction.
- Do not rescan the complete token-event table on every unchanged warm refresh; reuse the revision-keyed projection cache.
- Report local-only coverage explicitly. Remote/cloud-only sessions are unknown until a remote source exists, never implicit zero.
- A successful SQLite read is not enough to call a generation live if the upstream Observatory refresh failed or reported ingestion errors.

### Tokscale

Tokscale is an optional reconciliation oracle and explicit fallback. Both uses are disabled by default.

- Never invoke Tokscale on the ordinary native accounting path.
- Consume documented machine-readable CLI output only.
- When Tokscale is enabled, prefer a global `tokscale`; supported `npx --yes tokscale@latest` fallback is allowed when the command is genuinely unavailable.
- Unsupported/malformed non-empty JSON is an error, not zero usage.
- A reconciliation failure cannot invalidate a healthy native generation.
- A Tokscale fallback must retain explicit fallback/stale provenance rather than masquerading as native accounting.
- Preserve source/version/provenance where available.

### Codex quota

Quota is read through short-lived local `codex app-server` initialization + `account/rateLimits/read` without a model turn.

- Provider percentages, window duration, and `resetsAt` are authoritative.
- Missing lanes remain unavailable or explicit stale fallback, never borrowed from another window.
- Rollout-embedded quota observations are useful high-frequency telemetry during active work and must retain source/event timestamp/window identity.

### Codex local state and rollouts

Codex-private state SQLite is an optional acceleration/index layer; direct rollout ingestion remains content-minimal and authoritative for event telemetry.

- Discover current `state*.sqlite` candidates without assuming one numbered filename is permanent.
- Open provider state read-only and use only recognized table/column fingerprints; unknown/unavailable state fails open to rollout filesystem discovery.
- `threads.updated_at_ms` is the primary cheap change trigger with a bounded overlap/recheck window.
- `threads.tokens_used` is a cumulative summary/reconciliation signal only. Never use it as a replacement for disjoint native accounting.
- Verified `thread_spawn_edges` is preferred for ordinary persisted topology; rollout relationship metadata remains repair/compatibility evidence.
- Absolute provider-owned rollout paths are ephemeral locators only. Persist privacy-safe hashes/labels, not the raw paths, in normal telemetry.
- Persist exact TajsTokens byte offsets only at complete successfully handled record/batch boundaries.
- Never substitute Codex thread-history/projection offsets for TajsTokens checkpoint ownership.
- Never checkpoint `FileInfo.Length` just because it was observed.
- Unterminated trailing JSONL is not a complete record.
- Source replacement/truncation/rotation and stable session identity are distinct concepts.
- Cumulative `total_token_usage` can reset inside one session stream; preserve counter epochs.
- Repeated cumulative observations contribute only new positive delta within an epoch.
- Cached input is included in input; reasoning output is included in output.
- Inherited child history must not become newly generated subagent usage.

## SQLite persistence

Base telemetry currently uses `%LOCALAPPDATA%\TajsTokens\telemetry.db`. `SqliteTelemetryRepository` owns the base `PRAGMA user_version`; the Codex Observatory, Codex state index, and Phase 4 intelligence use independent component-owned schema state in the same database.

Persist normalized telemetry and content-free identities, including quota/token history, session/agent relationships, normalized activity, context/compaction metadata, rollout storage metadata, parser/checkpoint state, privacy-safe Codex state change fingerprints, forecast snapshots and reset/re-anchor events.

Historical usage/attribution is primarily derived from normalized facts rather than duplicating raw activity into another analytics warehouse. The native accounting revision table is content-free cache invalidation metadata, not another token ledger.

Settings remain separate in `%LOCALAPPDATA%\TajsTokens\settings.json` and contain no auth material.

## Privacy boundary

Normal telemetry must not persist ordinary:

- prompt/message text;
- reasoning text;
- source-code bodies;
- shell commands/output;
- tool result payloads;
- credentials/auth material;
- raw rollout JSON;
- Codex state titles/previews/first messages;
- absolute provider-owned rollout paths.

Large content-bearing rollout records are transient parser input and are reduced to type/status/size/timing/identity metadata. Sanitized fixtures use hand-authored structure or deterministic filler, never copied personal rollout payloads.

Intelligence and ingestion consume normalized/content-minimal state. Do not create a parallel content-bearing cache for attribution, scenario fitting, or faster discovery.

## Build and validation

Authoritative Windows validation:

```text
dotnet build TajsTokens.sln
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj
```

Publish smoke additionally exercises the self-contained Windows x64 publish.

Non-Windows builds compile a placeholder App target only. Never claim the WinUI/XAML application is validated based solely on Linux/macOS compilation.

Every runtime bug involving rollout/accounting/privacy/checkpoint/intelligence semantics should gain a compact sanitized regression test/fixture. Performance tests should use broad deterministic workloads/benchmarks rather than flaky millisecond assertions in normal CI.

## Coding expectations

- Nullable reference types remain enabled.
- Persist UTC timestamps losslessly with explicit offsets/round-trip formatting.
- Keep provider/persistence/intelligence failures isolated and preserve last-known-good data with explicit stale provenance.
- Favor explicit ownership and bounded concurrency over scattering `Task.Run` or locks without a model.
- Keep changes incremental and testable, but do not preserve obsolete architecture merely because it already exists.
- Do not silently weaken accounting idempotence, source provenance, privacy boundaries, checkpoint durability, or evidence labels to make a benchmark or attribution score prettier.
