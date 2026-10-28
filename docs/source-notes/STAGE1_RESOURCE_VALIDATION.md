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

## Presentation lifetime and selected-history reuse

The presentation slice retains the existing frame rather than replacing it on each
hide. Detaching it runs the pages' existing unload/cancellation paths. A single
30-second hidden deadline releases large ledger/history/conversation/raw-row read
models and generated visuals while retaining controls, navigation, filters and
lightweight selection identities. A user-selected schema baseline remains retained.
Restoring reattaches the same frame; existing page loaders reconstruct evicted data.

The overview can reuse its selected history for at most 30 seconds when selection,
settings instance and database generation still match. A persistent read-only SQLite
connection supplies `data_version`; its opaque connection identity prevents comparing
versions from different connections. Every other database connection's commit
invalidates reuse, including backfill and derived writes. Unknown/unavailable identity
disables reuse. A before/after check refuses to cache a history query spanning a
commit. This broad read-cache key is deliberately **not** a calibration-evidence key.
Live quota context remains independently projected from the latest telemetry.

Overview and Diagnostics coalesce queued visual updates. The overview also reuses
unchanged quota tile objects and avoids rescanning its selected ledger for summaries
on every live update. Alerts retain their separate lossless input path. Planner inputs
survive hiding; an estimate is invalidated and must be made against current anchors.

Automated verification: the full Core suite passed **670 tests**, none skipped, in
1 minute 18 seconds. New read-generation regressions distinguish commits from
rollbacks, prevent reuse across new connections, preserve cancellation and avoid
creating missing databases. Final Windows build/publish/install passed with zero
warnings/errors in 57.13 seconds, installing
`1.0.0+ed64cc0bdf21c11acba549b77ea6b888ca072fa6.dirty` as PID 38996.

Installed native UI inspection confirmed that a long minimize/restore retained the
Planning tab and temporary 2.5-hour / model / effort inputs. Those temporary inputs
were returned to their original defaults afterward. Current provider timestamps
continued advancing; the absent five-hour window remained explicit. This is runtime
state-retention evidence, not a numerical latency or comprehensive visual acceptance.
Diagnostics subsequently reported **one hidden history release pass and two overview
history reads**, confirming that the deadline ran and restoration reloaded evicted
history. A later short navigation return reported **three history reads and one
qualified cache hit**, confirming reuse under unchanged generation/settings/selection.
PERF-004 is complete. PERF-001 and PERF-007 remain open; broad visual acceptance and
numerical restore/physical-key latency are separate from these native state checks.

An observed-minimized 35-second process sample starting at 23:27:21 UTC captured both
the parent and a working calibration child throughout about 34.9 seconds. Parent:
0.0084% total-system CPU, 277.0 MiB final private commit (280.7 MiB peak), 373.6 MiB
peak working set, 15,617 logical read bytes and 4,755 logical write bytes. Worker:
0.5069% CPU, 253.9 MiB final private commit (272.3 MiB peak), 313.1 MiB peak working
set and zero additional logical bytes during this interval. Final combined private
commit was **530.9 MiB**. This is still too high for a solved background-resource
claim; it motivates the remaining qualified calibration scheduling work. It is not
a controlled memory-improvement ratio against the earlier naturally occurring phases.

## Opt-in phase diagnostics and installed profile

`TajsTokens-Work` is an opt-in EventSource with fixed numeric phase identifiers,
operation pairing, elapsed time and aggregate work counters. It carries no paths,
session/account/model identifiers or source payloads. Disabled scopes allocate
nothing. Unknown counters remain -1 (null in the report), distinct from measured
zero. A completed scope means its owner returned normally, not that a forecast was
fresh or statistically validated. Nested timings overlap.

Use the standalone offline reporter; it does not enter the app dependency graph:

```powershell
dotnet build tools/performance/TajsTokens.PerformanceReport
artifacts/tools/dotnet-trace collect -p <pid> --providers TajsTokens-Work --buffersize 16 --rundown false --duration 00:00:35 --output .codex/temp/stage1/app.nettrace
dotnet run --project tools/performance/TajsTokens.PerformanceReport --no-build --no-restore -- .codex/temp/stage1/app.nettrace
```

Attach separately to the parent and worker. Attaching halfway through a scope can
miss its start or both boundaries; an empty custom-provider capture is an error,
never evidence of zero work. The reporter emits only aggregate custom-provider
fields and checks source errors/events lost. External EventPipe capture required
self-describing EventSource format on this installed runtime; the reader matches
event names because EventPipe assigns self-describing event IDs. An in-process
listener alone did not establish external-capture compatibility.

External synthetic verification captured 268 starts and completions, no unfinished
scopes, source errors or lost events. It observed 258 token transactions and 512
submitted observations across two single/batched comparisons of 128 observations.
This verifies diagnostics plumbing, not a production performance improvement.

The installed visible/active-append capture at 01:05 local on 2026-10-28 contained
one collection (3,685.8 ms), one catalog read (1.36 ms, two rows), two rollout passes
(85.65 ms, 43 records, 81,996 complete-record bytes), four prefix checks (19.94 ms,
24,592,739 logical hash bytes), two semantic commits (34.31 ms) and two token commits
(10.38 ms, seven submitted observations). Projection took 306.57 ms for 910 result
rows; the fleet read took 32.14 ms for 426 unique returned thread turns. These are
small samples, not representative p95 budgets. Fleet rows count distinct returned
turn evidence; feature-index preparation measures index construction, not all
feature evaluation. First-frame timing begins at app visibility transition and
ends at the first Rendering callback after attachment; physical input/IPC delivery
and later selected-history completion are outside that measurement.

Runtime metrics were collected separately with dotnet-counters 10.0.750501. Across
34 one-second parent intervals: 66,911,608 allocated bytes, 1.094 CPU seconds and
27.24 ms aggregate GC pause time (largest one-second pause aggregate 21.48 ms).
Last-collection heap minus fragmentation was approximately 31.3 MiB; GC committed
memory was 101.5 MiB. The worker exited during its capture: nine one-second intervals
contained 319,352,768 allocated bytes, 2.734 CPU seconds and 5.218 seconds aggregate
GC pause time (largest one-second pause aggregate 698.9 ms). Last-collection occupied
heap estimate was 228.9 MiB, GC committed memory 249.1 MiB. Last-collection occupancy
is not exact current live heap; a one-second pause aggregate is not one GC pause.
The worker phase capture contained no boundaries and cannot time its calibration.
A managed stack in a later slice showed `PredictHorizonCore` filtering full token
prefixes inside composed quota validation. This and allocation pressure motivate
qualified preparation reuse; neither observation proves every calibration cost.

Profiling adds buffers/overhead. Its working set/commit is not an unprofiled resource
acceptance result. Process logical I/O and hash counters remain separate from
physical disk traffic. PERF-001 and PERF-007 remain open.

Validation: 671 Core tests passed, Windows app build/install passed with zero warnings
or errors, and the offline reporter built successfully. The installed version was
replaced by this instrumented build. Qodo review could not complete because its
service returned an API compatibility failure; no Qodo coverage is claimed.

## Workload replay preparation

The existing evaluation-local replay cache now owns one sorted input preparation
and an indexed, bounded feature cache. It uses binary prefix views for timely
evidence and filters late/unknown collection times where needed. The existing
feature builder, prequential fit, selection and uncertainty calculations still
own the answers. Numeric feature retention is capped at 8,192 entries; hitting
the cap falls back to computation, not truncated history.

Historical workload features retain the existing semantics: they reconstruct an
earlier event-time origin from evidence available at the enclosing prediction
origin. A collected-by-origin replay does not silently become a different
historical training model. Timely observations cannot alter earlier features as
that enclosing cutoff advances; newly available late evidence advances a
conservative generation and invalidates feature reuse. Unknown capture stays
excluded. Reconstructed and collection-qualified inputs use separate keys.
Replacing any source feature list rebuilds preparation even at identical counts.
This scope retains no raw dataset across refreshes or worker exits. Existing
exact-input durable numeric fits continue to preserve interrupted-slice progress.

A synthetic comparison loaded the installed pre-change Core assembly and the new
assembly in separate processes. Both used the existing shared replay cache, the
same 360 timely token observations, 90 metadata rows, 40 chronological prediction
origins, two horizons and a two-origin warmup. All 80 serialized predictions had
the same SHA-256 in both binaries. First order: 107,931,232 -> 72,213,528 allocated
bytes and 372.90 -> 233.64 ms. Reversed process order: 107,931,232 -> 73,948,728 bytes
and 555.43 -> 399.91 ms. That is 31–33% fewer allocated bytes and 28–37% less elapsed
time in this bounded synthetic fixture. It does not establish the same improvement
for the retained 120-day installed corpus or lower steady private commit. The
local reflection probe and aggregate outputs are in `.codex/temp/stage1/`.

Regression coverage compares cached and independent predictions across horizons,
reverse origins, late token/metadata/context collection, unknown collection times
and same-count model/value replacement. Existing chronological model/backtest
tests remain part of the full Core suite.

An additional freshness correction carries an opaque process-local storage
generation from the native projection into coordinator token provenance. Database
replacement can reuse a revision number; a delayed calibration must then mark its
workload stale. Tests cover equal revisions with and without replacement and the
real physical-replacement provider path. This identity is not a durable evidence
identifier and does not change retained accounting.

PERF-007 remains open for evidence/maturity scheduling and overlapping dataset
read reuse. PERF-001 retains its scenario/physical-I/O/numerical-budget gate.

Validation for this slice: all 674 Core tests passed (none skipped); the serial
Windows solution build/install completed in 59.45 seconds with zero warnings or
errors and restarted the installed app as PID 14172. Computer Use was stopped by
the user before the additional first-frame capture, so no new UI timing result is
claimed. The initial full-suite run exposed two comparisons that included distinct
opaque fixture-database identities; those now assert different identities and
compare the accounting payload separately. The final full-suite run passed.

## Evidence-qualified scheduling and overlapping reads

PERF-007 is implemented in the existing calibration owner. The app database has a
derived `calibration_evidence_state` singleton with input/feature revisions and a
schema signature. Transactional triggers track insert/update/delete of quota,
native token, workload and context evidence and rollout source membership. True
no-op updates and rollout path/size/last-seen maintenance do not advance it.
Derived forecast/reset/cache writes do not advance it. A changed column signature
or missing owned trigger is repaired atomically and advances the revision. Missing
or unknown schemas disable reuse; they never suppress the original worker path.
This table retains counters/schema metadata, not an additional source dataset.

The scheduler also qualifies physical storage identity, complete quota anchors,
freshness and user associations. A completed unchanged generation can be returned
with its original generation/anchor times; it is not relabeled as a new forecast.
Its expiry is bounded by forecast horizons, quota resets, observed activity expiry,
future-dated/late-collected feature availability and the 30-day workload read's
moving two-hour lookback. Feature-time qualification is reused between quota-only
changes. Clock rollback invalidates reuse. Failed/interrupted slices do not become
completed cache entries; existing exact numeric fits still survive interruptions.
Evidence changed during a slice makes its returned results stale and prevents
reuse. The coordinator preserves that stale flag independently of token revision.
Cheap provider-anchored pace remains independent of detailed calibration.

Within a worker, one bounded widest feature snapshot supplies overlapping reads
for quota lanes and the 30-day workload pass. Quota reads remain narrow in the
original reader, preserving account assertions at their actual time. Each feature
projection uses the original SQL active-session query: inferring that set from
already-filtered metadata would omit old valid prefixes when recent retired-source
metadata selected a session. Source/capture timestamps and stable row order are
preserved. Input stamps before/after capture and projection reject mixed generations;
derived writes do not invalidate these reads. The worker uses one evaluation time
for quota and workload; its output preserves that actual time even when a slice
takes longer. No read transaction or raw dataset survives the worker.

A wider feature read reaching its supported bound falls back to the requested
narrow read; it must not make a previously supported range unavailable. Ledger
reads and ranges outside the shared window use the original reader. Shared
active-session materialization is also bounded, with the same fallback.

Synthetic measurement: 20,000 normalized token rows, three overlapping reads
(two 120-day anchor cutoffs and one 30-day workload range), warm SQLite/file caches,
no quota rows and no model fitting. Both paths returned 60,000 token references
with identical serialized evidence hashes. Normal then shared: 25,165,896 ->
9,046,992 allocated bytes, 370.78 -> 172.70 ms. Reversed order: 25,164,648 ->
9,039,560 bytes, 413.97 -> 169.11 ms. This is about 64% fewer allocations and
53–59% less elapsed time for that fixture, not a whole-app improvement ratio.
Local probe/output: `.codex/temp/stage1/probe`, `reader-benchmark*.jsonl`.

Targeted regressions cover source correction/deletion/rollback, derived/no-op
writes, trigger recovery, equal-revision physical replacement, future collection,
window boundaries, expiry, rollback of the clock, failures, mid-slice changes,
account cutoffs, retired metadata, source-row parity and bounded-read fallback.
All **682 Core tests passed**, none skipped (1 minute 20 seconds). The serial
Windows solution build/install passed with zero warnings/errors (55.34 seconds)
and restarted the daily app as PID 276. No UI interaction was attempted.

### Resource observation and remaining acceptance

The existing resource sampler now includes the installation-qualified app's child
tree, including transient helpers, with PID creation-time qualification. Earlier
app/calibration-only observations may omit these helpers. Combined private peaks
sum one sampling cycle instead of unrelated per-process maxima. CPU/I/O for short
children covers only their observed portion; shorter-than-interval processes and
start/end gaps remain unmeasured.

A 35-second unprofiled sample of this installed build observed parent CPU 0.234%
of 16 logical processors over 34.57 seconds, final private commit 283.1 MiB and
sampled private peak 298.9 MiB. A calibration child was observed for 10.42 seconds:
2.174% CPU, 260.1 MiB sampled private peak, 342,558,388 logical read bytes and
102,646,976 logical write bytes. Its `conhost` helper used 4.72 MiB private commit.
Combined sampled private peak was **553.2 MiB**; final 283.1 MiB was after the child
exited and must not substitute for the active-worker peak. Visibility was not
observed because Computer Use remains stopped. This is not a hidden-idle or
controlled before/after comparison, and the overall memory problem is not solved.

Optional PhysicalDisk raw counters are separate system-wide observations. In that
35.99-second device interval C: reported 11,886,080 read / 146,059,264 write bytes,
D: 13,258,752 / 409,600 and E: 0 / 421,376. These include other applications and
cannot attribute disk traffic to TajsTokens. Process transfer counters do not
establish per-process physical disk attribution.
The method follows Microsoft's [PhysicalDisk counter definition](https://learn.microsoft.com/en-us/previous-versions/aa394308(v=vs.85))
and [two-sample counter calculation](https://learn.microsoft.com/en-gb/windows/win32/perfctrs/calculating-counter-values).

PERF-001 remains open for numerical budgets, controlled scenario comparisons,
restore timing and resource acceptance. Sharing preparation and reads is not
evidence that the combined memory target has been met.

### User-operated restore capture

The user chose to perform the hide/restore manually; agent UI control stayed
stopped. During the 90-second capture the user reported "no stalls, pretty normal
I think". The installed app emitted two successful visibility-to-first-frame
measurements: **30.30 ms and 96.31 ms**. Two selected-history reads completed in
745.76 ms and 773.30 ms. One hidden-history eviction was observed. All 52 started
phases completed, with zero lost events or source errors. These are two observed
transitions, not a population p95 or physical input-to-display latency.

Three collector passes also completed during that interval (3,715.45/3,729.00/
3,896.90 ms by sorted duration), alongside three fleet reads and continued append
ingestion. The capture verified that the shell returned before the history query
finished. It supports bounded restore responsiveness and the user's manual pass;
it does not close the memory/resource gate. Local evidence is
`manual-restore-phases.nettrace` and `manual-restore-phase-summary.json` under
`.codex/temp/stage1/`.

## Retained evidence memory

The final workload pass now releases the wider quota-phase feature cache after
projecting its 30-day range. It retains complete prefixes for relevant sessions,
including starts older than 30 days. A later wider request falls back to the
original reader rather than receiving truncated evidence. If source evidence
changes between phases, the final read reloads only its needed range. If detailed
quota calibration was skipped, the worker never loads an unused 120-day range.

The existing SQLite dataset reader also shares repeated common text within each
read: session/source labels, model/effort, event/contract labels and related common
metadata. Sharing uses ordinal equality, at most 16,384 values of at most 512
characters. Unique observation/record/turn IDs stay unpooled. The dictionary is
local to one read; there is no global string-intern pool or additional durable
retention. Timestamps, source identity values, row order, bounds and forecasting
calculations are unchanged.

An isolated managed-retention experiment loaded the installed pre-change reader
and the new reader over the same synthetic 20,000-token / 20,000-metadata fixture.
Both returned identical serialized evidence hashes. Normal order: 19,243,856 ->
9,010,672 retained managed bytes; reversed order: 19,129,480 -> 9,010,648. That is
about **53% less live dataset memory**. Token-session string instances fell from
20,000 to 200 distinct session values; metadata source-string instances fell from
20,000 to one value. Full collection was used only in the isolated measurement
process to measure retained objects; no forced GC was added to the application.
These are dataset-retention measurements, not process-private-commit savings.

The old installed Infrastructure assembly SHA-256 was
`937b101a7d3799a2dc4962b7335f656c7e544153984a4f3cf329906fb3b25d09`
(source content through a4d2380); the new installed assembly is
`4f9b538cb2b6c1b211fa4ffdfcccfe57adf7efad04d60b0e98510d3d6e568171`.
Local reproduction is in `.codex/temp/stage1/probe` with
`reader-retained-memory*.jsonl`. Bounds/replacement/fallback, common-value sharing
and old-prefix retention are covered by targeted regressions. All **686 Core
tests passed**, none skipped (1 minute 26 seconds). The serial Windows build and
installation passed with zero warnings/errors in 69.75 seconds, restarting the
daily app as PID 36716.

### Replay allocation and process attribution

Quota replay now estimates each origin's pace once and projects both targets from
that rate, using a prefix view instead of repeated array copies. Model formulas,
selection and evidence remain unchanged. The isolated comparison exercised all
11 candidates, three horizon choices and dense weekly/five-hour-cycle fixtures:
14,201 serialized trials matched (SHA-256
`2a0c7bf9cfc3dd2dd3cf65ad22921c4ed01ccf47b8853db62067acffd11d41b4`).
Allocated bytes fell from 1,552,769,576 to 221,938,968; elapsed time was 8,681.04
versus 3,352.30 ms. Reversed order confirmed 1,552,766,376 versus 221,937,504 bytes
and 9,458.95 versus 4,567.00 ms. These fixture results are not whole-app ratios.
Local evidence: `pace-replay-benchmark*.jsonl` under `.codex/temp/stage1/`.

Opt-in `MemoryObserved` events report process private commit, last-GC occupied and
committed heap, and SQLite allocator current/high-water bytes. GC fields describe
the last collection, not current live objects; these overlapping counters cannot
form an exact partition. The installed parent sample reported 273.43 MiB private
commit, 39.81 MiB last-GC occupied, 116.89 MiB last-GC committed and 6.33 MiB SQLite
allocation (14.88 MiB SQLite high-water). SQLite does not explain the bulk of this
process. The remaining native/CLR/UI memory has not been individually attributed.
The trace/report completed 37 phases without lost events or source errors.
Local evidence: `native-memory-attribution*` under `.codex/temp/stage1/`.

After the user confirmed hiding the dashboard, the earlier 120-second sample
averaged 489.27 MiB across app, worker and helpers, peaked at 566.70 MiB, and ended
at 449.94 MiB. The app alone ended at 258.43 MiB and peaked at 313.36 MiB. This was
before replay's allocation change; it is not a controlled speed comparison.

The user then exited the installed app and authorized a background-only restart
on the latest build. Without ever opening the dashboard, the 120-second sample
averaged 235.33 MiB, peaked at 388.17 MiB across the tree, and ended at 305.12 MiB.
The main app ended at 110.33 MiB (114.80 MiB peak); the active calibration worker
peaked at 191.43 MiB. Startup/catch-up CPU was 0.287% for the parent and 0.492% for
the worker, normalized to 16 logical processors. Helpers and active workloads
vary; this operational comparison identifies a dashboard-initialization increment
but does not prove how much unloading can reclaim. Local evidence:
`reader-memory-hidden-120s.json` and `background-only-120s.json`.

The current source passed all 686 Core tests (1m38s), followed serially by the
Windows build/install with zero warnings/errors (72.50s). The offline reporter
also built successfully. The user chose dashboard unloading after 30 seconds
hidden, preserving lightweight state; implementation and runtime acceptance of
that policy are the next PERF-004 extension. Numerical memory budgets remain open.

## Hidden dashboard unloading

The approved policy now detaches immediately and, after 30 seconds continuously
hidden/minimized, captures page-owned presentation inputs and drops the frame,
its cached pages and generated history. Short hides reuse the same view. The
native window stays alive for the tray and restore path. The later extension below
also releases the navigation shell. No
forced GC or process memory cap was added.

Snapshots are process-local typed records containing scalar values, selection
identities, static input indexes and navigation entries; they retain no controls,
pages, callbacks or history row arrays. Account/source/database/table selections
use identities rather than row indexes. Nested usage scopes and raw/source explorer
inputs are included. New explicit thread/planner/model-lab navigation takes
precedence where its parameter differs from the suspended view. Back/Forward
entries are copied without retaining transition objects.

Unsaved settings preserve the baseline used for optimistic-save conflict detection.
Settings and CLI inputs also survive brief hides instead of being overwritten by
Loaded. Fresh history uses the existing cancellation/generation-qualified readers.
Derived history, evaluation results, CLI stdout/stderr and raw comparison baselines
are released; restoring them does not silently present old results as current.

All 686 Core tests passed, none skipped (1m19s). The serial Windows solution build
passed with zero warnings/errors; a final form-lifecycle correction was followed
by another successful Windows build/install (52.52s), restarting the daily app as
PID 7984. Core tests do not exercise WinUI state restoration. Manual planner/form,
Back/Forward, restore timing and combined-memory acceptance remain pending under
PERF-004/PERF-001. Agent Computer Use remains stopped; the user performs UI actions.

### Restore ordering and accessibility identity

The first installed unload pass preserved planner text/numeric inputs but reset its
selected tab to Overview. Restoration in Frame.Navigated ran before the page's
navigation-mode initialization. Saved inputs now apply once at Loading, after
navigation parameter handling and before Loaded starts scoped queries. Nested
usage inputs are applied while detached; nested explorer pages use the same
Loading boundary. Switching between history and model-lab modes does not apply a
saved index to a different tab collection.

An installed pass of that correction kept the Planning tab, 3.25-hour duration and
test model text after a full unload. A separate unsaved Settings interval of 67
seconds survived unloading; Back returned to Planning with its inputs intact.
The test values were restored without saving runtime settings. One traced corrected
restore reached its first frame in 103.305 ms. All 45 captured phases completed,
with zero lost events/source errors and a dashboard-release event. This is one
observed transition, not a population percentile or input-to-photon latency.
Evidence: `dashboard-restore-order*` under `.codex/temp/stage1/`.

Broader UI inspection reproduced an unresponsive chat browser with unbounded
native allocation. Symbolized live stacks locate the UI thread in WinUI item
container lookup and UIAutomationCore sibling traversal/BulkFetch, rather than a
forecast calculation. A screenshot-only attempt also encountered the same native
accessibility path; it did not isolate the initiating client. Two diagnosed test
processes required targeted recovery after the normal shutdown timed out. The
normal deployment helper remains graceful-only; its behavior was not changed.

This matches the reported WinUI [duplicate-item automation defect](https://github.com/microsoft/microsoft-ui-xaml/issues/11226).
The chat diagnostic lists formerly bound display strings directly, allowing equal
rows. They now wrap each row in a reference-identity TextListItem and bind its Text,
preserving duplicate labels and order. Do not convert this wrapper to a value-equal
record or deduplicate user-visible evidence. An automated regression covers this
identity contract. Full Core suite: 687 passed, none skipped (1m12s). Windows build
and installed accessibility confirmation are pending at this checkpoint. Stack-only
local evidence: `usage-ui-stall*` and `chat-screenshot-native-stack.txt`; no memory
dump or content-bearing heap artifact was collected.

### Confirmed hidden sample after page unloading

On the earlier installed unload build (before the restore-order and accessibility
corrections), a UI-confirmed 120-second minimized interval ended at 420.76 MiB
combined private commit. After the 30-second delay, its time-weighted mean was
415.10 MiB and sampled peak 595.06 MiB. The main process fell from 232.49 MiB at the
first sample to 207.41 MiB at the last; worker peak was 226.68 MiB. Parent CPU was
0.0733% and worker CPU 0.5354%, normalized to 16 logical processors. The sample was
unprofiled and includes ongoing collection/calibration plus observed helpers.

At the aligned 595.06 MiB peak the app used 206.06 MiB, calibration 226.68 MiB,
the native Codex quota helper 126.93 MiB, node 21.36 MiB and the three remaining
launcher/console helpers 14.04 MiB. Thus releasing pages does not resolve the
combined peak; helper overlap and residual UI/runtime memory remain material.
The separate later accessibility runaway is excluded from this hidden sample.
Evidence: `dashboard-unload-hidden-120s.json`. The proposed sustained-versus-peak
400 MiB budget has been put to the user; neither choice is yet accepted.

The final Windows build/install passed with zero warnings/errors (55.24s), starting
PID 38692. Repeated accessibility walks over the 394-thread chat browser and usage
view returned normally; the process remained responsive without the earlier native
allocation runaway. On this build, a full hidden unload preserved the nested usage
view, seven-day history and text filter. A cached forecast History page switched
into Model lab without applying an invalid saved tab index. A further unload
preserved the data explorer's child filter. Temporary inputs were cleared; the
usage range was returned to its original thirty days. No settings were saved.

The later restore capture completed all 34 phases, with no lost events/source
errors. Its two first-frame observations were **53.601 ms and 76.525 ms**. These
cover installed transitions; they are not p95 estimates. The earlier corrected
planner/settings/Back checks plus these nested-view checks close PERF-004's
functional acceptance. Evidence: `uia-fixed-restore*`. PERF-001 remains open.

A fresh unprofiled, UI-confirmed hidden sample after visiting multiple pages on the
final build averaged 412.63 MiB after the unload delay and sampled a 670.77 MiB
combined peak. Worker activity was observed only for the final 61.86 seconds, so
this mean is not a sustained-worker budget result. At the aligned peak: parent
336.75 MiB, worker 178.87 MiB, native quota helper 119.16 MiB, node 21.34 MiB and
other helpers 14.66 MiB. Parent CPU was 0.0521%, worker 0.4894% of 16 processors.
This broader page-visit scenario is not directly comparable to the earlier planner
sample or a fresh tray-only process. Evidence: `uia-fixed-hidden-120s.json`.

An isolated, explicitly induced collection followed the normal-operation samples.
`dotnet-gcdump` 10.0.750501 reported a 6,102,090-byte managed graph (69,429 objects),
while private commit fell from 366.71 to 309.97 MiB after five seconds. Its runtime
warned that .NET 10 uses lossy event buffering; the graph is not an exact inventory
of every native/managed allocation. Live SOS inspection still enumerated two
Overview and two Forecast page objects, with no managed root path found for them;
that alone does not establish a leak or identify native ownership. The remaining
private commit cannot be attributed to retained forecasting arrays from this
managed graph. Framework/native caches, allocations outside the CLR heap, observer
effects and lifecycle differences remain unresolved attribution candidates.

This diagnostic changes GC state and **does not count as normal-operation RAM
acceptance**. No forced collection was added to production. Metadata-only graph,
aggregate before/after JSON and stack/root reports remain local under
`.codex/temp/stage1/hidden-page-*`; no raw process-memory dump was taken.

### Navigation shell and allocator

Hidden eviction now releases the NavigationView shell as well as its frame. The
existing native window is retained. Pane-open and Advanced expansion values join
the lightweight state; handlers are detached before release and restored with the
new shell. An installed planner duration of 4.5 hours and its selected view survived
a full unload, and Back returned to Overview. The shell trace observed a 141.939 ms
first frame; all 15 captured phases completed with no lost events or source errors.
These are individual observations, not percentile estimates. The shell slice passed
687 Core tests and a Windows build with zero warnings/errors before installation.

The manifest opts into the supported Windows
[segment heap](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests#heaptype).
Live native heap signatures confirmed that this opt-in took effect. No forced GC,
working-set trim or new process boundary was added. Native heap free-space inspection
did not support adding speculative heap trimming; reserved virtual address ranges
are not RAM measurements.

Both unprofiled 120-second samples used Planning with a 4.5-hour input, then minimized:

| Installed variant | Mean after 30 s | Mean while worker active | Sampled tree peak | Last parent |
|---|---:|---:|---:|---:|
| Shell unloading | 472.39 MiB | 490.98 MiB | 644.59 MiB | 256.02 MiB |
| Shell unloading + segment heap | 459.29 MiB | 459.29 MiB | 537.19 MiB | 202.55 MiB |

The worker was active for 82.04 of the first sample's 89.09 measured seconds and
all 89.98 seconds of the second. Natural evidence and worker/helper phases differed;
this is supportive operational evidence, not a controlled allocator effect size.
The allocator is retained for its observed savings within the supported platform.
The 400 MiB sustained combined target remains provisional and was not met here.
Resource samples now include executable and build-manifest SHA-256 hashes because
a dirty source-identity string alone cannot distinguish successive local builds.
Evidence: `shell-unload-hidden-120s.json`, `segment-heap-hidden-120s.json`,
`shell-unload-restore-report.json` and `segment-heap-native-stats.txt` locally in
`.codex/temp/stage1/`. Interval-weighted means include the partial interval crossing
30 seconds; older calculations omitting that interval differ slightly.

### Overview evidence projection

Overview history previously requested the complete predictive dataset but consumed
only its ledger and quota. The existing reader interface now offers that projection
with a complete-read fallback for other implementations. The SQLite implementation
keeps one read transaction, the same ownership/provenance rules and selected ledger
bounds (`from` inclusive, `to` exclusive). It skips metadata/context and predictive
token-list retention; work-filtered views also skip unused quota rows. Forecast and
calibration reads, algorithms, collection times and retained evidence are unchanged.
Oversized selected token reads fail explicitly rather than truncating.

An owned fixture with 20,000 token events and 20,000 metadata rows produced identical
ledger/quota SHA-256 results in both benchmark orders. Complete reads allocated
34.67/34.69 MB and took 425.01/413.53 ms; projected reads allocated 15.69/15.70 MB
and took 195.20/144.91 ms. These are warmed isolated reader measurements, not app
private-commit savings. Focused tests cover endpoints, unknown models, collection
time, source identity, conflicting ownership, omitted tables and the 500,000-row
refusal. Temporary benchmark outputs are `ledger-projection-bench*.log`.

The combined slice passed all 691 Core tests, none skipped (1m35s), and the serial
Windows build/install with zero warnings/errors (57.66s). The daily app was restarted
as PID 24800. Its executable SHA-256 is
`A37D8953067ACE796A70157813E14D86AFF3E2F3BC3CA80AC9D0B0C2C26526B4`.
Installed Overview and Planning loaded normally; the fixture establishes exact
projection parity, while the installed check establishes the production read path
remains usable. It does not compare moving live totals across different times.

A subsequent UI-confirmed five-minute minimized sample on that executable, without
profiling or forced GC, averaged **351.79 MiB** after the 30-second unload delay.
Calibration was present for only 117.82 of the 269.38 measured seconds; the mean
while it was present was **496.88 MiB**, so the overall mean cannot close the
provisional 400 MiB sustained-worker gate. The aligned tree peak was **556.90 MiB**:
parent 276.96, worker 215.47, quota helper 29.46, node 21.45 and launcher/console
helpers 13.56 MiB. The final parent/tree was 226.54 MiB after the worker exited.
Parent CPU was 0.100% of the 16 logical processors; worker CPU was 0.520% over its
146.45-second observed lifetime. PhysicalDisk capture succeeded but describes the
whole device, not attributed app I/O. These results include the app's observed
helper tree and must not be reported as parent-only acceptance.

The post-sample restore preserved Planning, its 4.5-hour input, the open pane and
collapsed Advanced group. The input was reset to its original one hour, Overview
was selected and no settings were saved. A separate trace observed a **120.256 ms**
first frame, with zero lost events/source errors. It captured 23 starts/22 completions;
one operation extends outside the capture, not an established operation failure.
Selected history completed in 658.307 ms, including its 498.050 ms evidence read.
Evidence: `ledger-projection-hidden-300s.json` and `ledger-projection-restore-report.json`.
