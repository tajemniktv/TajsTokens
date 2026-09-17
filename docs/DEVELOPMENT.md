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
window title include the Git commit and `clean`/`dirty` state (tracked changes and untracked
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
