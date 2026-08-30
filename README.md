# TajsTokens

TajsTokens is a local-first native Windows telemetry dashboard for Codex/AI coding-agent usage.

## Projects

- `src/TajsTokens.App` - WinUI 3 desktop shell (MVVM + mock Overview)
- `src/TajsTokens.Core` - domain models, service interfaces, forecasting logic
- `src/TajsTokens.Infrastructure` - SQLite persistence, provider stubs/mocks, ingestion plumbing
- `tests/TajsTokens.Core.Tests` - unit tests for core calculations

## Prerequisites

- .NET SDK 10.0+
- Windows 11 (or Windows 10 19041+) for launching the WinUI app

## Build

The authoritative full build is Windows because only Windows compiles the WinUI/XAML application:

```powershell
dotnet restore TajsTokens.sln
dotnet build TajsTokens.sln -c Debug
```

On non-Windows hosts the solution intentionally compiles Core, Infrastructure, tests, and a placeholder App assembly only. A green Linux/macOS build does **not** validate WinUI/XAML.

## Run tests

```powershell
dotnet test tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
```

## Run the app (Windows)

The bootstrap application is currently unpackaged (`WindowsPackageType=None`) for simple local development:

```powershell
dotnet run --project src/TajsTokens.App/TajsTokens.App.csproj
```

The Overview page is intentionally powered by synthetic in-memory telemetry to demonstrate the intended UX. Mock values are never written into the production telemetry database.
