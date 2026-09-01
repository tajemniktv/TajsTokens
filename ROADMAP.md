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

Phase 3 proves direct/native Codex observability but does **not** replace Tokscale as the default accounting source. Full reconciliation/cutover is now pulled forward into Phase 4.6 rather than waiting for the old Phase 6 slot.

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

The post-change representative Windows profile is complete. It confirmed the UI-thread/runtime fixes worked and exposed the next bottleneck as long-running discovery/semantic SQLite work, which Phase 4.5 addressed.

## Phase 4 - Intelligence and historical analytics ✅

Merged in PR #57 and tracked by completed milestone #56. The detailed domain issues remain authoritative where their broader acceptance criteria extend beyond the first Phase 4 product slice.

- persist reset-aware forecast history and expose current pace/trend/confidence views (#9);
- derive provider-observed quota-burn intervals without crossing reset/re-anchor boundaries and correlate them with native root/subagent activity (#11);
- detect/deduplicate expected resets, rolling-window re-anchors and unusual/full-reset evidence while keeping provider `resetsAt` authoritative (#14);
- query bounded minute/hour/day native history, token classes, root/subagent splits, repo/model dimensions and day/hour heatmap data in SQLite (#17);
- estimate dual-window workload scenarios from the user's own historical quota-drop/concurrency observations with ranges/sample counts/confidence and honest insufficient-history states (#10);
- ship native Usage, Forecasts and Analytics surfaces over normalized intelligence contracts.

Phase 4 attribution remains interval-based and explicitly estimated. TajsTokens must never invent a universal local-token-to-subscription-quota conversion merely because two numbers happen to be available in the same database.

## Phase 4.5 - State-indexed ingestion and persistence hardening ✅

Merged in PR #60 and tracked by #59/#58/#51/#52.

- discover and read the current Codex state SQLite catalog safely without hard-coding a numbered private schema forever;
- validate a recognized schema fingerprint and fail open to the existing filesystem discovery path when state is unavailable/unknown;
- query changed threads by indexed `updated_at_ms` with a bounded overlap/fingerprint rather than recursively touching every historical rollout on warm refresh;
- use cumulative `threads.tokens_used` only as reconciliation/change evidence, never as disjoint event accounting;
- use verified `thread_spawn_edges` as primary ordinary topology evidence with rollout metadata as repair/fallback;
- keep rollout JSONL authoritative for event-level token classes, exact timestamps, context/compaction, embedded quota and activity history;
- batch semantic session/agent/activity/quota/context and rollout-metadata writes into bounded durable SQLite transactions with one intentional serialized writer;
- preserve TajsTokens-owned exact byte checkpoints, counter epochs, replay/idempotence and inherited-history semantics;
- document and run representative Windows cold/warm profiling.

Representative real-corpus validation completed on 2026-09-01:
- cold upgrade reconciliation reached 318 normalized sessions across about 1.98 GiB of observed rollout history while the UI remained usable;
- warm idle settled at effectively 0 CPU / 0 disk;
- a no-change warm refresh was negligible;
- one changed Codex session produced 11 new complete records, 6 normalized records and 1 touched session, with the complete telemetry refresh finishing in about 3.1 s and low disk activity.

The primary Phase 4.5 product target is therefore met: **warm idle Observatory refresh is effectively invisible**. Remaining cumulative-token commit amplification is a cold-import optimization rather than a steady-state architectural blocker.

## Phase 4.6 - Product plumbing and native accounting cutover 🚧

Tracked by #65, with #61 owning Settings and #62 owning the native-accounting cutover. #63 tracks remote/cloud-only Codex coverage separately.

### Product plumbing / Observatory read performance
- replace the Settings placeholder with a real WinUI surface;
- materialize a normalized/versioned `settings.json` on first successful startup and preserve safe fallback for corrupt/inaccessible settings;
- optimize Observatory session-list loading after profiling identified `SearchSessionsAsync` correlated SQLite aggregates as the dominant read-side hotspot;
- keep session/search/filter semantics correct while avoiding repeated scans of the same token/context/storage/activity rows;
- make refresh diagnostics distinguish catalog/discovered scope from actual changed/scanned work;
- re-profile the representative 318-session history after the query change.

### Native Codex accounting cutover
- reconcile native vs Tokscale over identical local session/model/hour scopes instead of declaring parity by vibes;
- compare uncached input, cache read/write, output, reasoning, reported totals, session totals, model attribution and time buckets;
- port/adapt mature Tokscale Codex edge-case semantics where evidence shows TajsTokens is weaker, preserving MIT provenance/notice requirements;
- make native Codex accounting sufficient to drive normal Overview/Usage data without launching Tokscale;
- promote native accounting to the default Codex source after parity gates;
- disable Tokscale by default for Codex steady-state collection while retaining it explicitly for reconciliation/fallback and future non-Codex breadth during the transition.

Remote/cloud-only sessions are not a cutover blocker. Until #63 finds a supported source, local native aggregates must say they represent local/partial history rather than silently treating unseen remote activity as zero.

## Phase 5 - Power-user platform

- Announcements/provider status timeline
- CLI + local API
- Mini HUD/taskbar/widget surfaces
- Multi-account/profile groundwork
- Full release/update/signing/WinGet work

## Phase 6 - Accounting expansion and hardening

The old "first native cutover" role has moved forward to Phase 4.6 because the implementation matured faster than the roadmap.

- expand reconciliation/coverage tooling beyond the initial Codex cutover;
- improve remote-inclusive/account-global coverage when supported provider surfaces exist;
- extend native accounting to additional clients only where deep support is worthwhile;
- retain explicit provenance/coverage semantics across local, remote and imported histories.

## Phase 7 - Ecosystem / expansion

- Additional coding-agent providers where useful
- Git/PR efficiency metrics
- Deterministic model/reasoning benchmark lab
- Secure remote sync, read-only web dashboard, notification relay and optional mobile companion
