# Copilot instructions for TajsTokens

## Architecture conventions

- `TajsTokens.Core` owns domain models, provider/repository contracts, and provider-independent analytics. It must not reference App or Infrastructure.
- `TajsTokens.Infrastructure` implements Core contracts for storage, local files, and external providers. Infrastructure depends on Core.
- `TajsTokens.App` is the Windows composition/UI layer and may depend on Core + Infrastructure. UI code must not parse Codex files, invoke Tokscale, or query external APIs directly.
- Prefer provider interfaces + mock/stub implementations over fabricated integrations.
- Quota percentages and token counters are independent telemetry streams. Never invent a universal token-to-subscription-quota conversion.
- Provider adapters must normalize overlapping provider counters into disjoint token buckets and preserve provenance/reported totals.
- Raw Codex transcript/JSONL payloads must not be persisted wholesale in the telemetry database.

## Build and test

- Authoritative Windows build: `dotnet build TajsTokens.sln`
- Core tests: `dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj`
- Non-Windows builds compile a placeholder App target only; they do not validate WinUI/XAML. Never claim the full app builds based only on a Linux/macOS run.

## Coding expectations

- Nullable reference types must remain enabled.
- Preserve local-first/offline behavior and SQLite persistence.
- Persist UTC timestamps losslessly with explicit offsets/round-trip formatting.
- Incremental file ingestion checkpoints advance only to complete, successfully handled record boundaries.
- Keep changes incremental and maintainable; avoid speculative overengineering.
