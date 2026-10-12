# Incremental local builds

Run `dotnet build TajsTokens.slnx -c Debug` from the repository root. MSBuild
reuses compilation and WinUI intermediates in ignored `artifacts/`; NuGet reuses
its global package cache. Keep these outputs between builds. Do not routinely
run `clean`, `rebuild`, or clear NuGet caches. Restore still runs when needed so
dependency changes are not hidden by a blanket `--no-restore`.

Successful local app builds publish incrementally into
`artifacts/dogfood-publish/<Configuration>/<RuntimeIdentifier>/`. The .NET SDK
tracks prior publish outputs and removes files no longer produced. Debug/Release
and runtime publish directories are separate. A deployment lock serializes
publish and install. Only use this directory for generated publish outputs.

Deployment copies the result into a fresh `.codex/temp/dogfood/staging/`
snapshot, validates it, and uses the existing backup, swap, and readiness
protocol. The cache is never moved into the installation. Each successful app
build still installs and restarts, even when compilation is up to date; copying,
hash verification, data backup, and startup checks are intentional safety costs.

For test-only or isolated validation, use `-p:DogfoodEnabled=false`. CI does not
dogfood. CI caches NuGet packages with dependency-keyed fallback reuse, but not
`bin`/`obj` or publish outputs across machines. It still restores before building.

Check deployment changes with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Test-Deployment.ps1
```

To investigate reuse, compare unchanged build/publish logs with `-v:normal` and
output timestamps. A deleted generated output must be restored on the next
build; changing an included source must invalidate the relevant compilation.
