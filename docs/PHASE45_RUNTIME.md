# Phase 4.5 runtime and ingestion performance

Phase 4.5 exists to make the Observatory collection path cheap enough that TajsTokens can remain a background utility rather than becoming a workload worth observing itself.

## Baseline evidence

The representative Windows profile taken after the Phase 3.5/Phase 4 UI-thread hardening showed a materially healthier interactive application:

- the UI remained responsive while historical collection continued;
- process CPU was roughly 2.5% during the observed steady collection period;
- disk throughput could remain active at up to roughly 4 MB/s for extended periods;
- low-level rollout file reading was a small part of sampled wall time;
- semantic persistence dominated the remaining ingestion path.

The sampled call tree attributed roughly:

- 202 s to `ReadNewJsonLinesAsync`;
- 201 s to `CodexSessionIngestionService.IngestAsync`;
- 193 s to parsed semantic persistence;
- about 43-44 s each to session, agent and activity upserts;
- about 29 s to quota upserts;
- about 16 s to context upserts;
- about 15 s to cumulative-token persistence;
- only about 6 s to the low-level `ReadAtOffset` path.

Those numbers are sampled aggregate time, not a stopwatch benchmark. Their useful conclusion is architectural: optimizing JSON decoding or buffer copies first would attack the wrong bottleneck.

A parallel read-only inspection of Codex's local state database found roughly 318-319 indexed thread rows. Normal TajsTokens refresh still recursively entered every historical rollout even when almost all of them were already at EOF. That made discovery work and semantic write amplification the Phase 4.5 targets.

## Phase 4.5 architecture

Normal collection now has two modes.

### Recognized Codex state catalog

```text
Codex state SQLite (read-only)
    -> validate private-schema fingerprint
    -> one process-start full metadata reconciliation
    -> indexed updated_at_ms query + bounded overlap thereafter
    -> compare privacy-safe per-thread fingerprints
    -> reconcile thread_spawn_edges independently
    -> open only changed/reconciliation-needed rollout paths
    -> seek TajsTokens-owned byte checkpoint
    -> parse complete new JSONL records
    -> one bounded ingestion writer lane
    -> parser/checkpoint state after durable persistence
```

The state database is an acceleration/index source only. Rollout JSONL remains authoritative for event-level token classes, exact timestamps, counter epochs, inherited-prefix exclusion, context/compaction, embedded quota observations and exact complete-record boundaries.

A state fingerprint is not accepted merely because a rollout read returned successfully. TajsTokens reconciles the state-reported cumulative token evidence with its persisted counter where available, and otherwise requires a fully consumed rollout whose file timestamp has caught up to the state timestamp. This prevents a state-ahead-of-rollout race from permanently suppressing a trailing record.

### Fallback

Missing, locked, malformed, empty-rotated or unrecognized Codex state databases fail open to the existing recursive rollout discovery path. TajsTokens does not require a private Codex schema to remain stable.

## Ingestion writer

Production ingestion uses one high-volume batch-writer abstraction per Observatory runtime.

At each bounded 128-record durability boundary it serializes:

- rollout file metadata;
- rollout record metadata;
- session projections;
- agents and relationships;
- activity/timeline rows;
- quota snapshots;
- context/compaction observations;

through one gate, connection, transaction and prepared command set.

Cumulative token observations remain ordered and reuse the established counter-epoch/reset implementation while the same ingestion writer still owns the batch lane. If any counter write fails, the source byte checkpoint is not advanced; replay repeats idempotent projection/storage writes before retrying the ordered counter mutation.

## Privacy

The state-index acceleration layer persists only content-free change evidence. It does not persist Codex titles, previews, first messages, raw provider payloads, or absolute rollout paths. Rollout locators are reduced to hashes before entering TajsTokens-owned state.

## Representative Windows re-profile procedure

Use the same machine and broadly the same Codex history for before/after comparisons. Record the TajsTokens commit SHA and Codex version with every result.

### 1. Existing-history / process-start reconciliation

1. Start TajsTokens with an already-populated telemetry database and a large existing Codex corpus.
2. Record time from Observatory refresh start to completion.
3. Confirm one compact state-catalog reconciliation occurs instead of a recursive JSONL census.
4. Record CPU, disk read/write rate, peak working set and sampled hot paths.

This pass is intentionally somewhat more conservative than subsequent refreshes because it compares the complete compact state catalog once per process.

### 2. Warm idle refresh

With no Codex thread changed since the previous successful refresh:

1. trigger a normal/manual refresh;
2. verify zero rollout JSONL files enter ingestion;
3. verify no historical rollout parsing occurs;
4. record wall time and disk activity.

Target: effectively invisible collection, consisting primarily of the indexed Codex-state query and small TajsTokens fingerprint reads.

### 3. One active root

1. produce a normal Codex turn in one root thread;
2. refresh TajsTokens;
3. verify only that changed/reconciliation-needed rollout is opened;
4. record records processed, wall time, CPU/disk activity and SQLite hot paths.

### 4. Root plus subagents

Repeat with a root that spawns/uses subagents. Verify changed rollout selection, independent `thread_spawn_edges` reconciliation and ordered native-accounting behavior.

### 5. Archive/path movement

Move/archive a known Codex thread through normal provider behavior. Restart TajsTokens and verify the process-start full catalog reconciliation observes the path/archive metadata change without requiring a token timestamp change.

### 6. State fallback

Temporarily point a test/sandbox Codex home at a missing or deliberately incompatible state catalog while retaining sanitized rollouts. Verify filesystem discovery remains correct and no existing accounting is lost.

## Metrics to record

For each scenario capture:

- commit SHA / Codex version;
- thread count and changed-thread count;
- rollout files opened;
- JSONL records scanned/normalized;
- refresh wall time;
- records per second for changed imports;
- CPU utilization;
- disk read/write throughput and sustained activity duration;
- managed/working-set memory;
- dominant sampled methods;
- telemetry database size before/after when relevant;
- correctness notes: replay, counter reset, topology and checkpoint behavior.

Do not turn machine-sensitive milliseconds into normal CI assertions. CI should protect deterministic semantics and structural performance properties, while representative Windows profiling provides the empirical resource numbers.

## Completion gate

Phase 4.5 performance work is ready to close when:

- Windows CI and release-smoke remain green;
- state-index and batch-writer correctness/privacy regressions are green;
- warm idle on a representative large corpus opens no unchanged rollout JSONL;
- process-start, one-root and root-plus-subagent measurements are recorded;
- no new UI freeze or sustained background I/O regression is visible;
- remaining expensive paths, if any, have measured evidence rather than speculative optimization targets.
