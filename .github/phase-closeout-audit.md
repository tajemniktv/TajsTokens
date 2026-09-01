# Phase 0-3 closeout audit

This file records milestone-boundary audits so future work does not confuse a broad epic with the subset that completed a working product phase.

## Completed milestone issues

The dedicated Phase 2 implementation issues were closed after verifying the merged implementation:

- #41 Daily-driver Windows utility milestone
- #42 Runtime settings persistence
- #43 Release smoke test / portable artifact
- #44 Background telemetry snapshot coordinator
- #45 Tray lifecycle and close semantics

Phase 3 is also complete as a working Codex Observatory milestone. Its delivered product boundary includes direct incremental rollout ingestion, privacy-safe normalized session/root/subagent telemetry, native-shadow accounting primitives, embedded quota observations, context/compaction telemetry, session/timeline UI, and rollout storage diagnostics. Phase 3.5 is the current stabilization boundary for runtime cost, scalable Observatory reads, forecast semantics, and concurrency hardening.

## Foundation closeout

Issue #2 was nearly complete after Phase 2 but its explicit foundation scope still lacked:

- normalized repository/workspace entities and SQLite tables;
- a persistent forecast-snapshot entity/table boundary;
- current database-schema documentation.

Those pieces were added as schema v3 with a transactional v2 -> v3 migration and regression tests. Phase 3 then populated the normalized session/agent/event boundaries with direct Codex rollout telemetry rather than redesigning the persistence model.

## Broad epics intentionally left open

These issues have completed Phase 0-3 subsets but still contain acceptance criteria assigned to later phases, so they must not be closed merely because the current milestone advanced them:

- #3 Tokscale provider: current model/hourly JSON integration works, but richer session/version/capability/reconciliation work remains.
- #6 Quota telemetry: app-server five-hour/weekly telemetry and rollout-embedded quota observations work, but dynamic/additional windows, credits, and richer metadata remain.
- #16 Overview UI: real-data Overview works, while dynamic windows, richer analytics, agent summaries, and later adaptive polish remain.
- #19 Tray: status/open/refresh/toggles/exit are done; richer flyout, active-agent actions, display modes, and later provider/profile concepts remain.
- #20 Notifications: low-quota/reset/provider-health alerts are done; forecast, agent, storage, announcement, quiet-hours, deep-link, and per-rule controls remain.
- #21 Background collector: one process-lifetime loop/startup/manual refresh and off-dispatcher rollout ingestion are done; sleep/resume, adaptive polling, richer backoff/diagnostics/resource measurement remain.
- #33 Source health: live/stale/error/LKG behavior exists, while the full cross-provider health taxonomy, fallback policy, retry metadata, and doctor/history UI remain.
- #36 Distribution: the x64 portable ZIP + SHA-256 smoke path is done; installer, signing, updater, WinGet, and additional release hardening remain.

The roadmap therefore marks Phases 0, 1, 2, and 3 complete. Phase 3.5 is a deliberate stabilization milestone before Phase 4 intelligence/historical analytics expands the workload and UI surface further.
