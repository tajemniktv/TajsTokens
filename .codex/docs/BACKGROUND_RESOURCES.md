# Background resource policy

Historical implementation/measurements from the earlier October 27 resource pass.
The main-process CPU cap, forced GC and discovery coupling below are superseded by
[Stage 1](../../docs/source-notes/STAGE1_RESOURCE_VALIDATION.md); worker containment
and the dated measurement limitations remain relevant. PROJECT.md owns current status.

Windows implementation and local measurements, 2026-10-27. Product ownership remains in PROJECT.md.

## Ownership and behavior

- `WindowsResourcePolicy` follows hidden/minimized versus visible state. Windows background mode lowers CPU, I/O and memory priority; EcoQoS is enabled. The main app's job caps total-system CPU at 0.5% while hidden. Provider helpers silently leave that job. No working-set trimming API is used. Shutdown restores normal scheduling before native resource cleanup; closing a job handle alone does not remove its process limit.
- `App.RequestDashboard` lifts the cap before XAML construction or queued activation. Requests coalesce. `MainWindow` activates the shell before restoring its page on a later dispatcher turn. Hiding releases the frame/cache and queries/timers; destination/back-stack metadata survives. Version-4 tray callbacks accept selection, keyboard selection and double-click.
- `TelemetryCoordinator` publishes telemetry before scheduling independent compact outlooks and calibration. It leaves a full polling delay after a pass and coalesces overlapping work. Compact calculations cannot downgrade completed detailed results; late detailed results cannot acquire newer quota anchors as fresh.
- `CalibrationProcessService` owns one child using the existing evaluation executable and private standard streams. Its CPU budget follows visibility: 0.5% background, 2% foreground. Private commit is limited to 384 MiB and managed heap to 256 MiB. Background-started slices allow ten minutes with a five-minute cooldown; foreground-started slices allow three minutes with a one-minute cooldown. Timeouts, failures and parent exit terminate the owned worker and observe its pipe readers.
- `CalibrationWorkerHost` accesses only app-owned SQLite data. Numeric fit parameters are cached separately from telemetry. Keys hash exact binary features/targets/penalty plus solver version. Checksums and finite/dimension checks protect reuse. Entry count and SQLite pages bound storage; byte exhaustion evicts old entries. Cache failure falls back to calculation. At most 256 fits remain in worker memory.

## Calculation invariants

The model and chronology remain unchanged. The feature index retains complete prefixes for relevant sessions, including old starts/ownership metadata, and preserves input order for ties and token-share dictionaries. Token replay and cost-interval construction both use it. Variable lookbacks include exact duration in their memo key. Independent full-history feature construction remains the parity oracle.

Replay memoization retains complete numeric rows and exact training/validation identities; late backfills invalidate them. Per-evaluation caches remain bounded to 8,192 rows and 4,096 points. Durable ridge reuse is separately bounded to 16,384 entries and 32 MiB plus SQLite journals. Promotion still checks collection-time qualification before reconstructed comparisons; unused incumbent replays remain lazy.

## Evidence and practical limits

Before the first resource fix, the process held 542 MiB total working set / 453 MiB private commit. A busy sample used approximately one core and allocated about 206 MiB/s; repeated forecast replay dominated its stack. Commit `ea4334f` temporarily paused detailed work while hidden. Its tray-only idle sample held 122.4 MiB total working set / 89.9 MiB private commit and accumulated zero CPU over 20 seconds. These are point-in-time measurements, not hard bounds, and differ from Task Manager's private working set.

With isolated calibration enabled, a 20.66-second live background sample measured 0.000% total CPU in the main app and 0.492% in its worker on 16 logical processors. Private commit was 198.3 MiB in the app after a dashboard visit and 265.8 MiB in the active worker; total working sets were 100.3 / 305.5 MiB. Calibration still needs temporary memory. The worker releases it between slices.

The packaged worker completed an initialized empty-database fixture in 4.373 seconds in background mode and 1.172 seconds in foreground mode, returned typed forecasts, honored cooldown/reset and reaped its child on cancellation. It ran from the shared self-contained installation. Deployment checks passed. A live hide-to-open activation through `--open-dashboard` and the common tray handler reached a captured visible shell in 2.040 seconds, including command launch and screenshot overhead, while real calibration was active. History was still loading asynchronously. This verifies the handler/window restoration; physical tray click delivery and user visual acceptance remain separate.

An initial real-data worker stack exposed full-history rescans in cost-interval feature construction, despite indexed token replay. That path now uses the same index. Regression coverage compares variable-lookback indexed features with the original builder, exact uncached/warm/reopened fit parameters and chronological reports, corrupted/full caches, polling during blocked calibration, superseded anchors and delayed compact results arriving after detailed results.

## Rechecking resource use

Measure CPU-time deltas over 15-30 seconds and divide by elapsed time and logical processor count. Include parent and child, working set and private commit, cold collection, calibration and cooldown. Confirm collection with content-free read-only counts/times from app-owned SQLite. Use `artifacts/tools/dotnet-counters` for allocation/GC and `dotnet-stack` for a current busy stack. Do not export account keys or source payloads in performance reports.

Final checks: 596 Core tests passed. Windows build/publish/install succeeded without warnings/errors. The real-data cost pass built 1,140 intervals from 17,742 quota rows, 78,309 token events and 6,814 metadata observations in 11.317 seconds; first/middle/last feature projections matched the independent full-history builder. Durable fit count grew from 194 to 4,715 in the live calibration runs. A hidden app's native exit stack identified MRM resource cleanup under the old CPU cap; after restoring scheduling before teardown, the final hidden instance with an active calibration worker exited gracefully in 148 ms; its child was reaped. Further UI testing was stopped when the user cancelled computer use with Escape.


Final tray-only sample for source commit a64f3b0: main app 0.000% total CPU / 101.6 MiB private commit / 135.3 MiB working set; active worker 0.517% total CPU / 260.9 MiB private commit / 300.5 MiB working set over 20 seconds. Installed app/shared libraries and the four worker entry files matched the validated publish. Working tree clean; no push.
