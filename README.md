# TajsTokens

TajsTokens is a local-first native Windows telemetry dashboard for Codex/AI coding-agent usage.

The current Phase 1 build replaces the starter's synthetic Overview with real local data:

- Codex subscription quota and provider reset timestamps from the local `codex app-server`;
- token totals, cache/output/reasoning breakdowns, and hourly history from Tokscale;
- quota snapshots persisted to local SQLite so forecasting can learn from real history;
- explicit provider freshness/error state instead of silently substituting fake zeroes.

Tokscale is intentionally the bootstrap/default token-accounting backend. TajsTokens will later grow a native accounting engine and reconcile it against Tokscale before native accounting becomes the default.

## Projects

- `src/TajsTokens.App` - WinUI 3 desktop shell and composition root
- `src/TajsTokens.Core` - domain models, service interfaces, forecasting logic
- `src/TajsTokens.Infrastructure` - SQLite persistence, real local providers, ingestion plumbing
- `tests/TajsTokens.Core.Tests` - unit and provider-contract tests

## Prerequisites

- .NET SDK 10.0+
- Windows 11 (or Windows 10 19041+) for launching the WinUI app
- Codex installed and authenticated, with `codex` available on `PATH`
- Tokscale installed and available on `PATH` for token accounting and hourly history

TajsTokens does not install or modify either tool automatically.

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

The application is currently unpackaged (`WindowsPackageType=None`) for simple local development:

```powershell
dotnet run --project src/TajsTokens.App/TajsTokens.App.csproj
```

The Overview refreshes once when opened and can be refreshed manually. Phase 2 will own persistent background scheduling, tray state, notifications, and release packaging.

## Real-data integrations

### Tokscale

Phase 1 uses Tokscale's documented machine-readable CLI boundary. TajsTokens currently invokes:

```powershell
tokscale models --json --group-by client,model --client codex
tokscale hourly --json --client codex
```

If Tokscale is missing or returns an unsupported payload, token cards fail independently while Codex quota can remain live. No synthetic token values are substituted.

### Codex quota

TajsTokens starts a short-lived, read-only local Codex app-server session, performs the required initialize handshake, and calls `account/rateLimits/read`. Quota windows are identified by their provider-reported duration (300 minutes for the five-hour window and 10,080 minutes for weekly) rather than assuming `primary` always means five-hour. The provider's `resetsAt` timestamp is authoritative.

This quota read does **not** create a model turn.

## Local data and privacy

The Phase 1 database remains under `%LOCALAPPDATA%\TajsTokens\telemetry.db`. Quota snapshots are stored as normalized telemetry. Tokscale model/hour aggregates are rendered directly and are not duplicated into SQLite on every refresh.

TajsTokens does not persist prompt text, reasoning text, shell output, or raw Codex rollout payloads in this phase.
