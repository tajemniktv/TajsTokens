# Copilot instructions for TajsTokens

## Architecture conventions

- Keep a strict boundary: `App` (UI) -> `Core` (domain/interfaces) -> `Infrastructure` (providers/storage).
- Do not couple UI directly to Tokscale/Codex files/external APIs.
- Prefer provider interfaces + mock/stub implementations over fabricated integrations.

## Build and test

- Restore/build solution: `dotnet build TajsTokens.sln`
- Run core tests: `dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj`

## Coding expectations

- Nullable reference types must remain enabled.
- Preserve local-first/offline behavior and SQLite persistence.
- Keep changes incremental and maintainable; avoid speculative overengineering.
