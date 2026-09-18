# TajsTokens

TajsTokens is a local-first Windows companion for **Codex quota, usage and activity**.

See your reported quota windows, explore your work history, and get conditional quota
outlooks and usage predictions—with freshness and uncertainty kept visible.

## What you can do

- **Overview:** check prominent remaining-quota values, resets, labelled hourly token usage,
  quota outlooks and short-term usage predictions. Collection details stay available on demand.
- **Codex → Work & threads:** navigate workspaces, threads and subagents; inspect turns, tools,
  context and supported native source details. Jump from a thread to its token usage.
- **Codex → Usage breakdown:** compare models, projects, sessions, hours, days and months in consistent token
  tables. Search/sort rows, inspect exact counts and filter a selected row across breakdowns.
  Scope is retained local Codex activity; time buckets use UTC, not inferred billing or quota cost.
- **Forecasts:** browse saved quota outlooks, see token workload predictions, estimate
  supported workload scenarios, and compare models within a selected history cohort and target. Historical evidence
  explains which rollout/app-server readings are usable, repeated, conflicting or incomplete.
  Cost-calibration diagnostics compare actual recorded work with quota movement; they are
  separate from end-to-end forecast and cross-regime transfer diagnostics. Workload composition
  is visible; a learned quota outlook replaces the incumbent only after independent validation gates pass.
- **Quota burn:** explore recorded quota movement alongside local token activity and filter
  quota drops separately from reset-time shifts. Account readings are the default; rollout
  observations remain separate diagnostics, with explicit observation times and detailed deadlines.
- **Diagnostics:** check source health, collection coverage and why a forecast is unavailable.
- **Data Explorer:** inspect Codex SQLite data read-only and compare selected sources.
- **Codex CLI Harness:** explicitly run a configured local CLI and inspect its output.

Predictions are estimates, not guarantees. Token predictions describe recorded local usage
(including cached input), not subscription-quota percentages. Quota outlooks and scenarios
may remain in a learning state when compatible history is insufficient. An omitted quota
window is not treated as unlimited usage.

## Local data and privacy

Codex source inspection is read-only. TajsTokens keeps selected telemetry in its own local
database; viewing a conversation or other source content does not automatically copy it
into that database. Inspection exports are explicit local actions, not automatic uploads.
The CLI harness runs only when invoked.

The daily installation lives at
`%LOCALAPPDATA%/Programs/TajemnikTV/TajsTokens/current`, with persistent data in the
sibling `data` directory. Launch **TajsTokens** from the Start menu after installation.

## Build and run

Requires **Windows 10 19041+ or Windows 11** and **.NET SDK 10.0+** to build from source.

```powershell
dotnet restore TajsTokens.slnx
dotnet build TajsTokens.slnx -c Debug
```

**A successful local app build installs and restarts the daily app.** To build without
changing the running installation, add `-p:DogfoodEnabled=false`. CI and test-only builds
do not deploy.

See the [contributor guide](docs/DEVELOPMENT.md) for isolated running, tests, deployment,
backup and recovery instructions. [PROJECT.md](PROJECT.md) contains current architecture,
product direction and planned work; those plans are not a list of shipped features.
