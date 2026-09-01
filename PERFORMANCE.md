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

The notification-area icon now caches rendered badge variants by `(remaining percentage, freshness)` and skips shell/icon updates entirely when both visible badge state and tooltip are unchanged. Expensive font/icon construction therefore moves from every telemetry snapshot to the first encounter of a distinct visible state.

### Rollout metadata persistence

Production rollout ingestion now batches `rollout_records` metadata at the same 128-record durable checkpoint cadence. A batch uses one SQLite connection + transaction, one prepared insert command, and one rollout-file upsert instead of paying a connection/transaction/file-upsert cycle for every JSONL record.

Semantic session/agent/token/context writes remain ordered and idempotent. The source byte checkpoint advances only after the corresponding metadata batch has committed, so batching cannot trade away replay correctness.

### UI data loading

Observatory is now a fixed master-detail surface rather than a nested whole-page scroll containing every detail domain simultaneously. Session detail tabs load agent topology, timeline, context, token and storage information on demand. Long lists remain inside virtualizing `ListView` surfaces and page/selection generation guards prevent stale asynchronous detail from replacing the current selection.

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
4. confirm repeated unchanged snapshots do not repeatedly construct tray fonts/icons;
5. compare `rollout_records` transaction/command counts with record count and verify they occur in bounded batches;
6. record total import time, complete records processed, records/sec, database growth and peak process/managed memory;
7. leave the app idle for several polling cycles and record CPU/I/O/wakeup behavior;
8. repeat selection/navigation across sessions with timeline/context/storage tabs to catch materialization or stale-result regressions.

Do not make normal CI depend on narrow wall-clock thresholds. Correctness CI should verify batching/checkpoint semantics; performance regression jobs or manual profiling can use broad throughput/allocation guardrails on controlled runners.
