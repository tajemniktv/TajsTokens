# Development and recovery

Run commands from the repository root. Contributor rules and telemetry/source boundaries are in [AGENTS.md](../AGENTS.md) and [PROJECT.md](../PROJECT.md).

## Build and test

```powershell
dotnet restore TajsTokens.slnx
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.slnx -c Debug
```

Run tests and builds serially when they share outputs. Use `--no-restore` after a successful restore. `dotnet build TajsTokens.slnx -c Debug -p:DogfoodEnabled=false` performs an isolated app build; CI and test-only builds do not deploy. Full WinUI validation requires Windows. SDK outputs are in ignored `artifacts/`, and task temporary files belong in `.codex/temp/`.

## Daily installation

A successful default local app build publishes, installs and restarts `%LOCALAPPDATA%\Programs\TajemnikTV\TajsTokens\current`. Persistent database/settings live in sibling `data`; the prior binary is kept in `previous`, and deployment backups are in `backups`. The build does not delete `data`.

```powershell
# Explicitly deploy the current publish, or roll back the binary without downgrading data:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Deploy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Deploy.ps1 -Rollback

# Validate deployment behavior after changing it:
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Test-Deployment.ps1
```

Binary rollback does **not** restore an older database schema. Stop the app and preserve the current `data` folder before restoring a matching data backup. Consult [the deployment scripts](../tools/dogfood/README.md) for the exact staging, hash-verification and recovery contract.

## Source and integration boundaries

Codex native files and credentials are read-only. The app's local database owns retained evidence; no external collector should become a second authoritative Codex token ledger. The [local limits API](LOCAL_LIMITS_API.md) serves already-collected current quota on authenticated loopback only. Remote SSH fleet probes are explicit, read-only, content-free status checks; they do not synchronize history or quota. Avoid writing prompts, command bodies, account credentials or API tokens to shared reports.
