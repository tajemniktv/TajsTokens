# TajsTokens

TajsTokens is a local-first Windows companion for Codex quota, usage and activity. It shows reported quota windows and source freshness, explores locally observed token and thread history, and offers conditional quota outlooks and planning. Token totals, API-equivalent price estimates and subscription quota remain distinct quantities.

## Current features

- Quota overview, history, alerts and tray status. Account allowances stay visible while filtering local work; reset-aware outlooks identify the tightest window and explain missing evidence separately from conditional pace estimates.
- Background collection uses reduced Windows resource priority. Hidden or minimized dashboards pause immediately and release their pages after 30 seconds, preserving inputs and navigation for reopening. Detailed calibration continues in a separate worker with a 0.5% background CPU budget, time limits, cooldowns and a reusable local numeric cache; quota polling, alerts and compact outlooks continue independently. Opening restores foreground resources before loading the dashboard.
- Local Codex thread, subagent and tool inspection; Live fleet shows recent turn evidence, token increments and cache input mix. An [on-demand SSH check](docs/REMOTE_FLEET.md) shows host-qualified turn status for configured remote aliases and retains content-free checks for offline review without merging account quotas.
- Usage breakdowns by model, project, session and time. The primary session planner checks both allowances with optional model/effort matching and recent-work autofill; forecast history, diagnostics and Model Lab remain available for deeper inspection.
- Native token collection reads plain and Zstandard-compressed Codex rollouts, verifies representation changes and retains superseded numeric evidence. Source text is decoded transiently, without saving decompressed copies.
- Read-only native Codex inspection. Optional local integrations are opt-in: the CLI harness runs only when requested, and the [local limits API](docs/LOCAL_LIMITS_API.md) is disabled by default.
- A read-only observed account/source inventory in Settings. Backend account keys remain attached to the quota observations that supplied them; historical local work is not assigned to the current login automatically.
- A headless `--daily-json` report of UTC-day totals from the existing local native token ledger. It contains aggregates only and does not claim account ownership.

Forecasts are estimates, not guarantees. A missing or stale quota lane is not zero usage or unlimited allowance. The Live fleet's recent-write state is not proof a Codex process is still running.

## Privacy and local data

Codex sources and credentials are read-only. TajsTokens keeps selected telemetry in its own local database; opening source content does not automatically persist or upload it. Remote SSH status checks run only when requested and may require a hardware-key touch.

The daily installation is `%LOCALAPPDATA%\Programs\TajemnikTV\TajsTokens\current`; persistent data lives in its sibling `data` directory.

## Build

Windows 10 19041+ or Windows 11 and .NET SDK 10.0+ are required for the WinUI app.

```powershell
dotnet restore TajsTokens.slnx
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.slnx -c Debug
```

A successful local app build installs and restarts the daily build. Add `-p:DogfoodEnabled=false` to build without changing the installed app. See [development and recovery](docs/DEVELOPMENT.md) and [project design/current work](PROJECT.md).
