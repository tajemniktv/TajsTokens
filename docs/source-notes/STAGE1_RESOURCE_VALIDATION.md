# Stage 1 resource and responsiveness validation

27 October 2026. [PROJECT.md](../../PROJECT.md#current-work) owns status and acceptance.
This note records reproducible measurements and their boundaries; it is not a second
checklist. The first implementation slice starts from `36faefb` over application
baseline `e609aeb`.

## First slice

The main UI/tray process no longer enters a hard-CPU-limited job or process-wide
background scheduling. Routine forced compacting GC is removed. EcoQoS remains a
hint, with desired/applied state separated so failed native calls retry; its state
and Windows error code are visible in Diagnostics. The calibration child's existing
CPU/memory budgets, lifetime cancellation and cleanup remain unchanged.

Collector snapshots now carry typed completion identity. Fleet requests deduplicate
that identity, allow one database read in flight and collapse queued generations to
the newest. Cached counts expire by clock time and reclassify stale native turn
status without another database query. Tray presentation coalesces separately from
the captured inputs for every alert transition. No accounting/forecast algorithm or
source-data retention contract changes in this slice.

## Measurement method

Run from the repository root:

```powershell
./tools/performance/Measure-AppResources.ps1 -Seconds 30 -OutputPath .codex/temp/stage1/sample.json
```

Use `-LaunchBackground` only when no instance is running. Record the observed
visibility state separately; deployment can reopen the dashboard. The sampler
qualifies the installation path, records its build identity and samples app/worker
CPU, private commit, working set and Windows process I/O. I/O counters are logical
process I/O, not physical disk traffic. CPU is normalized by logical processors.
Worker samples can cover only part of the parent sample when a slice starts/ends.
The host has 16 logical processors.

For managed allocation/GC, use the existing SDK tool, for example:

```powershell
$appProcess = Get-Process TajsTokens.App
./artifacts/tools/dotnet-counters.exe collect --process-id $appProcess.Id --duration 00:00:20 --counters System.Runtime --format json --output .codex/temp/stage1/runtime
```

Keep raw local diagnostics under `.codex/temp/stage1/`. No prompt text, source bodies,
account keys or credentials belong in a performance report. Treat startup, append,
backlog, calibration, settled idle and reset-boundary samples as separate conditions.

## Baseline observations

Installed identity was `1.0.0+e609aebee78712dd26c2eae040005e6dcf2377f3.clean`.
The app was initially stopped and launched explicitly in background mode.

| Condition | App CPU | App final private commit | Worker CPU / private commit | Boundary |
|---|---:|---:|---:|---|
| First 30 seconds after background start | 0.62% | 66.2 MiB | No sampled child | Startup, not settled idle |
| 30 seconds after observed minimization | 0.157% | 320.0 MiB | 0.494% / 266.7 MiB | Worker present for 11.7 seconds; parent private peak 321.2 MiB |

The minimized parent accumulated 72,657,349 logical read bytes and 11,330,040 logical
write bytes. Its low 32 MiB working-set peak did not imply low retained private
commit. Do not interpret these logical counts as continuous physical disk activity.

A separate 20-second runtime capture, overlapping dashboard activation, recorded
239,210,752 allocated bytes across 19 one-second allocation samples and 88.577 ms
of aggregate GC pause time (largest one-second pause aggregate: 39.5 ms). This is
not a clean hidden-idle comparison or a measurement of one collection's pause.
Another process sample overlapped activation and is excluded from the hidden table.

## First-slice measurements and remaining limits

After an observed minimize, the first-slice app accumulated 0.0164% total-system CPU
over 29.73 seconds, ended at 345.7 MiB private commit and accumulated 27,434 logical
read bytes / 116 logical write bytes. Its worker averaged 0.463% over a 20.45-second
partial sample and ended at 270.3 MiB private commit. The app's working-set peak was
446.2 MiB. A preceding post-install sample was not visibility-controlled and is not
used as a hidden comparison.

These intervals contain different naturally occurring work and worker phases; they
are observations, not a controlled speedup ratio. In particular, combined retained
memory has not fallen and the Stage 1 resource problem is not closed. Removal of
forced GC can leave more pages resident; shrinking working set was never the goal.
The remaining invalidation, preparation and collection work needs measured repair.

The installed dashboard restored after minimization, continued displaying new quota
timestamps, and Diagnostics exposed `Normal scheduling; EcoQoS disabled; calibration
has an independent budget.` The native workload generation remained explicitly stale
because one rollout failed to refresh; successful quota reads did not erase that
source gap. Coverage was still being recomputed on successive passes, corroborating
the pending PERF-005 retry-amplification work. No source path was exported here.

The first accessibility observation after opening was unavailable. A later observation
showed the dashboard functioning. A native stack captured the UI thread waiting in
the WinUI desktop message loop, not blocked in a managed collector. These observations
do not establish a continuous UI hang, its cause, or a numerical restore latency.

## Automated and installed verification

- Focused scheduling/fleet/background regressions: 13 passed.
- Full Core suite: 652 passed, none skipped; 1 minute 36 seconds.
- Windows solution build/publish/install: passed, zero warnings/errors; 55.61 seconds.
- Installation restarted the app and preserved its sibling data directory through
  the existing deployment backup procedure. The installed first-slice identity is
  `1.0.0+36faefb4d55f0496d5e479b8d6fce81624cb09f7.dirty`; this includes the uncommitted
  first-slice changes, not the original planning commit alone.
- The restarted app's Overview displayed current allowance and selected token history.
  Actual minimize and subsequent restore through the existing activation handler
  were observed. Repeated restore/physical tray acceptance remains in progress.
- Qodo fast checkpoint operation `6dc94425-6d9e-45c1-bd5f-5b5be91e7b7d` terminated
  with service state `failed`; no findings or completed-review coverage were returned.

Performance targets and per-phase counters are not established by these initial
samples. PERF-001 and the broader Stage 1 exit gate remain open. Removing the cap
alone is not evidence that repeated work or combined resource use has fallen.
