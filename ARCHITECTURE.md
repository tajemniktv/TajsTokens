# Architecture

TajsTokens is a local-first native Windows observability application. Through Phase 2 it has a real-data Codex/Tokscale dashboard, one process-lifetime background telemetry coordinator, SQLite quota history, tray status/controls, notifications, runtime settings, and a portable Windows x64 publish path.

## Architectural goals

- Keep UI, domain logic, providers, persistence, and Windows integration separable.
- Keep subscription quota telemetry independent from token telemetry so empirical correlations can be measured instead of assumed.
- Prefer provider-owned/local read paths over copied credentials or undocumented web APIs.
- Preserve last-known-good telemetry through transient provider failures, but never present stale data as fresh.
- Persist normalized local history without duplicating raw prompt/reasoning/tool payloads.
- Keep Phase 3 direct rollout ingestion incremental and content-minimal.

## Solution layout

- `TajsTokens.App` (WinUI 3)
  - Windows composition root and navigation shell.
  - `OverviewViewModel` renders the shared real telemetry snapshot and quota history.
  - Native Win32 notification-area surface, close-to-tray lifecycle, notifications, and Start with Windows integration.
- `TajsTokens.Core`
  - Provider-independent domain models and interfaces.
  - Disjoint token buckets with optional provider-reported totals.
  - Quota snapshots based on provider-reported percentage/window/reset telemetry.
  - Forecasting and quota-alert services.
  - Content-free repository/workspace identities and persistable forecast snapshots for the normalized telemetry schema.
- `TajsTokens.Infrastructure`
  - Versioned SQLite telemetry persistence and resilient JSON runtime-settings persistence.
  - Tokscale CLI adapter and Codex app-server quota provider.
  - Process-lifetime `TelemetryCoordinator` that serializes refreshes and publishes one shared snapshot.
  - Incremental JSONL boundary/checkpoint primitives for the Phase 3 typed rollout parser.
- `TajsTokens.Core.Tests`
  - Forecasting, accounting, provider-contract, alert/settings, coordinator, ingestion-boundary, and SQLite migration tests.

## Dependency direction

`Core` has no dependency on App or Infrastructure. `Infrastructure -> Core`. `App -> Core + Infrastructure` as the Windows composition root.

The UI does not parse Tokscale JSON, Codex app-server JSON-RPC, rollout JSONL, or SQLite directly.

## Current data flow

```text
Tokscale CLI --------------------\
                                  -> TelemetryCoordinator -> shared TelemetrySnapshot -> Overview
Codex local app-server quota ----/                                      |              -> tray
                                                                         |              -> alerts
                                                                         v
                                                              SQLite quota history
```

The coordinator owns startup, interval, and manual provider refresh serialization for the process. A manual refresh may cancel stale non-manual work rather than waiting behind it. Tokscale model/hourly reads are committed as one generation, and partial Codex quota responses retain omitted last-known-good lanes while marking the combined quota state stale.

The dashboard, tray, and alert engine therefore observe the same generation instead of running independent provider loops.

## Providers

### Tokscale

Tokscale is the bootstrap/default accounting provider. TajsTokens invokes its documented machine-readable CLI boundary and normalizes returned token classes into disjoint buckets. A global `tokscale` executable is preferred; the documented `npx --yes tokscale@latest` path is used only when the direct command is genuinely unavailable.

Tokscale failures preserve a previous successful token snapshot as stale. TajsTokens does not guess unsupported JSON contracts.

### Codex quota

Quota is read through a short-lived local `codex app-server` session using the normal initialize handshake and `account/rateLimits/read`. Five-hour and weekly lanes are identified from provider-reported window duration, and provider `resetsAt` values are authoritative. The read does not create a model turn.

Direct rollout-derived quota/session telemetry remains Phase 3 work.

## Background Windows lifecycle

- One collector loop exists for the application process.
- Runtime polling interval changes restart that loop with the normalized new interval.
- Closing the dashboard hides it when background mode is enabled; explicit tray Exit cancels the collector and tears down tray resources.
- The tray displays the most constrained known quota as a generated numeric icon and distinguishes fresh from stale state.
- Explorer/taskbar recreation is handled through `TaskbarCreated` re-registration.
- Low-quota/reset/provider-health notifications are deduplicated by quota-window or transition identity.
- Runtime preferences live in `%LOCALAPPDATA%\TajsTokens\settings.json`; corrupt or missing settings fall back to safe defaults and writes use a temporary replacement file.

## SQLite persistence

The telemetry database lives at `%LOCALAPPDATA%\TajsTokens\telemetry.db` and currently uses `PRAGMA user_version = 3`. Migrations run transactionally.

Schema v3 contains:

| Table | Purpose |
| --- | --- |
| `quota_snapshots` | Provider/profile quota observations and provider reset identity |
| `token_usage` | Normalized disjoint token observations |
| `sessions` | Stable session/thread metadata |
| `agents` | Agent identity/state metadata |
| `agent_relationships` | Parent/child agent links |
| `usage_events` | Normalized content-free session/activity events |
| `reset_events` | Detected quota-reset events |
| `announcements` | Normalized announcement/event feed records |
| `ingestion_checkpoints` | Incremental rollout file offsets, parser version, and source identity |
| `repositories` | Stable content-free repository identities |
| `workspaces` | Stable content-free workspace identities and optional repository association |
| `forecast_snapshots` | Historical forecast results scoped by provider/profile/window |

Indexes cover quota-history lookup, token/event time lookup, workspace-to-repository lookup, and forecast-history lookup.

Phase 1/2 actively persist quota history. The remaining normalized tables are the durable schema boundary for later rollout/session/agent/forecast-history phases; they exist now so Core and Infrastructure do not need a persistence redesign when those collectors become active.

## Incremental rollout ingestion boundary

Typed direct Codex rollout normalization is Phase 3, but the low-level boundary is already designed to be safe for large append-only JSONL sources:

- Persist the exact byte offset of the last complete handled record plus parser version and source-file identity.
- Never checkpoint `FileInfo.Length`; a source can grow between read completion and checkpoint persistence.
- Never checkpoint an unterminated final record.
- Reset ingestion when the file at a known path is replaced, even if the replacement is the same size or larger.
- Stream complete records rather than loading an entire multi-GB tail into memory.
- Raw Codex JSON/transcript payloads are transient parser input and are not copied wholesale into SQLite.

Phase 3 will add typed normalization, inherited-history/counter-epoch semantics, sanitized rollout fixtures, topology, context/compaction telemetry, and storage diagnostics on top of this boundary.

## Privacy boundary

Through Phase 2 TajsTokens persists normalized quota telemetry, schema-level content-free entities, checkpoints, and non-sensitive runtime preferences. It does not persist prompt text, reasoning text, shell output, auth material, or raw rollout payloads.

Settings are deliberately separate from the telemetry database: they are small application preferences rather than historical telemetry and contain no credentials.
