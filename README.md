# TajsTokens

TajsTokens is a local-first Windows companion for **Codex quota, usage and activity**.

See your reported quota windows, explore your work history, and get conditional quota
outlooks and usage predictions—with freshness and uncertainty kept visible.

## What you can do

- **Overview:** check prominent remaining-quota values, resets, labelled hourly token usage,
  quota outlooks and 5/15-minute local-token nowcasts, paused after ten minutes without observed
  activity (quiet open turns are not treated as confirmed idle sessions). A separate even-burn comparison shows usage
  against elapsed time at the reading, with an explicitly inferred window start—not a forecast.
  Collection details stay available on demand.
- **Codex → Work & threads:** navigate workspaces, threads and subagents; inspect turns, tools,
  context and supported native source details. Jump from a thread to its token usage.
  Thread Storage also shows bounded supplemental response-evidence coverage, selected counter snapshots and identity conflicts;
  these records stay separate from token totals and forecasts.
- **Codex → Usage breakdown:** compare models, projects, sessions, hours, days and months in consistent token
  tables. Search/sort rows, inspect exact counts and filter a selected row across breakdowns.
  A timeline and removable filter chips sit above the breakdown; detailed token columns are optional.
  Times display locally; daily/monthly groups retain UTC boundaries. Totals are not inferred billing or quota cost.
- **Forecasts:** start with current quota outlooks, see token nowcasts and conditional 30/60-minute
  session outlooks (recorded local work, not a prediction of human presence), estimate
  supported workload scenarios, and browse saved outlooks. Advanced → Model lab compares models within a selected history cohort and target. Historical evidence
  explains which rollout/app-server readings are usable, repeated, conflicting or incomplete.
  Cost-calibration diagnostics compare actual recorded work with quota movement; they are
  separate from end-to-end forecast and cross-regime transfer diagnostics. Workload composition
  is visible; a learned quota outlook replaces the incumbent only after independent validation gates pass.
  Explicit Model Lab evaluations with workload-score results save an original local research report for later inspection;
  these reports are experimental diagnostics, not a subscription balance or per-task accounting.
- **Quota history:** select points on a quota-used timeline alongside optional local token activity and filter
  quota drops separately from reset-time shifts. Account readings are the default; rollout
  observations remain separate diagnostics, with explicit observation times and detailed deadlines.
- **Advanced → Diagnostics / Model lab:** check source health, collection coverage and prediction evidence.
- **Experimental daily backend reports:** opt in on Quota history to collect daily Codex token
  counts and reported per-surface percentages using your existing Codex sign-in. Private endpoints
  may change; failures remain visible. Reports stay local, credentials are not saved, and daily
  percentages are not treated as the current weekly quota or a monetary cost.
- **Provider-native account evidence:** Diagnostics compares backend account activity with retained local
  tokens and reads optional thread credit estimates. Missing estimates remain unavailable; credits are
  not quota percentages. Experimental plan history is not yet exposed through Codex's supported app-server seam.
  Retained evidence also shows named quota/spend-control metadata and local service-tier setting
  counts. Settings are observations, not proof of the tier billed; unknown values stay unknown.
  Evidence-change diagnostics distinguish reported configuration changes, missing fields,
  failures and revised historical reports without treating ordinary resets as policy changes.
- **Historical rollout ownership:** explicitly associate retained histories with a recorded account
  in Settings, or revoke the association. User assertions remain separate from native account IDs;
  live learning still requires independently collected validation evidence.
- **Advanced → Data explorer:** inspect Codex SQLite data read-only and compare selected sources.
- **Advanced → CLI harness:** explicitly run a configured local CLI and inspect its output.
- **Window preferences:** restored window size and maximized state survive restarts.

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
