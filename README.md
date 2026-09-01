# TajsTokens

TajsTokens is a local-first native Windows application for observing AI coding-agent usage.

## Architecture reset

The project is currently rebuilding its data model from source evidence upward. The existing application is a working implementation, but its current metrics, labels, database shapes, and derived behavior are **not** the architectural specification.

[`PROJECT.md`](PROJECT.md) is the sole authority for current product/data architecture and planning. Older planning, architecture, audit, and performance documents have been preserved under `docs/archive/` for archaeology only.

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

## Documentation

Start with [`PROJECT.md`](PROJECT.md). `AGENTS.md` contains repository-working rules for coding agents and is subordinate to `PROJECT.md` on all product/data semantics.
