# TajsTokens

TajsTokens is a local-first native Windows observability/control-center app for Codex and AI coding-agent usage.

The current product combines:

- provider-authoritative Codex subscription quota and reset timestamps from the local `codex app-server` without creating a model turn;
- broad token totals/cache/output/reasoning breakdowns and hourly history from Tokscale;
- direct incremental Codex rollout ingestion for privacy-safe session/root/subagent, native-shadow token, context/compaction, activity and storage telemetry;
- one process-lifetime telemetry coordinator shared by Overview, tray, alerts and Observatory;
- last-known-good provider data with explicit stale/unavailable states;
- background collection, close-to-tray lifecycle, Start with Windows, native notification-area status and deduplicated quota/provider alerts;
- SQLite quota/history and normalized Observatory persistence under `%LOCALAPPDATA%\TajsTokens`;
- reset-aware quota forecasting with sustainable pace, burn pressure, confidence, quantized-meter uncertainty and margin-at-reset;
- a self-contained Windows x64 portable publish smoke artifact.

Tokscale intentionally remains the bootstrap/default **broad accounting** backend. Native Codex accounting already runs in shadow mode from rollout telemetry, but it does not become the default until later reconciliation demonstrates parity.

## Projects

- `src/TajsTokens.App` - WinUI 3 desktop shell, native Windows surfaces and composition root
- `src/TajsTokens.Core` - provider-independent domain models, accounting/alert/forecast services and interfaces
- `src/TajsTokens.Infrastructure` - SQLite/settings persistence, local providers, rollout ingestion and telemetry coordination
- `tests/TajsTokens.Core.Tests` - accounting, persistence, provider, ingestion, concurrency and privacy regressions

Repository-level coding-agent instructions **and** the current architecture/threading/privacy contract live in [`AGENTS.md`](AGENTS.md). `ROADMAP.md` describes the working-product phase plan and `PERFORMANCE.md` records the Phase 3.5 runtime budgets/profiling baseline.

## Prerequisites

- .NET SDK 10.0+ for development builds
- Windows 11 (or Windows 10 19041+) for launching the WinUI app
- an externally executable Codex CLI for live app-server quota reads: on `PATH`, in a supported user-local Codex install location, or selected with `CODEX_CLI_PATH`
- Tokscale either installed on `PATH` **or** Node.js/npm with `npx` available; TajsTokens can fall back to `npx --yes tokscale@latest`

Codex Desktop may contain a packaged/private Codex runtime while exposing no `codex` command to ordinary desktop processes. TajsTokens deliberately does not execute protected `WindowsApps` package resources directly.

TajsTokens does not install or modify Codex or Tokscale automatically. The npx Tokscale path uses npm's normal package cache and can be slower on its first invocation.

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

### Tokscale

TajsTokens consumes Tokscale through documented machine-readable CLI output, currently equivalent to:

```powershell
tokscale models --json --group-by client,model --client codex
tokscale hourly --json --client codex
```

When `tokscale` is not globally available, supported zero-install invocation uses:

```powershell
npx --yes tokscale@latest models --json --group-by client,model --client codex
npx --yes tokscale@latest hourly --json --client codex
```

If neither source succeeds, or a non-empty payload does not match a supported contract, previous successful token data can remain visible as **stale** rather than becoming a plausible fake zero.

### Codex quota

TajsTokens starts a short-lived local Codex app-server, performs the initialization handshake and calls `account/rateLimits/read`. Quota windows are identified by provider-reported duration/window metadata rather than assuming `primary` always means five-hour. Provider `resetsAt` is authoritative.

Quota reads never create a model turn. A partial provider response cannot silently substitute one lane for another, and a failed refresh preserves last-known-good values with stale provenance.

Direct rollout ingestion also captures embedded quota observations during active model work. Those retain event timestamp/window/source provenance and are distinct from the independently polled app-server source.

### Codex rollouts / Observatory

The Observatory discovers local active/archive rollout JSONL, resumes from exact complete-record byte checkpoints, and normalizes only telemetry needed by the product.

Current normalized domains include:

- stable sessions/root/subagent relationships;
- model/reasoning metadata;
- native shadow cumulative-token deltas with counter-reset epochs and inherited-child-history exclusion;
- quota observations;
- content-free activity/timeline events;
- context-window observations and compactions;
- rollout file/record-size metadata.

Phase 3.5 batches high-volume `rollout_records` metadata at durable checkpoint boundaries instead of using one SQLite transaction/file-upsert per JSONL record. Semantic accounting writes remain ordered and replay-safe.

The Observatory UI is a master-detail explorer: search/select sessions on the left, then load Overview, Agents, Timeline, Context, Tokens or Storage detail on demand rather than rendering every domain at once.

## Forecast semantics

Forecasts are scoped to the provider's current quota-window/reset identity.

- A slope from the previous reset epoch is not carried into a re-anchored window.
- Exhaustion ETA is shown only when exhaustion is projected **before** the current authoritative reset.
- Otherwise the useful result is `survives reset` plus projected remaining margin at reset.
- Sustainable pace is remaining quota divided by remaining reset time; burn pressure compares recent observed pace with that sustainable pace.
- Flat rounded provider samples mean `flat within meter precision`, not confident exact-zero burn.
- Sparse/stale data lowers confidence or suppresses forecasting.

TajsTokens does not pretend local token count maps deterministically to subscription quota consumption.

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

The release-smoke workflow publishes a self-contained Windows x64 folder, including the Windows App SDK runtime, and uploads it as a ZIP artifact plus SHA-256 sidecar after the same restore/build/test checks used by CI.

Code signing, a per-user installer, WinGet, update verification and an in-app updater remain tracked separately.

## Local data and privacy

The local database lives at `%LOCALAPPDATA%\TajsTokens\telemetry.db`; settings live beside it in `settings.json`. These paths are outside the application directory so portable/update experiments do not overwrite local history.

Normal telemetry does **not** persist ordinary prompt/message text, reasoning text, source-code bodies, shell commands/output, tool result payloads, credentials/auth material or raw rollout JSON. Large content-bearing rollout records are transient parser input and are reduced to normalized type/status/size/timing/identity metadata.
