# TajsTokens agent instructions

## Authority

Read [`PROJECT.md`](PROJECT.md) before making product, telemetry, persistence, or architecture decisions.

`PROJECT.md` is the sole authority for current product/data architecture and planning during the foundation reset. Existing code is implementation evidence, not specification. Files under `docs/archive/` are historical only and must not be used to resurrect old architecture or roadmap assumptions.

## Reset rules

- Do not introduce new semantic claims unless they are supported by an empirical source contract in `PROJECT.md` or work explicitly being added to it.
- Do not treat current UI labels, database columns, service names, tests, or comments as proof that a metric means what they currently say.
- Keep raw acquisition, source contracts, normalization, durable evidence, and read-model policy conceptually separate.
- Preserve source provenance, missing fields, ambiguity, and conflicting observations instead of silently resolving them during ingestion.
- Do not add new roadmap/phase/architecture documents. Update `PROJECT.md`.
- Do not delete working implementation merely because it is pre-reset. Change or remove it only when the reset work establishes a reason.
- Keep privacy conservative: do not expand durable storage of prompts, reasoning text, source-code bodies, credentials, or other content-bearing payloads without an explicit design decision.
- External application data and credentials are read-only unless a future source contract explicitly establishes otherwise.

## Working style

Prefer small, source-focused changes that can be reviewed against captured evidence. New parsers or adapters should come with sanitized fixtures/probes covering observed variants and edge cases.

When code and source evidence disagree, report the disagreement. Do not make the fixture imitate the code merely to keep a test green.

## Current repository

The present solution contains:

- `src/TajsTokens.App`
- `src/TajsTokens.Core`
- `src/TajsTokens.Infrastructure`
- `tests/TajsTokens.Core.Tests`

These are current implementation boundaries, not guaranteed target architecture.

## Build and validation

Full WinUI validation is Windows-specific.

```powershell
dotnet restore TajsTokens.sln
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.sln -c Debug
```

For focused changes, run the narrowest relevant tests first, then the full Core test suite and Windows application build before considering the change complete.
