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

## Phase 3 - Codex observatory ✅

- Sanitized/synthetic real-world rollout fixture baseline
- Incremental direct Codex JSONL discovery and typed normalization
- Replay-safe native Codex accounting primitives in **shadow** mode, including counter epochs/resets and inherited-prefix exclusion
- Content-free parser resume state matched to exact byte checkpoints
- Rollout-embedded five-hour/weekly quota snapshots in local history with explicit provenance
- Root/subagent topology and content-free lifecycle/activity state baseline
- Session browser/detail timeline in the native Windows app
- Context-window utilization and compaction telemetry baseline
- Stable-identity rollout storage diagnostics without raw payload or absolute-path persistence in the observatory surface
- Runtime hotfix keeping historical rollout/SQLite work off the WinUI dispatcher while provider telemetry publishes progressively

Phase 3 proves direct/native Codex observability but does **not** replace Tokscale as the default accounting source. Full reconciliation/cutover remains Phase 6.

## Phase 3.5 - Runtime, concurrency, UX, and forecast hardening ✅

- Cache tray badge/icon state and skip unchanged shell updates so telemetry snapshots do not recreate fonts/icons repeatedly
- Batch high-volume rollout-record metadata at durable checkpoint boundaries instead of one SQLite transaction per JSONL record
- Make execution-domain ownership, serialized SQLite writing, backpressure, cancellation and refresh supersession explicit repository architecture
- Keep provider/history work off WinUI and regression-test manual-vs-scheduled refresh races
- Replace the all-at-once Observatory page with a master-detail session explorer and on-demand detail tabs
- Give top-level Usage, Forecasts, Diagnostics and Settings surfaces explicit product homes rather than generic placeholders
- Make current-window forecasting reset-aware: sustainable pace, burn pressure, confidence, quantized-meter uncertainty, trend and margin-at-reset
- Suppress meaningless exhaustion timestamps that fall after the authoritative reset
- Record Windows profiling evidence, budgets and a repeatable re-profiling procedure
- Consolidate repository agent/architecture guidance into root `AGENTS.md`

The post-change representative Windows profile is complete. It confirmed the UI-thread/runtime fixes worked and exposed the next bottleneck as long-running discovery/semantic SQLite work, now owned by Phase 4.5 rather than keeping the Phase 3.5 umbrella permanently open.

## Phase 4 - Intelligence and historical analytics ✅

Merged in PR #57 and tracked by completed milestone #56. The detailed domain issues remain authoritative where their broader acceptance criteria extend beyond the first Phase 4 product slice.

- persist reset-aware forecast history and expose current pace/trend/confidence views (#9);
- derive provider-observed quota-burn intervals without crossing reset/re-anchor boundaries and correlate them with native root/subagent activity (#11);
- detect/deduplicate expected resets, rolling-window re-anchors and unusual/full-reset evidence while keeping provider `resetsAt` authoritative (#14);
- query bounded minute/hour/day native history, token classes, root/subagent splits, repo/model dimensions and day/hour heatmap data in SQLite (#17);
- estimate dual-window workload scenarios from the user's own historical quota-drop/concurrency observations with ranges/sample counts/confidence and honest insufficient-history states (#10);
- ship native Usage, Forecasts and Analytics surfaces over normalized intelligence contracts.

Phase 4 attribution remains interval-based and explicitly estimated. TajsTokens must never invent a universal local-token-to-subscription-quota conversion merely because two numbers happen to be available in the same database.

## Phase 4.5 - State-indexed ingestion and persistence hardening 🚧

Tracked by #59, with #58 owning Codex state-indexed selection and #51/#52 owning throughput/execution semantics.

- discover and read the current Codex state SQLite catalog safely without hard-coding a numbered private schema forever;
- validate a recognized schema fingerprint and fail open to the existing filesystem discovery path when state is unavailable/unknown;
- query changed threads by indexed `updated_at_ms` with a bounded overlap/fingerprint rather than recursively touching every historical rollout on warm refresh;
- use cumulative `threads.tokens_used` only as reconciliation/change evidence, never as disjoint event accounting;
- use verified `thread_spawn_edges` as primary ordinary topology evidence with rollout metadata as repair/fallback;
- keep rollout JSONL authoritative for event-level token classes, exact timestamps, context/compaction, embedded quota and activity history;
- batch semantic session/agent/activity/token/quota/context writes into bounded durable SQLite transactions with one intentional serialized writer;
- preserve TajsTokens-owned exact byte checkpoints, counter epochs, replay/idempotence and inherited-history semantics;
- benchmark cold import, warm idle, one root, root + subagents, archive movement, fallback and cancellation boundaries.

Primary product target: **a warm idle Observatory refresh should be effectively invisible**.

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
