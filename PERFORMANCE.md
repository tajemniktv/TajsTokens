# TajsTokens performance baseline

This document records the runtime evidence that motivated Phase 3.5 and the budgets used to judge future regressions. It is deliberately a profiling note, not a benchmark leaderboard.

## Post-Phase-3 / PR #50 profile

A real Windows dotTrace session against a populated local Codex history showed that PR #50 fixed the most serious architectural problem: historical rollout/SQLite work no longer dominated the WinUI dispatcher and provider telemetry could appear before the history import completed.

The follow-up capture still exposed several concrete costs:

- process CPU was roughly 3% during the shown capture;
- total process memory was roughly 73 MB, with roughly 19 MB managed at the shown point;
- the selected thread-state view was dominated by waiting rather than sustained CPU saturation, consistent with serialized file/SQLite work;
- a UI-freeze sample spent roughly 3.1 seconds below `WindowsSystemTrayService.UpdateStatus` / `BuildStatusIcon`, dominated by `System.Drawing.Font` / `FontFamily.CreateFontFamily` construction;
- rollout ingestion showed large aggregate wall time in `RecordRolloutRecordAsync`, generic SQLite `ExecuteAsync`, `PersistParsedRecordAsync`, and cumulative-token persistence.

Those numbers are profiler observations from one development run, not stable cross-machine throughput measurements. They should be compared by shape/hot path, not treated as contractual exact values.

## Phase 3.5 changes

### Tray

The notification-area icon caches rendered badge variants by `(remaining percentage, freshness)` and skips shell/icon updates when both visible badge state and tooltip are unchanged. A cache miss is rendered on a thread-pool worker, so `System.Drawing` font-family discovery and bitmap/icon rasterization no longer run synchronously from the WinUI dispatcher. Only a completed cached icon is posted back to the hidden tray window for the cheap `Shell_NotifyIcon` application step. The latest desired tooltip/badge state is retained independently of whether Explorer temporarily accepts `NIM_MODIFY`, so taskbar recreation cannot resurrect stale quota health.

### Rollout metadata persistence

Production rollout ingestion batches `rollout_records` metadata at the same 128-record durable checkpoint cadence. Each durable batch performs one canonical rollout-file upsert through the Observatory store, then one SQLite transaction with a prepared command for up to 128 record rows. This replaces the previous per-JSONL-line file-stat/file-upsert/record-transaction cycle while keeping the canonical safe-file-label and path-replacement rules in one persistence implementation.

Semantic session/agent/token/context writes remain ordered and idempotent. The source byte checkpoint advances only after both the canonical file metadata update and the record batch complete. If the record batch fails after the file upsert, replay safely repeats the idempotent file update and `ON CONFLICT`-safe record inserts instead of advancing past uncommitted metadata.

### UI data loading

Observatory is now a fixed master-detail surface rather than a nested whole-page scroll containing every detail domain simultaneously. Session search is executed in SQLite before limiting results, so an older matching session remains discoverable after the corpus grows beyond the default recent-session window. Agent topology is traversed for the selected root/subagent tree through a bounded recursive query, and Storage applies its session predicate before ordering/limiting. Timeline, context, token, and storage information otherwise load on demand through detail tabs.

Long lists remain inside virtualizing `ListView` surfaces. Search is debounced, and page/search/selection generation guards prevent stale asynchronous data from replacing the current query or selection.

### Forecasting and persistence

Phase 3.5 forecasting is scoped to the provider's current reset identity. Mixed flat/moving meter intervals retain their zero-rate samples instead of modelling only active-burn periods, while a wholly flat rounded meter is represented as uncertainty rather than confident zero burn. Exhaustion ETA is emitted only when exhaustion is projected before the authoritative reset; otherwise the useful result is the projected remaining margin at reset.

The reset-aware forecast fields are persisted in telemetry schema v4, including forecast state, burn pressure, reset margin, trend, and the quantized-flat marker. The v3-to-v4 migration assigns safe defaults to older rows instead of silently dropping Phase 3.5 semantics on new rows after restart.

## Product budgets

These are engineering targets, not hard real-time guarantees:

| Surface | Target |
| --- | --- |
| Ordinary WinUI dispatcher callback | ideally <16-30 ms |
| Selection/navigation reaction before async detail work | <50 ms |
| Unchanged/cached tray status handling | normally <10 ms |
| Synchronous dispatcher ownership | work expected to exceed ~100 ms must move elsewhere |
| UI freeze | reproducible >250 ms is a profiling-worthy bug |
| Historical import | may be long-running, but must stay progressive, cancellable and non-blocking to provider/UI freshness |

Steady-state monitoring should remain low enough in CPU, memory, disk I/O and wakeups to leave running continuously.

## Re-profiling checklist

For a representative Windows run with an existing `.codex` corpus:

1. start with a fresh or copied TajsTokens telemetry database so first-import behavior is exercised;
2. capture startup through provider publication and at least one substantial rollout import;
3. inspect WinUI dispatcher hot paths separately from aggregate process wall time;
4. confirm repeated unchanged snapshots do not repeatedly construct tray fonts/icons, and confirm a new badge cache miss performs `System.Drawing` work off the dispatcher;
5. compare `rollout_records` transaction/command counts with record count and verify record writes occur in bounded batches rather than per line;
6. record total import time, complete records processed, records/sec, database growth and peak process/managed memory;
7. leave the app idle for several polling cycles and record CPU/I/O/wakeup behavior;
8. repeat selection/navigation across sessions with timeline/context/storage tabs and search for an older session to catch global materialization, limit-before-filter, or stale-result regressions.

Do not make normal CI depend on narrow wall-clock thresholds. Correctness CI should verify batching/checkpoint semantics; performance regression jobs or manual profiling can use broad throughput/allocation guardrails on controlled runners.
