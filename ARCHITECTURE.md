# Architecture

## Foundation goals

- Local-first telemetry persistence and analytics
- Provider-based integrations (Tokscale, Codex quota, JSONL session files, announcements)
- WinUI UI decoupled from raw providers/files

## Solution layout

- `TajsTokens.App` (WinUI 3)
  - Navigation shell: Overview, Usage, Agents, Forecasts, Events, Settings
  - `OverviewViewModel` supplies polished mock data for quota, usage, forecast, topology, and events
  - `NoOpSystemTrayService` defines clean seam for future tray/notification integration
- `TajsTokens.Core`
  - Domain models (`TokenUsage`, `QuotaSnapshot`, `Agent`, `Announcement`, etc.)
  - Provider/repository/ingestion interfaces
  - `ForecastingService` (EWMA-style burn-rate and reset-survival estimate)
- `TajsTokens.Infrastructure`
  - SQLite repository and schema initialization (`SqliteTelemetryRepository`)
  - File checkpoint storage for incremental session ingestion
  - Provider stubs/mocks (`MockTokscaleProvider`, stub quota/announcement providers)
  - `CodexSessionIngestionService` and file event reader abstraction
- `TajsTokens.Core.Tests`
  - Unit tests for core forecasting calculations

## Data flow (current scaffolding)

Providers / JSONL reader -> ingestion services -> `ITelemetryRepository` (SQLite) -> analytics (`IForecastingService`) -> WinUI view-models.

## Incremental ingestion strategy

- Persist file offset and last session marker per JSONL file (`ingestion_checkpoints`)
- Read only new bytes from stored offset
- Store normalized events/session/agent linkage in repository
- Future parser enriches raw JSON lines into typed session and topology updates
