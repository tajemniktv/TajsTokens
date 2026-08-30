# TajsTokens

TajsTokens is a local-first native Windows telemetry dashboard for Codex/AI coding-agent usage.

## Projects

- `/src/TajsTokens.App` - WinUI 3 desktop shell (MVVM + mock Overview)
- `/src/TajsTokens.Core` - domain models, service interfaces, forecasting logic
- `/src/TajsTokens.Infrastructure` - SQLite persistence, provider stubs/mocks, ingestion plumbing
- `/tests/TajsTokens.Core.Tests` - unit tests for core forecasting behavior

## Prerequisites

- .NET SDK 10.0+
- Windows 11 (or Windows 10 19041+) for launching the WinUI app

## Build

```bash
dotnet restore /home/runner/work/TajsTokens/TajsTokens/TajsTokens.sln
dotnet build /home/runner/work/TajsTokens/TajsTokens/TajsTokens.sln -c Debug
```

## Run tests

```bash
dotnet test /home/runner/work/TajsTokens/TajsTokens/tests/TajsTokens.Core.Tests/TajsTokens.Core.Tests.csproj -c Debug
```

## Run the app (Windows)

```bash
dotnet run --project /home/runner/work/TajsTokens/TajsTokens/src/TajsTokens.App/TajsTokens.App.csproj
```

The Overview page is intentionally powered by synthetic/mock telemetry to demonstrate the intended UX while provider integrations are implemented incrementally.
