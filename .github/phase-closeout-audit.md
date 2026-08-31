# Phase 0-2 closeout audit

This file records the issue-boundary audit performed after merging Phase 2 so future work does not confuse an advanced epic with a completed milestone subset.

## Completed milestone issues

The dedicated Phase 2 implementation issues are complete and were closed after verifying the merged implementation:

- #41 Daily-driver Windows utility milestone
- #42 Runtime settings persistence
- #43 Release smoke test / portable artifact
- #44 Background telemetry snapshot coordinator
- #45 Tray lifecycle and close semantics

## Foundation closeout

Issue #2 was nearly complete but its explicit foundation scope still lacked:

- normalized repository/workspace entities and SQLite tables;
- a persistent forecast-snapshot entity/table boundary;
- current database-schema documentation.

This branch adds those missing pieces as schema v3, including a transactional v2 -> v3 migration and regression tests. The tables are intentionally content-free and do not imply that Phase 3 rollout collectors already populate them.

## Broad epics intentionally left open

These issues have completed Phase 0-2 subsets but still contain acceptance criteria assigned to later phases, so they must not be closed merely because the current milestone advanced them:

- #3 Tokscale provider: current model/hourly JSON integration works, but richer session/version/capability/reconciliation work remains.
- #6 Quota telemetry: current app-server five-hour/weekly telemetry works, but dynamic/additional windows, credits, rollout-source fusion, and richer metadata remain.
- #16 Overview UI: real-data Overview works, but dynamic windows, agent summaries, richer forecast/pace UI, and full adaptive polish remain.
- #19 Tray: Phase 2 status/open/refresh/toggles/exit are done; richer flyout, active-agent actions, display modes, and later provider/profile concepts remain.
- #20 Notifications: low-quota/reset/provider-health alerts are done; forecast, agent, storage, announcement, quiet-hours, deep-link, and per-rule controls remain.
- #21 Background collector: one process-lifetime loop/startup/manual refresh is done; sleep/resume, adaptive polling, richer backoff/diagnostics/resource measurement remain.
- #33 Source health: live/stale/error/LKG behavior exists, while the full cross-provider health taxonomy, fallback policy, retry metadata, and doctor/history UI remain.
- #36 Distribution: the x64 portable ZIP + SHA-256 smoke path is done; installer, signing, updater, WinGet, additional release hardening remain.

The roadmap therefore marks Phases 0, 1, and 2 complete while Phase 3 remains the next implementation boundary.
