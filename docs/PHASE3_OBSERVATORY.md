# Phase 3 Codex Observatory

Phase 3 adds direct local Codex rollout observability while keeping Tokscale as TajsTokens' default accounting provider.

## Data flow

```text
~/.codex/sessions + archived rollouts
        |
        v
FileSystemCodexSessionEventProvider
  complete JSONL records only
        |
        v
CodexSessionIngestionService
  stable file identity + exact byte checkpoint
        |
        v
CodexRolloutParser
  owning session_meta / turn context / token+quota / lifecycle / compaction
        |
        v
SqliteCodexObservatoryStore
  sessions + agents + relationships + timeline
  native shadow token deltas + counter epochs
  context observations + rollout record sizes
  rollout quota snapshots
        |
        +--> Observatory page
        +--> shared quota history / later analytics
```

The normal process-lifetime `TelemetryCoordinator` runs the observatory refresh alongside Tokscale and Codex app-server I/O after the base SQLite schema has initialized.

## Ownership and inherited child history

Codex rollout filenames normally contain the owning session UUID. The parser waits for the `session_meta.id` matching that UUID before normalizing semantic telemetry. This is important for subagent rollouts that begin with a copied parent prefix. `subagent_history_start_ordinal` is useful when present but is deliberately not required for this ownership boundary.

If a rollout filename does not contain a UUID, the parser falls back conservatively to the first session metadata record. Such sources should be treated as lower-confidence coverage until a verified source contract provides a stronger identity.

## Native shadow accounting

`token_count.info.total_token_usage` is treated as a cumulative counter stream, not a session lifetime scalar. For each source/session pair TajsTokens persists the last raw counters and an epoch number. A decrease in cumulative total/component counters starts a new epoch. Repeated snapshots produce zero delta.

Persisted shadow buckets are disjoint:

- uncached input = cumulative input delta minus cache-read/cache-write deltas;
- cache read;
- cache write;
- non-reasoning output = output delta minus reasoning-output delta;
- reasoning output;
- provider-reported total delta.

Every native event has a deterministic source-event id derived from source-file identity plus byte offsets. Replaying a previously committed record therefore cannot add its token delta twice.

This data is intentionally labelled **native shadow**. Phase 3 does not replace Tokscale or claim parity. Full reconciliation and cutover remain Phase 6.

## Quota source fusion

When a rollout `token_count` event contains `rate_limits`, each recognizable quota window is persisted into the same `quota_snapshots` history used by app-server observations, with `codex-rollout:*` source provenance. Common 300-minute and 10,080-minute windows map to five-hour and weekly kinds; other durations remain `Unknown` rather than being relabelled.

The provider's `resets_at` value is retained as authoritative. Rollout observations do not fabricate missing app-server lanes and do not make the top-level dashboard call itself live when its active provider read failed.

## Context and compaction

Token-count records contribute content-free `last_token_usage.input_tokens` plus `model_context_window` observations when available. Compaction records are stored as markers. This is sufficient to reconstruct context utilization and the pre/post compaction sawtooth later without storing prompts or summaries.

## Rollout storage diagnostics

Every complete record can contribute only:

- deterministic source-record id;
- source file path used locally for checkpoint/storage association;
- owning session id when known;
- normalized event class;
- byte length;
- timestamp.

The original JSON payload is discarded after parsing. This lets the UI identify giant records/files and event-class-heavy sessions without duplicating tool output into `telemetry.db`.

## Additive observatory schema

The base telemetry repository remains owner of `PRAGMA user_version`. Phase 3 adds an independently versioned additive `codex-observatory` component inside the same database:

- `observatory_schema`
- `codex_native_token_events`
- `codex_counter_state`
- `context_observations`
- `rollout_records`
- `rollout_files`

The component writes existing normalized `sessions`, `agents`, `agent_relationships`, `usage_events`, and `quota_snapshots` tables rather than inventing duplicate universes for those concepts.

## Privacy boundary

Normal Phase 3 ingestion must not persist prompt/message text, reasoning text, source code, command arguments, shell/tool output, credentials, or raw JSONL records. Timeline summaries are intentionally generic (`Tool call`, `Task completed`, `Context compaction`, etc.).

The regression fixtures under `tests/TajsTokens.Core.Tests/Fixtures/CodexRollouts` are hand-authored synthetic structures. Real personal rollouts are not repository assets.
