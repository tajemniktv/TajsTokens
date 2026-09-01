# Roadmap

Every phase should end as a coherent working product rather than an architectural halfway house.

## Phase 0 - Bootstrap foundation ✅

- Native WinUI 3/.NET shell
- Core / Infrastructure / App / Tests separation
- SQLite migrations and normalized domain models
- Windows-authoritative CI

## Phase 1 - Real-data MVP ✅

- Verified Tokscale JSON adapter for token accounting/hourly history
- Provider-authoritative Codex quota through local app-server
- Persistent quota history and basic forecasting
- Overview source/freshness/error states
- Robust CLI discovery/diagnostics

## Phase 2 - Daily-driver Windows utility ✅

- One process-lifetime background telemetry coordinator
- Preserve last-known-good data and mark stale provider state explicitly
- Notification-area status with open/refresh/exit controls
- Close-to-tray application lifecycle
- Deduplicated low-quota/reset/provider-health alerts
- Small persistent runtime-settings model
- Self-contained Windows x64 portable release smoke artifact

Phase 2 intentionally does **not** require the dashboard to stay open for telemetry to remain useful.

## Phase 3 - Codex observatory 🚧

- Sanitized/synthetic real-world rollout fixture corpus
- Incremental direct Codex JSONL discovery and typed normalization
- Replay-safe native Codex accounting primitives in **shadow** mode, including counter epochs/resets and inherited-prefix exclusion
- Rollout-embedded quota snapshots fused into the same local quota history with explicit provenance
- Root/subagent topology and content-free lifecycle/activity state
- Session browser/detail timeline in the native Windows app
- Context-window utilization and compaction telemetry
- Rollout record/file-size diagnostics without raw payload persistence

Phase 3 proves direct/native Codex observability but does **not** replace Tokscale as the default accounting source. Full reconciliation/cutover remains Phase 6.

## Phase 4 - Intelligence and historical analytics

- Burn-rate/ETA forecasting and sustainable-pace guidance
- `What ate my quota?` attribution and concurrency analysis
- Reset/re-anchoring intelligence
- Historical hourly/daily/minutely analytics, heatmaps, repo/model/agent drill-down
- Scenario planner based on observed account behavior

## Phase 5 - Power-user platform

- Announcements/provider status timeline
- CLI + local API
- Mini HUD/taskbar/widget surfaces
- Multi-account/profile groundwork
- Full release/update/signing/WinGet work

## Phase 6 - Native accounting

- Expand the Phase 3 native Codex accounting shadow into full supported accounting coverage
- Native vs Tokscale reconciliation harness and user/developer comparison surface
- Proven parity across counter resets, cache/reasoning semantics, inherited histories and time buckets
- Native accounting becomes TajsTokens' default Codex accounting backend; Tokscale remains fallback/import/reference

## Phase 7 - Ecosystem / expansion

- Additional coding-agent providers where useful
- Git/PR efficiency metrics
- Deterministic model/reasoning benchmark lab
- Secure remote sync, read-only web dashboard, notification relay and optional mobile companion
