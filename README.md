# TajsTokens

TajsTokens is a local-first native Windows application for **rich Codex observability**.

The current focus is understanding and exposing what Codex already knows about its own activity: source state, projects, threads, turns, relationships, tools, logs, runtime metadata, usage-related observations, and other source-native structures where their semantics can be established safely from evidence.

Local-first means users should be able to inspect the data their own Codex installation exposes without that data needing to leave the machine. Durable duplication and export are separate decisions: content-bearing source data does not automatically belong in TajsTokens' own database, and any export surface should make the boundary explicit and offer sanitization/redaction where appropriate. If raw export is supported, it should be an explicit user choice.

Possible future providers are a design consideration, not the current product scope. TajsTokens is intentionally **Codex-first, not Codex-shaped**: Codex can have a rich first-class model, while a future provider should be free to keep its own native concepts rather than being forced into Codex's schema.

## Architecture reset

The project is currently rebuilding its data model from source evidence upward. The existing application is a working implementation, but its current metrics, labels, database shapes, and derived behavior are **not** the architectural specification.

The architecture proceeds through five layers:

1. raw source acquisition;
2. empirical source contracts;
3. provider-native normalized observations;
4. durable evidence;
5. provider-native and, where justified, shared read models.

There is deliberately no universal provider schema between acquisition and the UI. Shared abstractions are introduced only where source semantics genuinely overlap.

[`PROJECT.md`](PROJECT.md) is the sole authority for current product/data architecture and planning. Older planning, architecture, audit, and performance documents have been preserved under `docs/archive/` for archaeology only.

## Current investigation tooling

The application includes a read-only Codex State DB Explorer for inspecting Codex-owned SQLite state without assigning domain meaning to fields merely because they exist. It supports the evidence-first source-contract work described in `PROJECT.md` and is not itself a semantic model.

## Repository layout

- `src/TajsTokens.App` - WinUI 3 desktop application
- `src/TajsTokens.Core` - core models and interfaces used by the current implementation
- `src/TajsTokens.Infrastructure` - current source adapters, persistence, ingestion, and services
- `tests/TajsTokens.Core.Tests` - regression tests

The layout above describes the repository as it exists today. It does not pre-approve the current semantic boundaries during the reset.

## Requirements

- .NET SDK 10.0+
- Windows 11 or Windows 10 19041+ to launch the WinUI application

## Build

```powershell
dotnet restore TajsTokens.sln
dotnet build TajsTokens.sln -c Debug
```

The authoritative full application build is Windows because WinUI/XAML is Windows-specific.

## Tests

```powershell
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
```

## Run

```powershell
dotnet run --project src/TajsTokens.App/TajsTokens.App.csproj
```

## Documentation roles

The three root documents intentionally have different jobs:

- [`README.md`](README.md) is the repository entry point: what TajsTokens is, how to build it, and where to start.
- [`PROJECT.md`](PROJECT.md) is the **sole authoritative product/data architecture and planning document** during the reset.
- [`AGENTS.md`](AGENTS.md) contains operational guardrails for coding agents working in the repository and is subordinate to `PROJECT.md` on all product/data semantics.

Keeping those roles separate avoids turning either the README into an architecture novel or the agent instructions into a second competing specification, two venerable traditions of software documentation that we can safely skip.
