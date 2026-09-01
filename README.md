# TajsTokens

TajsTokens is a local-first native Windows observability/control-center app for Codex and AI coding-agent usage.

The current product combines:

- provider-authoritative Codex subscription quota and reset timestamps from the local `codex app-server` without creating a model turn;
- native local Codex token totals/cache/output/reasoning breakdowns and hourly history projected from normalized rollout telemetry;
- optional Tokscale reconciliation/fallback, disabled by default;
- direct incremental Codex rollout ingestion for privacy-safe session/root/subagent, token, context/compaction, activity and storage telemetry;
- one process-lifetime telemetry coordinator shared by Overview, tray, alerts, Observatory and the intelligence refresh path;
- last-known-good provider data with explicit stale/unavailable states;
- background collection, close-to-tray lifecycle, Start with Windows, native notification-area status and deduplicated quota/provider alerts;
- SQLite quota/history and normalized Observatory persistence under `%LOCALAPPDATA%\TajsTokens`;
- reset-aware quota forecasting with sustainable pace, burn pressure, confidence, quantized-meter uncertainty and margin-at-reset;
- Phase 4 intelligence foundations: persisted forecast history, reset/re-anchor events, bounded historical aggregates, interval-based quota-burn correlation and account-local workload scenarios;
- native Usage, Forecasts and Analytics surfaces over normalized/query-bounded data contracts;
- a self-contained Windows x64 portable publish smoke artifact.

Native Codex accounting is the default **local-history** accounting source. It intentionally does not claim account-global coverage: remote/cloud-only Codex sessions remain outside that total until a remote source exists. Tokscale is retained as an optional comparison oracle and explicit fallback rather than a mandatory process on every refresh.

## Projects

- `src/TajsTokens.App` - WinUI 3 desktop shell, native Windows surfaces and composition root
- `src/TajsTokens.Core` - provider-independent domain models, accounting/alert/forecast/intelligence services and interfaces
- `src/TajsTokens.Infrastructure` - SQLite/settings persistence, local providers, rollout ingestion, telemetry coordination and historical intelligence queries
- `tests/TajsTokens.Core.Tests` - accounting, persistence, provider, ingestion, concurrency, intelligence and privacy regressions

Repository-level coding-agent instructions **and** the current architecture/threading/privacy contract live in [`AGENTS.md`](AGENTS.md). `ROADMAP.md` describes the working-product phase plan, `PERFORMANCE.md` records the Phase 3.5 runtime budgets/profiling baseline, and [`docs/PHASE4_INTELLIGENCE.md`](docs/PHASE4_INTELLIGENCE.md) defines the Phase 4 evidence/attribution semantics.

## Prerequisites

- .NET SDK 10.0+ for development builds
- Windows 11 (or Windows 10 19041+) for launching the WinUI app
- an externally executable Codex CLI for live app-server quota reads: on `PATH`, in a supported user-local Codex install location, or selected with `CODEX_CLI_PATH`

Tokscale and Node.js/npm are **not required for normal collection**. They are needed only if Tokscale reconciliation or Tokscale fallback is enabled. When enabled, TajsTokens prefers a global `tokscale` command and can use `npx --yes tokscale@latest` when Node.js/npm is available.

Codex Desktop may contain a packaged/private Codex runtime while exposing no `codex` command to ordinary desktop processes. TajsTokens deliberately does not execute protected `WindowsApps` package resources directly.

TajsTokens does not install or modify Codex or Tokscale automatically. The optional npx Tokscale path uses npm's normal package cache and can be slower on its first invocation.

## Build

The authoritative full build is Windows because only Windows compiles the WinUI/XAML application:

```powershell
dotnet restore TajsTokens.sln
dotnet build TajsTokens.sln -c Debug
```

On non-Windows hosts the solution intentionally compiles Core, Infrastructure, tests and a placeholder App assembly only. A green Linux/macOS build does **not** validate WinUI/XAML.

## Run tests

```powershell
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
```

## Run the app (Windows)

The application remains unpackaged (`WindowsPackageType=None`) for local development:

```powershell
dotnet run --project src/TajsTokens.App/TajsTokens.App.csproj
```

TajsTokens starts one background telemetry loop for the process. Closing the dashboard hides it by default rather than terminating collection; use the tray **Exit** action for a full shutdown. The tray can reopen the dashboard or trigger an immediate refresh.

The default poll interval is 60 seconds. Runtime settings are normalized in `%LOCALAPPDATA%\TajsTokens\settings.json`; corrupt/missing settings fall back to safe defaults. Start-with-Windows uses the current-user `Run` registry entry and is intentionally rejected for `dotnet run`/`dotnet.exe` development sessions because that host path would not identify the project to launch.

## Real-data integrations

### Native Codex accounting

The default accounting path projects TajsTokens-owned normalized Codex token events from SQLite into model totals and hourly buckets. Model/hour projections are read from one SQLite generation, and unchanged warm refreshes reuse a cached projection keyed by a writer-owned accounting revision plus cheap event-table metadata instead of repeatedly rescanning the entire history.

The accounting source is explicitly **local normalized Codex rollout history**. Missing remote/cloud-only sessions are unknown coverage, not assumed zero.

### Tokscale (optional)

Tokscale is retained as an optional reconciliation oracle and explicit fallback. Both settings are disabled by default, so ordinary refreshes do not launch Tokscale or `npx`.

When reconciliation is enabled, TajsTokens compares the native model/hour generation with Tokscale's documented machine-readable CLI output while continuing to display native data. When fallback is enabled, Tokscale may be used only if native accounting cannot be read.

The supported Tokscale calls are currently equivalent to:

```powershell
tokscale models --json --group-by client,model --client codex
tokscale hourly --json --client codex
```

When `tokscale` is not globally available and Node.js/npm is installed, the optional zero-install path uses:

```powershell
npx --yes tokscale@latest models --json --group-by client,model --client codex
npx --yes tokscale@latest hourly --json --client codex
```

A failed optional reconciliation does not invalidate a healthy native generation. An explicitly enabled fallback retains fallback provenance rather than pretending to be native/live accounting.

### Codex quota

TajsTokens starts a short-lived local Codex app-server, performs the initialization handshake and calls `account/rateLimits/read`. Quota windows are identified by provider-reported duration/window metadata rather than assuming `primary` always means five-hour. Provider `resetsAt` is authoritative.

Quota reads never create a model turn. A partial provider response cannot silently substitute one lane for another, and a failed refresh preserves last-known-good values with stale provenance.

Direct rollout ingestion also captures embedded quota observations during active model work. Those retain event timestamp/window/source provenance and are distinct from the independently polled app-server source.

### Codex rollouts / Observatory

The Observatory discovers local active/archive rollout JSONL, resumes from exact complete-record byte checkpoints, and normalizes only telemetry needed by the product.

Current normalized domains include:

- stable sessions/root/subagent relationships;
- model/reasoning metadata;
- native cumulative-token deltas with counter-reset epochs and inherited-child-history exclusion;
- quota observations;
- content-free activity/timeline events;
- context-window observations and compactions;
- rollout file/record-size metadata.

Phase 3.5 batches high-volume `rollout_records` metadata at durable checkpoint boundaries instead of using one SQLite transaction/file-upsert per JSONL record. Semantic accounting writes remain ordered and replay-safe. If a logical rollout path is replaced under a different filesystem identity, the superseded native token/counter generation is retired before the replacement becomes active so both generations cannot be counted together.

The Observatory UI is a master-detail explorer: search/select sessions on the left, then load Overview, Agents, Timeline, Context, Tokens or Storage detail on demand rather than rendering every domain at once.

## Forecast and intelligence semantics

Forecasts are scoped to the provider's current quota-window/reset identity.

- A slope from the previous reset epoch is not carried into a re-anchored window.
- Exhaustion ETA is shown only when exhaustion is projected **before** the current authoritative reset.
- Otherwise the useful result is `survives reset` plus projected remaining margin at reset.
- Sustainable pace is remaining quota divided by remaining reset time; burn pressure compares recent observed pace with that sustainable pace.
- Flat rounded provider samples mean `flat within meter precision`, not confident exact-zero burn.
- Sparse/stale data lowers confidence or suppresses forecasting.
- Phase 4 persists those reset-aware forecasts during the shared telemetry refresh so forecast history/baselines exist across restarts.

Phase 4 also derives **quota-burn intervals** only between adjacent provider observations that belong to the same provider/profile/window identity and whose used percentage increased. Those intervals can be correlated with native token activity, root/subagent participation, model/reasoning metadata and compactions. The provider-observed meter change is a fact; the contributor ranking is an estimate because subscription meters can be rounded or delayed.

Reset history uses provider identity changes and before/after meter observations. Expected reset, rolling-window re-anchor and unusual/full-reset evidence have explicit classifications/confidence. Backend `resetsAt` wins over locally predicted schedules.

The scenario planner learns only from this account's historical quota-drop intervals and workload concurrency. It returns ranges, sample counts and confidence, or an explicit insufficient-history state. TajsTokens does not pretend local token count maps deterministically to subscription quota consumption.

## Native intelligence surfaces

- **Usage**: bounded minute/hour/day history, disjoint native token classes, root/subagent splits, repo/model/role breakdowns and UTC day/hour heatmap data with exact values.
- **Forecasts**: persisted five-hour/weekly forecast history and an account-local workload scenario planner.
- **Analytics**: provider-observed quota-burn intervals, synchronized local activity, estimated contributor ranking, and reset/re-anchor timeline.

Queries/downsampling happen outside the WinUI dispatcher. These pages do not parse rollout JSONL or issue raw SQLite queries from XAML/code-behind.

## Tray and notifications

The tray icon shows the most constrained known quota as a small numeric badge. Its tooltip summarizes five-hour/weekly remaining values and whether quota data is live or stale. Explorer/taskbar restarts re-register the icon automatically.

Phase 3.5 caches rendered badge variants and skips shell/icon work when visible state is unchanged, avoiding repeated `System.Drawing.Font`/icon construction on every telemetry snapshot.

Current quick actions:

- open the dashboard;
- refresh telemetry;
- enable/disable notifications;
- enable/disable Start with Windows for published builds;
- exit TajsTokens completely.

Current alerts cover low quota thresholds (30/20/10/5% by default), detected quota-window refreshes and provider-health transitions. Notifications never include prompt/reasoning content.

## Portable release artifact

The publish-smoke workflow publishes a self-contained Windows x64 folder, including the Windows App SDK runtime, and uploads it as a ZIP artifact plus SHA-256 sidecar after the relevant build/publish checks.

Code signing, a per-user installer, WinGet, update verification and an in-app updater remain tracked separately.

## Local data and privacy

The local database lives at `%LOCALAPPDATA%\TajsTokens\telemetry.db`; settings live beside it in `settings.json`. These paths are outside the application directory so portable/update experiments do not overwrite local history.

Normal telemetry does **not** persist ordinary prompt/message text, reasoning text, source-code bodies, shell commands/output, tool result payloads, credentials/auth material or raw rollout JSON. Large content-bearing rollout records are transient parser input and are reduced to normalized type/status/size/timing/identity metadata.

Phase 4 intelligence is computed only from those already-normalized content-free records plus quota/forecast/reset history. Attribution and scenario rows therefore do not create a second content-bearing analytics store behind the user's back, a standard which the software industry has somehow made noteworthy.
