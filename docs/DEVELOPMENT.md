# TajsTokens contributor guide

Build, deployment and recovery procedures—not product scope or a roadmap.
For current architecture and work, see [PROJECT.md](../PROJECT.md); contributor rules are
in [AGENTS.md](../AGENTS.md). Run all commands below from the repository root.

## Repository layout

- `src/TajsTokens.App` - WinUI 3 desktop application
- `src/TajsTokens.Core` - core models and interfaces used by the current implementation
- `src/TajsTokens.Infrastructure` - current source adapters, persistence, ingestion, and services
- `tests/TajsTokens.Core.Tests` - regression tests

This is the current implementation layout. Product and source semantics are defined in PROJECT.md.

## Requirements

- .NET SDK 10.0+
- Windows 11 or Windows 10 19041+ to launch the WinUI application

## Build

### Read-only quota-cost evaluation

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --cost "$env:LOCALAPPDATA/Programs/TajemnikTV/TajsTokens/data/telemetry.db"
```

This reads the retained database in one transaction and prints aggregate, cohort-separated
cost diagnostics. It does not migrate/replay sources, change live predictions, or deploy the
app. For private output, redirect to `.codex/temp/`. The snapshot time and policy version are
printed. Use `--cost-owned-rollouts` instead of `--cost` only when the user explicitly confirms
all retained rollouts belong to their account; this records the assertion without rewriting
native IDs or pooling session histories. No credentials or message content are read/exported.

The same cost comparisons appear under Forecasts → Model evaluation, separately labelled
from quota and token forecasts. Missing precision/coverage and sparse generations are not
calibration successes. See PROJECT.md for model gates and interpretation.

Use `--composed` with the same database argument to compare origin-only workload → cost → quota
forecasts with oracle actual-work cost and the incumbent policy on paired outcomes. `--transfer`
tests earlier-regime weights with destination scaling against destination-local training; it
explicitly reports when account-linked compatible regimes are absent. Neither command sends
messages or changes source data. These calculations are also available in Model evaluation.

Use `--composed-strict` for collection-time evaluation. Live promotion requires it as well as
the retrospective `--composed` win; backfilled observations cannot become prospective evidence.
For private ownership-aware experiments, append `--settings <path-to-settings.json>` to `--cost`,
`--composed` or `--composed-strict`. This reads explicit saved associations without saving settings
or changing native account IDs. Create/revoke bounded associations in Settings → Historical
rollout ownership. Asserted history supplements compatible training only; validation remains
native-account evidence and requires stronger gates. Keep settings and account identities local.

### Application build

```powershell
dotnet restore TajsTokens.slnx
dotnet build TajsTokens.slnx -c Debug
```

The authoritative full application build is Windows because WinUI/XAML is Windows-specific.

### Local dogfooding (default)

Every successful local Windows **app** build publishes a complete self-contained build for the
host architecture to `.codex/temp/dogfood/staging/<id>`, verifies the copied file hashes, requests
graceful exit, backs up the owned database/settings, swaps the installation, and starts it.
The installed app must acknowledge shell startup and remain alive for a short smoke interval.
This is startup evidence, not a claim that every data source or screen is healthy.

The stable per-user layout is:

```text
%LOCALAPPDATA%/Programs/TajemnikTV/TajsTokens/
  current/       installed app (TajsTokens.App.exe)
  previous/      last replaced build; explicit rollback remains available
  data/          live telemetry.db, settings.json, and local InspectionExports
  backups/       pre-deployment database/settings snapshots (including WAL/SHM)
  retained/      older binary generations, never automatically deleted
```

Launch **TajsTokens** from the Start menu. Its shortcut always targets `current`; the existing
launch-at-login preference is preserved and reconciled by the app. File properties and the
Settings build identity include the Git commit and `clean`/`dirty` state (tracked changes and untracked
non-ignored files). Both apps use the same versioned `current/build-identity.json` contract:
schema version, app name, identity, configuration, runtime, build timestamp, and file hashes.
Without usable Git metadata the identity explicitly says `git-unknown`.

On first launch, the app copies the old `%LOCALAPPDATA%/TajsTokens` database, settings and
`InspectionExports` into the new `data` directory, then atomically promotes the complete copy.
The legacy originals remain a recovery copy; they are no longer the live store. Legacy
`build-validation` binaries are not app data and are not migrated. Existing destination data
is authoritative: migration never merges or overwrites it. Close any older app instance before
migrating. Failed migration directories are retained separately for investigation/retry.

Opt out for an isolated build/debug session, or while someone is using the running app:

```powershell
dotnet build TajsTokens.slnx -c Debug -p:DogfoodEnabled=false
# Or for all builds launched from this shell:
$env:DogfoodEnabled = 'false'
```

These are the same opt-out and `tools/dogfood/Deploy.ps1` entry points used by TajsToucher;
`-p:Dogfood=false` also works as a compatibility alias in both repositories. Explicit script
deployment defaults to Release; pass `-Configuration Debug` to override. App builds retain
their requested configuration. SDK outputs stay in `artifacts/`; disposable staging/tests stay
in `.codex/temp/`. The backends remain independent so each app retains its own safety rules:
TajsTokens snapshots its database/settings; TajsToucher protects active GPG operations.

CI, design-time builds, restore, and test-only builds do not deploy. Core/Infrastructure/tool
builds do not deploy either. The publish invoked by deployment opts out to prevent recursion.
Cross-compilation still builds the requested target; dogfooding publishes the **host** architecture.
The hook runs after the app's successful Build target, not after unrelated solution projects.
If deployment fails, the command reports failure rather than silently claiming installation.

```powershell
# Explicit deployment, or binary rollback without rebuilding:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Deploy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Deploy.ps1 -Rollback
# Isolated transaction tests; no real app or user data is touched:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Test-Deployment.ps1
```

Publish/copy failures leave the running app untouched. An older tray-only build without the
exit handshake may require **Exit** from its tray menu; deployment refuses to force-kill it.
All processes and shutdown handshakes are preflighted before asking any app to exit. A
cross-session installation lock serializes updates; short Windows sharing-violation retries
allow image handles to close without force-killing a process or overwriting a locked file.
An unrecognized install/session is also a refusal, not permission to terminate a namesake.
Startup failure restores and restarts the prior binaries when graceful shutdown permits it.
On a first installation there is no previous build to restore.

**Recovery:** `transaction.json` means a deployment was interrupted or automatic recovery
could not finish. Further deployments refuse to overwrite this state. Exit TajsTokens, inspect
the journal plus `current`, `previous`, and `retained` (including failed candidates). Preserve the
candidate you move aside, restore the desired complete build as `current`, and only then remove
the journal and relaunch. Do not blindly delete a journal while a deployment is still running.
`pending` and `retained/unused-*` contain unpromoted candidates; retry safely retains them.

Binary rollback deliberately does **not** downgrade or overwrite live data. A future incompatible
schema migration may require manually restoring its matching stopped-app backup: preserve the
entire live `data` directory first, restore the database/settings snapshot to a new `data`
directory, and copy any local exports you want to retain. Never combine a backed-up database
with a different generation's WAL/SHM. Backups, legacy data, and exports remain private local
files; nothing is uploaded. No automatic retention deletion is performed.

All SDK build/intermediate/publish outputs use the SDK's centralized repo-level `artifacts/`
layout, which Git ignores. Existing legacy `bin`/`obj` directories are excluded from compilation
but are not destructively cleaned. Disposable agent/test work belongs in `.codex/temp/`.

## Tests

```powershell
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
```

## Run

```powershell
dotnet run --project src/TajsTokens.App/TajsTokens.App.csproj -p:DogfoodEnabled=false
```
## Provider-native evidence diagnostics

`dotnet run --project tools/TajsTokens.ForecastEvaluation -- --tt <telemetry.db>`
reconstructs the experimental TT basis and scalar-cost comparison from 30 days of owned evidence.
It prints exact basis semantics, weights, reference basket, supported model/effort dimensions,
coverage and matched cost errors. It does not persist original scores, poll Codex, alter past
forecasts or establish cross-user comparability. Retain the full basis ID with any reported TT;
newly reconstructed history may yield a different basis, not a revision to the old unit.
Local and transfer comparisons are labelled separately. A transfer reuses the earlier basis
and fits destination calibration only; zero transfer comparisons means no compatible chronological
pair, not proof that scalar conversion generalizes. Basis/calibration end times are included.

`dotnet run --project tools/TajsTokens.ForecastEvaluation -- --session-quota <telemetry.db>`
evaluates the reconstructed session-workload to quota-cost chain over the same 30-day owned-data
lookback. Output separates conditional-positive and unconditional errors against matched pace,
reset counts and joint-band coverage. The incumbent-policy comparison has its own matched count
and candidate errors; do not compare a subset baseline against the full candidate sample.
It neither polls Codex nor promotes a model; missing bands
are not zero uncertainty. Model Lab exposes the same comparison with source/account cohort labels.

`dotnet run --project tools/TajsTokens.ForecastEvaluation -- --session-outlook <telemetry.db>`
evaluates recorded-activity probability and conditional positive-token workload separately over
retained local history. It performs no native collection. Output includes sample counts, Brier and
calibration errors, conditional/expected-token errors against pace, and historical-range coverage.
It is reconstructed event-time evidence, not historical deployment proof or an account-wide model.

The evaluation CLI also exposes separate server evidence (no credentials or browser access):

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --server-evidence <telemetry.db>
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --probe-server-evidence <telemetry.db>
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --collect-server-evidence <telemetry.db>
```

The first command reads retained evidence only. The probe makes bounded app-server requests and
compares them with the database without writing/migrating it. Collection persists the content-free
reports and may migrate owned schema to 14; use a current application, not an older running binary.
Optional third argument is an existing settings path to include user-declared association counts.
Normal application collection uses the same service on a 30-minute in-process backoff.

Private-backend daily reports are a separate, explicit opt-in:

```powershell
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --daily-pairing <telemetry.db>
dotnet run --project tools/TajsTokens.ForecastEvaluation -- --collect-daily-evidence <telemetry.db>
```

Pairing reads retained reports (initializing current owned schema if needed). Collection performs
one bounded private-backend fetch with the existing selected Codex login and saves allowlisted
typed observations. It does **not** enable background polling, alter settings, refresh tokens or
write native credentials. Use only with explicit authorization for experimental backend access.
Account brackets and a final selected-account check reject account switches. Neither command
turns daily relative percentages into current five-hour/weekly quota or zero credits into a scale.

`--declare-current-rollouts <telemetry.db> <settings.json>` is an explicit ownership action, not a
read-only diagnostic: after a fresh account bracket, it saves bounded assertions through the existing
settings owner. Use only when the user has declared those retained histories belong to the current
account. Preserve the original settings; restart the application to reload an out-of-process change.
Settings → Historical rollout ownership can revoke assertions. No native account fields are rewritten.

See [source contracts and limitations](source-notes/CODEX_SERVER_USAGE.md). Binary rollback never
downgrades the database; an older binary may reject schema 14. Preserve the complete data directory
and use the deployment backup/recovery procedure rather than deleting server evidence tables.
