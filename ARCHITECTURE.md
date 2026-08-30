# Architecture

## Foundation goals

- Local-first telemetry persistence and analytics
- Provider-based integrations (Tokscale, Codex quota, JSONL session files, announcements)
- WinUI UI decoupled from raw providers/files
- Quota telemetry kept independent from token telemetry so empirical correlations can be measured rather than assumed

## Solution layout

- `TajsTokens.App` (WinUI 3)
  - Navigation shell: Overview, Usage, Agents, Forecasts, Events, Settings
  - `OverviewViewModel` supplies polished in-memory mock data only
  - `NoOpSystemTrayService` defines a clean seam for future tray/notification integration
- `TajsTokens.Core`
  - Provider-independent domain models and interfaces
  - Disjoint token buckets with optional provider-reported totals
  - Quota snapshots based on provider-reported percentage/window/reset telemetry
  - `ForecastingService` estimates quota percentage-point burn and reset survival
- `TajsTokens.Infrastructure`
  - Versioned SQLite schema/repository
  - Lossless UTC timestamp round-tripping
  - File checkpoint storage for incremental session ingestion
  - Provider stubs/mocks; verified concrete adapters are added only with fixtures/contracts
  - Complete-record JSONL boundary reader that does not persist raw transcript payloads
- `TajsTokens.Core.Tests`
  - Unit tests for forecasting and token accounting

## Dependency direction

`Core` has no dependency on App/Infrastructure. `Infrastructure -> Core`. `App -> Core + Infrastructure` as the Windows composition root.

## Data flow

Providers / JSONL reader -> ingestion + normalization -> `ITelemetryRepository` (SQLite) -> analytics -> WinUI view-models.

## Incremental ingestion strategy

- Persist exact byte offset of the last complete handled JSONL record plus parser version.
- Never checkpoint `FileInfo.Length`; files can grow between EOF observation and checkpoint persistence.
- Never checkpoint an unterminated final JSONL record.
- Stream bounded records instead of loading an entire file tail into memory.
- Raw Codex JSON/transcript payloads are transient parser input and are not copied wholesale into SQLite.
- When the typed parser contract changes, increment parser version so earlier records can be safely reprocessed.

## Persistence/versioning

SQLite uses `PRAGMA user_version` for schema migrations. The PR #1 pre-release schema is treated as version 0 and its synthetic quota/token/checkpoint tables are recreated once as schema version 1.
