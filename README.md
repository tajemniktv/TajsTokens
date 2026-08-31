# TajsTokens

TajsTokens is a local-first native Windows telemetry dashboard for Codex/AI coding-agent usage.

The current Phase 2 work turns the real-data dashboard into a background Windows utility:

- Codex subscription quota and provider reset timestamps from the local `codex app-server`;
- token totals, cache/output/reasoning breakdowns, and hourly history from Tokscale;
- one process-lifetime telemetry coordinator shared by dashboard, tray, and alert evaluation;
- last-known-good token/quota snapshots retained and visibly marked stale after transient provider failures;
- notification-area quota status with open, refresh, notification, startup, and exit controls;
- close-to-tray behavior so collection continues while the dashboard is hidden;
- optional per-user Start with Windows registration for published/apphost builds;
- deduplicated low-quota, reset, and provider-health notifications;
- quota snapshots persisted to local SQLite so forecasting can learn from real history;
- resilient local runtime settings under `%LOCALAPPDATA%\TajsTokens\settings.json`.

Tokscale is intentionally the bootstrap/default token-accounting backend. TajsTokens will later grow a native accounting engine and reconcile it against Tokscale before native accounting becomes the default.

## Projects

- `src/TajsTokens.App` - WinUI 3 desktop shell, tray surface, and composition root
- `src/TajsTokens.Core` - domain models, alert/forecast services, service interfaces
- `src/TajsTokens.Infrastructure` - SQLite/settings persistence, real local providers, background telemetry coordination
- `tests/TajsTokens.Core.Tests` - unit and provider-contract tests

## Prerequisites

- .NET SDK 10.0+ for development builds
- Windows 11 (or Windows 10 19041+) for launching the WinUI app
- an externally executable Codex CLI for live app-server quota reads: on `PATH`, in a supported user-local Codex install location, or selected with `CODEX_CLI_PATH`
- Tokscale either installed on `PATH` **or** Node.js/npm with `npx` available; TajsTokens falls back to `npx --yes tokscale@latest` when a global Tokscale command is absent

Codex Desktop may contain its own packaged/private Codex runtime while still exposing no `codex` command to ordinary desktop processes. TajsTokens deliberately does not execute protected `WindowsApps` package resources directly. A future quota fallback can use rollout telemetry when no externally executable app-server CLI is available.

TajsTokens does not install or modify Codex or Tokscale automatically. The `npx` Tokscale path uses npm's normal package cache and may take longer on its first invocation.

## Build

The authoritative full build is Windows because only Windows compiles the WinUI/XAML application:

```powershell
dotnet restore TajsTokens.sln
dotnet build TajsTokens.sln -c Debug
```

On non-Windows hosts the solution intentionally compiles Core, Infrastructure, tests, and a placeholder App assembly only. A green Linux/macOS build does **not** validate WinUI/XAML.

## Run tests

```powershell
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
```

## Run the app (Windows)

The application remains unpackaged (`WindowsPackageType=None`) for local development:

```powershell
dotnet run --project src/TajsTokens.App/TajsTokens.App.csproj
```

Phase 2 starts one background telemetry loop for the whole process. Closing the dashboard hides it by default rather than terminating TajsTokens; use the tray **Exit** action for a full shutdown. The tray can reopen the dashboard or trigger an immediate refresh.

The default background poll interval is 60 seconds. Runtime settings are normalized and stored in `%LOCALAPPDATA%\TajsTokens\settings.json`; corrupt/missing settings fall back to safe defaults. The tray exposes notification and Start with Windows toggles; a richer Settings page and editable polling/threshold controls remain follow-up work.

The Start with Windows toggle uses the current-user `Run` registry entry and launches a published TajsTokens executable with `--background`. It is intentionally rejected for `dotnet run`/`dotnet.exe` development sessions because that host path would not identify the project to launch.

## Real-data integrations

### Tokscale

TajsTokens uses Tokscale's documented machine-readable CLI boundary. It invokes the equivalent of:

```powershell
tokscale models --json --group-by client,model --client codex
tokscale hourly --json --client codex
```

When `tokscale` is not available globally, the same arguments are run through the documented zero-install path:

```powershell
npx --yes tokscale@latest models --json --group-by client,model --client codex
npx --yes tokscale@latest hourly --json --client codex
```

If neither Tokscale nor npx is available, or Tokscale returns an unsupported payload, TajsTokens retains any previous successful token snapshot as **stale** rather than turning it into fake zero usage. No synthetic token values are substituted.

### Codex quota

TajsTokens starts a short-lived, read-only local Codex app-server session, performs the required initialize handshake, and calls `account/rateLimits/read`. Quota windows are identified by their provider-reported duration (300 minutes for the five-hour window and 10,080 minutes for weekly) rather than assuming `primary` always means five-hour. The provider's `resetsAt` timestamp is authoritative.

Codex executable resolution prefers `CODEX_CLI_PATH`, then known user-local Windows Codex CLI locations, then the `codex` command on `PATH`. If app-server exits before responding, TajsTokens surfaces its bounded stderr/exit status so a missing or broken CLI is diagnosable instead of appearing as a generic stdout EOF.

This quota read does **not** create a model turn. When a quota refresh fails after a successful read, the previous quota remains visible/tray-addressable as stale and low-quota alerts are suppressed until fresh quota data returns.

## Tray and notifications

The Phase 2 tray icon shows the most constrained known quota as a small numeric badge when quota is available. Its tooltip summarizes five-hour/weekly remaining values and whether the quota snapshot is live or stale.

Current quick actions:

- open the dashboard;
- refresh telemetry;
- enable/disable notifications;
- enable/disable Start with Windows for published builds;
- exit TajsTokens completely.

Current background alerts cover low quota thresholds (30/20/10/5% by default), detected quota-window refreshes, and provider-health transitions. Alerts are deduplicated in-process by quota/reset identity so the same threshold is not emitted every polling interval. Notification content never includes prompt/reasoning text.

## Portable release artifact

The Phase 2 release workflow publishes a self-contained Windows x64 folder, including the Windows App SDK runtime, and uploads it as a ZIP artifact plus SHA-256 sidecar after the same restore/build/test checks used by CI. This is the first distribution smoke test, not the final installer/update story.

Code signing, a per-user installer, WinGet, update verification, and an in-app updater remain tracked in #36.

## Local data and privacy

The local database remains under `%LOCALAPPDATA%\TajsTokens\telemetry.db`; runtime settings live beside it in `settings.json`. These paths are outside the application directory so portable/update experiments do not overwrite local history.

Quota snapshots are stored as normalized telemetry. Tokscale model/hour aggregates are rendered from the provider snapshot and are not duplicated into SQLite on every refresh.

TajsTokens does not persist prompt text, reasoning text, shell output, or raw Codex rollout payloads in this phase.
