# TajsTokens agent instructions

## Authority

Read [`PROJECT.md`](PROJECT.md) before making product, telemetry, persistence, provider-model, or architecture decisions.

`PROJECT.md` is the sole authority for current product/data architecture and planning during the foundation reset. Existing code is implementation evidence, not specification. Files under `docs/archive/` are historical only and must not be used to resurrect old architecture or roadmap assumptions.

## Current product direction

TajsTokens is currently a **Codex-first observability application**. The near-term goal is to understand and expose Codex richly from trustworthy local evidence.

Keep possible future providers in mind when choosing boundaries, but do not turn current Codex work into a generic-provider framework unless a concrete Codex need requires that abstraction.

The architectural shorthand is:

> **Codex-first, not Codex-shaped.** Preserve rich Codex-native semantics. Future providers get their own native models. Shared abstractions are projections only where semantics genuinely overlap.

## Reset rules

- Do not introduce new semantic claims unless they are supported by an empirical source contract in `PROJECT.md` or work explicitly being added to it.
- Do not treat current UI labels, database columns, service names, tests, comments, table names, or field names as proof that a metric or relationship means what it appears to mean.
- Keep raw acquisition, source contracts, normalization, durable evidence, and read-model policy conceptually separate.
- Preserve source provenance, source-native identity, missing fields, ambiguity, and conflicting observations instead of silently resolving them during ingestion.
- Do not add new roadmap/phase/architecture documents. Update `PROJECT.md`.
- Do not delete working implementation merely because it is pre-reset. Change or remove it only when reset work establishes a reason.
- Keep privacy conservative: do not expand durable storage of prompts, reasoning text, source-code bodies, credentials, or other content-bearing payloads without an explicit design decision.
- External application data and credentials are read-only unless a future source contract explicitly establishes otherwise.
- Source explorers and raw inspection tools are acquisition aids. Their output does not become product truth merely because it came from a Codex-owned database or API.

## Provider and schema rules

- **Do not force Codex into a lowest-common-denominator provider schema.** If Codex exposes a useful, safe, contracted concept or relationship, preserve it even if a hypothetical future provider has no equivalent.
- **Do not force future providers into the Codex ontology.** A future provider may use runs, requests, generations, workers, queues, hardware metrics, or entirely different concepts. Give it a provider-native model rather than manufacturing Codex threads/turns/subagents.
- **Normalized does not mean generic.** Provider-native normalized observations are preferred when they reflect the evidence honestly.
- Shared infrastructure may unify provenance, capture/observation timestamps, source identity, contract/version metadata, persistence mechanics, and query plumbing. It must not require one universal semantic payload.
- Shared cross-provider concepts belong primarily in read-model projections and must be justified by demonstrated semantic overlap, not by similar names.
- If a read model maps a source-native value into a broader presentation category, retain the native value and treat the broad category as derived policy. For example, do not replace a provider's exact state with a generic `Running`/`Failed` classification and then persist the classification as evidence.
- Provider-specific UI and read models are first-class. Codex views may be substantially richer than future shared views.
- Do not discard a safe, understood source-native field merely because no current UI renders it. Also do not persist a field merely because it exists: its semantics and privacy posture still need a contract.
- Avoid speculative provider capability matrices, plugin systems, or generic base classes whose only justification is possible future breadth. Prefer small seams that keep provider ownership explicit.

## Working style

Prefer small, source-focused changes that can be reviewed against captured evidence. New parsers or adapters should come with sanitized fixtures/probes covering observed variants and edge cases.

When code and source evidence disagree, report the disagreement. Do not make the fixture imitate the code merely to keep a test green.

For Codex investigation work:

1. identify the exact Codex-owned source being studied;
2. capture representative values/states without assigning unsupported meaning;
3. document known / observed-but-unexplained / hypothesized / unknown semantics;
4. establish identity, time, lifecycle, mutation, and coverage behavior where possible;
5. only then add normalized/durable evidence or a read-model interpretation.

When multiple Codex sources appear to describe the same concept, do not choose an authority by convenience. Preserve the observations and put any precedence/reconciliation rule in an explicit read-model policy after the relationship is demonstrated.

## Current repository

The present solution contains:

- `src/TajsTokens.App`
- `src/TajsTokens.Core`
- `src/TajsTokens.Infrastructure`
- `tests/TajsTokens.Core.Tests`

These are current implementation boundaries, not guaranteed target architecture.

The repository also contains a read-only Codex State DB Explorer. Treat it as acquisition/investigation tooling according to the boundary documented in `PROJECT.md`, not as permission to directly project raw private SQLite rows into product semantics.

## Build and validation

Full WinUI validation is Windows-specific.

```powershell
dotnet restore TajsTokens.sln
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
dotnet build TajsTokens.sln -c Debug
```

For focused changes, run the narrowest relevant tests first, then the full Core test suite and Windows application build before considering the change complete.
