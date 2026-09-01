# TajsTokens agent and architecture guide

This file is the repository-level source of truth for coding-agent instructions and the current architectural contract.

TajsTokens is a local-first native Windows observability application for Codex and AI coding-agent usage. The product is deliberately deeper than a token counter: it correlates subscription quota, token accounting, local Codex sessions/agents, context/compaction behavior, storage growth, and later forecasting/attribution while keeping ordinary conversation contents out of its telemetry store.

## Product state

The application currently has:

- a real-data WinUI 3 dashboard;
- Tokscale-backed broad token accounting;
- provider-authoritative Codex quota through the local `codex app-server`;
- SQLite quota/history persistence;
- a process-lifetime telemetry coordinator shared by Overview, tray, alerts, and Observatory;
- background collection, close-to-tray lifecycle, notifications, runtime settings, Start with Windows, and a self-contained Windows x64 portable publish path;
- direct incremental Codex rollout ingestion with privacy-safe session/root/subagent, token, quota, context/compaction, activity, and storage metadata;
- native Codex accounting in shadow/reconciliation mode while Tokscale remains the displayed/default broad accounting source.

Phase 3.5 hardens runtime performance, concurrency/cancellation, information architecture, scalable session exploration, and reset-aware forecast semantics before Phase 4 adds deeper analytics.

## Solution layout and dependency direction

### `TajsTokens.Core`

Owns provider-independent domain models, interfaces, accounting semantics, alerts, and analytics/forecasting logic.

- Must not reference App or Infrastructure.
- Quota percentages and token counters are independent telemetry streams. Never invent a universal token-to-subscription-quota conversion.
- Provider adapters and native accounting must normalize inclusive counters into disjoint buckets so cache/reasoning are never double-counted.

### `TajsTokens.Infrastructure`

Implements Core contracts for SQLite, local Codex files, provider processes/RPC, settings, ingestion, and process-lifetime coordination.

- Depends on Core only.
- UI must not parse provider JSON, rollout JSONL, invoke Tokscale/Codex directly, or query SQLite directly.
- Prefer provider-owned/local read paths over copied credentials or undocumented web scraping.

### `TajsTokens.App`

Windows composition/UI layer using WinUI 3 / Windows App SDK.

- May depend on Core + Infrastructure.
- Owns navigation, presentation, native Windows shell integration, and application lifecycle.
- UI code mutates XAML state only on the WinUI dispatcher; expensive provider/file/SQLite/drawing/analytics work belongs elsewhere.

### `TajsTokens.Core.Tests`

Regression suite for accounting, forecasting, provider contracts, settings/alerts, coordinator behavior, incremental ingestion, SQLite migrations, privacy, and threading/cancellation boundaries.

## Current data flow

```text
Tokscale CLI --------------------------\
                                        \
Codex local app-server quota ------------> TelemetryCoordinator -> immutable TelemetrySnapshot
                                          /       |                 |        |        |
Codex local rollout JSONL -> ingestion --/        |                 |        |        +-> alerts
                                                   |                 |        +----------> tray
                                                   |                 +-------------------> Overview
                                                   +-------------------------------------> Observatory refresh signal

normalized history -------------------------------> SQLite
```

Provider telemetry is published before a potentially long historical rollout scan. A first-run import must never keep quota/Tokscale blank merely because local history is large.

## Execution, threading, and concurrency contract

`async` is not synonymous with background execution. Thread ownership must be intentional.

### WinUI dispatcher

Allowed:

- XAML property/collection mutation;
- navigation/focus work;
- tiny shell coordination that is proven cheap.

Not allowed:

- provider process/RPC calls;
- rollout discovery/read/parse;
- SQLite reads/writes;
- token/context aggregation;
- expensive icon/font rendering;
- large list transformation or analytics.

Any reproducible synchronous UI freeze above roughly 250 ms is a performance bug worth profiling. Normal dispatcher callbacks should usually stay below roughly 16-30 ms; navigation/selection should react within roughly 50 ms before asynchronous detail work begins.

### Provider I/O

Tokscale and Codex app-server operations run outside the caller's `SynchronizationContext`. They are cancellable and failures are isolated. A failed provider must not erase another provider's fresh data or make stale data look live.

### Telemetry coordinator

One process-lifetime coordinator is authoritative for startup, periodic, manual, resume, retry, and later event-driven refreshes.

- Refreshes are serialized.
- Manual refresh may supersede stale non-manual work.
- Provider/interim snapshots may publish before the longer Observatory import, then a final snapshot follows.
- Snapshot consumers must tolerate multiple snapshots per refresh and reject stale asynchronous results.
- Subscriber callbacks must remain cheap; heavy subscriber work is queued/coalesced outside producer paths.

### Rollout ingestion pipeline

The intended shape is bounded and backpressured:

1. discover/read complete JSONL records;
2. parse/normalize with limited concurrency only where semantic ordering permits;
3. serialize/batch durable SQLite mutations and accounting/checkpoint state;
4. publish aggregate progress.

Do not create arbitrary parallel SQLite writers. One intentional writer path per database/profile is preferred. Parsing may become parallel, but durable mutations/checkpoints remain ordered where accounting semantics depend on observation order.

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

- Prefer bounded transactions and prepared/reused operations over opening a connection/transaction for every normalized fragment when practical.
- Keep correctness-critical cumulative accounting state ordered.
- Advance checkpoints only after corresponding normalized writes have committed.
- WAL/synchronous configuration may be tuned only with an explicit durability rationale.
- Large first-run imports may take time, but remain progressive, cancellable, and non-blocking to UI/provider freshness.

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

## Information architecture

Top-level navigation is organized around user questions rather than data-source implementation details:

- **Overview**: quota health, reset-aware pace/forecast, current Codex work, recent important activity/anomalies, source health.
- **Usage**: totals, time series, token classes, models, repositories/workspaces, providers/accounts.
- **Codex / Observatory**: sessions, agents, timeline, context/compactions, native accounting, rollout/storage diagnostics.
- **Forecasts**: short-window/weekly pacing, confidence, history, and later scenario planning.
- **Diagnostics**: sources, app-server/Tokscale/rollout/SQLite health, refresh/import progress, failures, doctor data, later incidents/announcements.
- **Settings**: general, collection, notifications, startup/background, providers, privacy/storage.

Overview is not a dumping ground for every new metric. High-signal summaries deep-link to detail surfaces.

### Observatory/session explorer

Use a scalable master-detail shape:

- master: search/filter/sort/group session/root trees, stable selection, virtualized/incremental loading;
- detail: focused Overview, Agents, Timeline, Context, Tokens/accounting, and Storage sections/tabs loaded on demand;
- never render every long timeline/context/storage domain simultaneously merely because the database can return it.

## Provider contracts

### Tokscale

Tokscale is the bootstrap/default broad accounting provider while native Codex accounting matures.

- Consume documented machine-readable CLI output only.
- Prefer a global `tokscale`; supported `npx --yes tokscale@latest` fallback is allowed when the command is genuinely unavailable.
- Unsupported/malformed non-empty JSON is an error, not zero usage.
- Preserve source/version/provenance where available.

### Codex quota

Quota is read through short-lived local `codex app-server` initialization + `account/rateLimits/read` without a model turn.

- Provider percentages, window duration, and `resetsAt` are authoritative.
- Missing lanes remain unavailable or explicit stale fallback, never borrowed from another window.
- Rollout-embedded quota observations are useful high-frequency telemetry during active work and must retain source/event timestamp/window identity.

### Codex rollouts

Direct rollout ingestion is content-minimal and incremental.

- Persist exact byte offsets only at complete successfully handled record/batch boundaries.
- Never checkpoint `FileInfo.Length` just because it was observed.
- Unterminated trailing JSONL is not a complete record.
- Source replacement/truncation/rotation and stable session identity are distinct concepts.
- Cumulative `total_token_usage` can reset inside one session stream; preserve counter epochs.
- Repeated cumulative observations contribute only new positive delta within an epoch.
- Cached input is included in input; reasoning output is included in output.
- Inherited child history must not become newly generated subagent usage.

## SQLite persistence

Base telemetry currently uses `%LOCALAPPDATA%\TajsTokens\telemetry.db`. `SqliteTelemetryRepository` owns the base `PRAGMA user_version`; the Codex Observatory has its own component schema version in the same database.

Persist normalized telemetry and content-free identities, including quota/token history, session/agent relationships, normalized activity, context/compaction metadata, rollout storage metadata, parser/checkpoint state, and forecast snapshots.

Settings remain separate in `%LOCALAPPDATA%\TajsTokens\settings.json` and contain no auth material.

## Privacy boundary

Normal telemetry must not persist ordinary:

- prompt/message text;
- reasoning text;
- source-code bodies;
- shell commands/output;
- tool result payloads;
- credentials/auth material;
- raw rollout JSON.

Large content-bearing rollout records are transient parser input and are reduced to type/status/size/timing/identity metadata. Sanitized fixtures use hand-authored structure or deterministic filler, never copied personal rollout payloads.

## Build and validation

Authoritative Windows validation:

```text
dotnet build TajsTokens.sln
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj
```

Release smoke additionally exercises the self-contained Windows x64 publish.

Non-Windows builds compile a placeholder App target only. Never claim the WinUI/XAML application is validated based solely on Linux/macOS compilation.

Every runtime bug involving rollout/accounting/privacy/checkpoint semantics should gain a compact sanitized regression test/fixture. Performance tests should use broad deterministic workloads/benchmarks rather than flaky millisecond assertions in normal CI.

## Coding expectations

- Nullable reference types remain enabled.
- Persist UTC timestamps losslessly with explicit offsets/round-trip formatting.
- Keep provider/persistence failures isolated and preserve last-known-good data with explicit stale provenance.
- Favor explicit ownership and bounded concurrency over scattering `Task.Run` or locks without a model.
- Keep changes incremental and testable, but do not preserve obsolete architecture merely because it already exists.
- Do not silently weaken accounting idempotence, source provenance, privacy boundaries, or checkpoint durability to make a benchmark prettier.
