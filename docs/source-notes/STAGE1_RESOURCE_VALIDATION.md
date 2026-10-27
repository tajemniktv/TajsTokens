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

## Independent discovery and bounded fallback

The next slice separates the successful discovery watermark from rollout catch-up.
An in-process pending set carries uncommitted catalog threads; the durable watermark
still stays behind the oldest unapplied work. Startup performs full reconciliation,
so a restart reconstructs the pending set even after interruption. A source-generation
change resets discovery state without deleting retained observations. Full discovery
continues on its existing five-minute cadence; immediate pending retries do not
force it to run every poll.

Filesystem fallback now uses the same 8-recent/8-rotating-older work budget. The
ingestion owner skips an unchanged source only when its already-verified physical
identity, consumed prefix, length/write time, parser version and durable checkpoint
all still match. Unknown identities or missing checkpoints require ingestion.
Appends still enter the existing strong prefix verifier; no suffix-only shortcut
or replacement of content verification was introduced. The fallback still enumerates
file names; this change bounds ingestion, not the cost of every directory listing.

Validation:

- Twelve focused ingestion tests passed, covering backlog/restart fairness, immediate
  missing-file recovery without full rediscovery, source-generation changes,
  periodic metadata corrections and filesystem fallback.
- The 40-file fallback fixture catches up in bounded passes, skips all unchanged
  files warm, reopens only one appended file and recovers a missing durable checkpoint.
  Its untyped records intentionally replay their prefix when semantic resume state
  is absent; that recovery behavior is preserved.
- Full Core suite: 653 passed, none skipped; 1 minute 7 seconds.
- Windows build/publish/install: passed, zero warnings/errors; 58.02 seconds.
  It gracefully replaced the first-slice app and restarted the updated version.
- Installed Diagnostics at 23:35 local reported **one catalog row read; full discovery
  reused**, retaining coverage measured at 23:34:51. One rollout still failed and
  remained explicitly stale. This confirms retries no longer require full discovery
  while preserving the source-gap warning; it does not establish account-wide coverage.

PERF-002, PERF-003 and PERF-005 are complete. Final resource/latency acceptance,
memory reduction and user visual acceptance have not been established.

## Projection, maintenance and ordered writes

This slice is implemented and installed, closing PERF-006, PERF-008 and P-01.
The trigger-owned token projection revision covers insert, deletion, retirement,
metadata/time repair and actual row edits. Its singleton lookup replaces the previous
`COUNT(*)`/`MAX(rowid)` stamp scan. Context-only batches advance the existing broad
evidence revision while sharing unchanged numeric totals/hourly arrays. This does
not narrow forecast freshness to token counts. Physical database replacement clears
projection and initialization caches, including pooled native connections; a restored
file with equal revision values cannot reuse the previous file's totals.

Unchanged ingestion no longer restamps durable checkpoints. A full 128-record batch
also avoids a duplicate final checkpoint write. Unchanged rollout-file metadata updates
last-seen at most once per five minutes; changed size/session/path updates immediately.
This timestamp describes discovery freshness, not a token's original capture time.
Candidate-only fingerprint reads and a bounded 4,096-edge comparison avoid reloading
app-owned relationships on unchanged polls. Source edges are still read independently,
changed edges reconcile immediately and the existing five-minute reconciliation checks
for app-owned corrections.

Legacy path scrubbing now has its own durable version marker and atomic migration.
Ordinary writers continue sanitizing new data. Initialization is reused for the same
physical file and repeated after replacement; unsupported newer schemas remain refused.

The original reset plan scanned retained quota rows and used a temporary B-tree for
seven ordering terms before selecting each stream's latest 512 rows. Intelligence
schema 7 maintains a rebuildable stream registry and expression index in the app-owned
database. A refresh without new evidence or a time-qualification change reads no
history and performs no durable update. Otherwise each registered stream seeks its
latest 512 rows, preserving the former raw partition boundaries and shared eligibility
policy. More than one tail of pending input, historical/same-time inserts, edits,
deletion, database replacement, future-row maturity and clock rollback rebuild from
all retained quota evidence. The migration and rebuild commit together; failed cache
writes retain retryable input state. This retains the existing bounded ordinary
refresh policy; it does not introduce a new reset classifier.

The native fleet plan previously ranked all retained turns (2,658 in the inspected
read-only source), then applied its limit. Ranking now includes only the selected
catalog IDs. No index or other mutation is made to the Codex-owned database. The
regression includes 10,001 unrelated historical threads and retains the selected
thread's latest turn/item, even where the previous limit could hide it.

### Token transaction experiment (P-01)

Synthetic fixed-time cumulative observations use the same current reducer, revision
triggers and WAL policy for both lanes. This isolates transaction grouping; it is not
a comparison against the prior released binary or live Codex workload. Results were
measured with the temporary prototype subsequently retained in the existing evaluation
CLI; [commands](../DEVELOPMENT.md#ingestion-performance-fixtures) reproduce the fixtures.

| Observations / pass | Single transactions | Batches of 128 | Total native tokens in both |
|---|---:|---:|---:|
| 1,000 / first ingestion, single lane first | 2,190 ms | 222 ms | 110,000 |
| 1,000 / first ingestion, batch lane first | 2,137 ms | 403 ms | 110,000 |
| 10,000 / first ingestion | 21,733 ms | 2,195 ms | 1,100,000 |
| 10,000 / duplicate replay | 2,258 ms | 662 ms | 1,100,000 |

At 10,000 observations, transaction count falls from 10,000 to 79. Measured initial
CPU time falls from 5,921.9 to 1,671.9 ms; managed allocation falls from 163,684,840 to
142,807,008 bytes. Reversing lane order still shows a material elapsed-time benefit.
These figures justify adoption within the existing reducer owner. They do not
establish lower app-plus-worker retained memory or a physical disk I/O reduction.

Production token batches keep observation order, deduplication identity, reset/epoch
decisions and next-counter-state/event writes in one transaction. The semantic batch
can commit first; its existing projections are idempotent. The source byte checkpoint
advances only after both lanes succeed. Regressions compare serialized accounting
snapshots across ordered single/batched ingestion, reset variants, duplicates and
restart. An injected second-token failure rolls the entire token batch back, leaves
the checkpoint behind, and a fresh ingestion owner replays to the correct EOF/total.

The Qodo checkpoint submission and original-request recovery both failed its review
API compatibility gate. No completed review result or findings were returned; these
attempts do not provide review coverage.

### Verification and prefix cost

- Full Core suite: **668 passed**, none skipped, 1 minute 14 seconds. Legacy test
  fixtures now actually remove newer derived structures when recreating an older
  schema, and use a new repository owner to simulate an older-process upgrade.
  Migration rollback/retry, account/source boundaries and retained evidence assertions
  remain covered.
- Windows solution build/publish/install: passed, **zero warnings/errors**, 59.65 s.
  It gracefully replaced PID 24000 with PID 25732. Installed identity:
  `1.0.0+6ce8e40e6154f3f80a12e1e3e96f94075566d82a.dirty` (this slice's uncommitted
  build, before its eventual commit). The running version changed.
- Installed read-only query plans confirm two singleton primary-key revision seeks
  instead of a token-table scan, and a covering expression-index reset-tail seek
  without the old ranking temporary B-tree. The native source contained 2,662 turns
  at this later inspection; fleet ranking changed from an index scan to indexed
  `thread_id` searches. A small per-thread ordering temporary B-tree remains. No
  percentage of live CPU or disk time is attributed to these plans.
- The retained CLI's 1,000-observation reversed-order smoke measured 296.6 ms for
  batched first ingestion versus 2,256.1 ms single transactions, preserving 110,000
  tokens in each lane and their duplicate replays. Its disposable fixtures were removed.
- Installed Overview loaded allowance/work history. Diagnostics at 00:13 local
  reported nine new complete records, 14.6 MiB prefix-hashed, 11.5 KiB complete-record
  bytes decoded and two checkpoint writes across three scanned files. Full discovery
  was reused with three catalog rows read. This was **active append**, not unchanged
  idle. One unresolved rollout still remained stale; quota collection continued and
  missing-window/source-gap labels stayed explicit.

The prefix fixture on this Ryzen 7 5800X / 16-logical-processor / 31.9 GiB Windows
host measures logical work through the existing boundary-ingestion owner:

| Phase | Source bytes | Prefix bytes hashed | Complete-record bytes decoded | Checkpoint writes | Elapsed |
|---|---:|---:|---:|---:|---:|
| Cold, 1,024 records | 1,078,272 | 1,078,272 | 1,078,272 | 8 | 36.4 ms |
| Unchanged | 1,078,272 | 0 | 0 | 0 | 14.1 ms |
| One appended record | 1,079,325 | 1,079,325 | 1,053 | 1 | 3.2 ms |
| Rewritten consumed prefix | 1,079,325 | 2,158,650 | 1,079,325 | 9 | 15.7 ms |

The rewrite is detected and replayed under a new generation. Strong append verification
still scales with the consumed prefix; a rewrite can hash it again when the first
bounded replay checkpoint precedes the initial verification endpoint. This cost is
measured and visible, not replaced with a suffix-only integrity assumption. Short
fixture timings include JIT/cache effects and are not a live workload latency SLA.

PERF-001, PERF-004 and PERF-007 remain open. The overall resource/restore exit gate,
physical I/O characterization and reduced combined retained memory remain unproven.
