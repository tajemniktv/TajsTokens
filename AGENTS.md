# TajsTokens contributor instructions

## Document authority and upkeep

Read [PROJECT.md](PROJECT.md) before product, telemetry, persistence, provider-model or
architecture decisions. It owns the current project: product goals, source contracts,
retention, architecture, implementation status, next work and acceptance criteria.

- **AGENTS.md:** stable contributor workflow and safety rules. Change it when the way we
  work changes, not for each feature, model, schema version, audit result or task milestone.
- **PROJECT.md:** update meaningful product/architecture decisions and implementation gaps
  here. Keep implemented behavior, intended changes and optional research clearly separate.
  Do not rewrite it after every routine command.
- **README.md:** public quick look at features that exist, requirements and basic usage.
  Update it when user-facing features, behavior or setup change—not for internal model
  experiments, task handoffs, audit counts or future plans.
- **[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md):** contributor setup, build, deployment and
  recovery procedures. Keep changing operational detail out of the public overview.
- **docs/source-notes/:** supporting source/version evidence, not a competing product spec.
  **docs/archive/:** historical context only, never current instructions.

There is no separate delivery roadmap. Use **Current work** in PROJECT.md rather than
creating another plan or treating old checklists as current authority.

## Working style

- Select a concrete vertical slice from PROJECT.md: source/contract, acquisition where
  needed, read model, user-facing behavior and verification. Reuse the existing owners.
- Preserve working implementation and user-owned/concurrent changes. Do not restart
  foundational research or introduce a replacement framework without a concrete need.
- Be correct under observed conditions, conservative when uncertain and diagnosable when
  wrong. Resolve actual gaps; do not invent an exhaustive theoretical acceptance matrix.
- Keep implementation, automated tests, runtime checks and user visual acceptance separate.
  A successful build is not proof of every workflow. A pending user visual pass is not
  permission to invent more implementation work.
- Keep temporary files and generated investigation material in the project's
  `.codex/temp/`; SDK outputs belong in ignored `artifacts/`.
- Do not launch, restart or manipulate the app when the user reserves runtime testing.
  Use an isolated build in that case.

## Evidence, privacy and change safety

Apply the source, privacy, provider-native and prediction policies in PROJECT.md.

- A field name, table, UI label or passing test is not proof of source semantics. Use observed
  installed data and explicit matching/near-matching upstream implementation where available.
- Record the upstream commit/tag/version and whether it matches the installed build.
  Investigate runtime disagreements instead of forcing them to match upstream. Use targeted
  experiments for ambiguous, version-sensitive or Desktop-only behavior; do not repeatedly
  re-prove an explicit relationship already corroborated by runtime evidence.
- Source absence from public Codex code is not proof that an observed Desktop source is invalid.
  Label inference and preserve uncertainty rather than inventing relationships.
- Native Codex data and credentials stay read-only. Local inspection, durable collection and
  export are different boundaries; do not expand retention or upload raw/content-bearing data
  merely because it is locally readable. Review/sanitize artifacts intended for sharing.
- Before changing a durable contract, trace relevant writers, reducers, readers, checkpoints
  and migrations. Verify identity, overlap, idempotence and recovery; preserve provenance,
  actual collection times, alternatives and unrelated user data.
- Model/forecast changes must include chronological evaluation against sensible baselines,
  with target-appropriate errors, sample counts, uncertainty coverage and sparse/stale/reset
  behavior. Do not present heuristic scores as calibrated probabilities or stop at an
  evaluation harness when the supported improvement belongs in the product.

## Build and validation

Full WinUI validation requires Windows. Run from the repository root:

```powershell
dotnet restore TajsTokens.slnx
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.slnx -c Debug
```

Successful local Windows app builds publish/install/restart the daily build at
`%LOCALAPPDATA%/Programs/TajemnikTV/TajsTokens/current`; persistent data is in sibling
`data`. CI, test-only and non-app builds do not deploy. Use `-p:DogfoodEnabled=false`
(`Dogfood=false` is an alias) for an isolated build or when runtime testing is reserved.
Report whether validation changed the running version.

Use targeted tests while iterating. At a code milestone and before completion, run the full
Core suite and Windows app build; algorithm changes also require relevant backtests.
Run tests and builds serially when they share output paths. Do not rerun passing checks
without a relevant change or new evidence.

For **documentation-only work**, check consistency, links and referenced commands.
Do not build, deploy, restart the application or run unrelated test suites.

Deployment/recovery details are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). When changing
deployment behavior, also run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/dogfood/Test-Deployment.ps1
```

Keep app-specific data backup and graceful shutdown ownership intact. Do not introduce a
cross-repository runtime dependency to share the deployment implementation.

## Local reference

The read-only Codex source clone is `E:\dev\codex`. Use it as corroborating evidence,
not a dependency or submodule. The [Codex SQLite reference](docs/source-notes/CODEX_SQLITE_SCHEMA.md)
documents the inspected upstream schema; PROJECT.md and installed-runtime evidence determine
what the product may claim.
