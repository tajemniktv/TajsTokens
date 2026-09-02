# Codex upstream SQLite schema reference

> **Status: pinned source-reference note, not an architectural specification.**
>
> `PROJECT.md` remains the sole authority for TajsTokens product/data architecture. This document records what the public `openai/codex` source currently says about its SQLite runtime so we do not keep rediscovering the same schema by hand, a cherished software tradition that deserves less encouragement.
>
> Installed/local Codex data remains authoritative for what a particular build actually emits. Upstream source is corroborating evidence for intended structure and semantics. A mismatch must be recorded as a mismatch, not edited away.

## Evidence pin

This note is based on public upstream source at:

- repository: `openai/codex`
- commit: `eb078b4f44b0c8099d376440d63ce5bbb11675bd`
- inspected: 2026-09-02
- state migration head: `0052_projects_recency.sql`
- thread-history migration head: `0006_thread_turn_ends.sql`

This PR does **not** claim that the user's installed Codex/Desktop build is byte-for-byte aligned with that commit. Local parity should be checked with the read-only source explorer or a direct read-only SQLite inspection when it matters.

## Scope

This note covers the SQLite databases managed by `codex-rs/state` and the source-backed relationships that are explicit enough to describe without product inference.

It does not define:

- quota or rate-limit semantics;
- the complete rollout JSONL schema;
- app-server RPC contracts;
- authentication or credential storage;
- desktop-only/private subsystems absent from the public repository;
- TajsTokens normalized observations or durable evidence tables.

The fact that something is absent from these SQLite databases is not evidence that Codex lacks it elsewhere.

## Current upstream runtime database set

`codex-rs/state/src/sqlite.rs` currently declares six runtime SQLite databases:

| File | Upstream role | Migration set | Current head |
| --- | --- | --- | --- |
| `state_5.sqlite` | primary state database | `state/migrations` | `0052` |
| `logs_2.sqlite` | logs database | `state/logs_migrations` | `0002` |
| `goals_1.sqlite` | goals database | `state/goals_migrations` | `0002` |
| `memories_1.sqlite` | memories database | `state/memory_migrations` | `0001` |
| `queue_1.sqlite` | durable user-message queue | `state/queue_migrations` | `0002` |
| `thread_history_1.sqlite` | paginated thread-history projection | `state/thread_history_migrations` | `0006` |

The shared writable connection configuration uses WAL mode, `NORMAL` synchronous mode, incremental auto-vacuum, a five-second busy timeout, and a pool of up to five connections. The same source also exposes an explicit read-only pool that refuses to create a missing database and uses one connection.

Each SQLx-managed database also contains framework migration bookkeeping such as `_sqlx_migrations`; those rows are migration metadata, not Codex domain evidence.

---

# `state_5.sqlite`

## Current upstream tables

After applying the current migration chain, the Codex-owned application tables retained in `state_5.sqlite` are:

- `threads`
- `thread_dynamic_tools`
- `backfill_state`
- `thread_spawn_edges`
- `remote_control_enrollments`
- `external_agent_config_imports`
- `thread_sections`
- `rollout_migration_state`
- `rollout_migration_skipped_rollouts`
- `projects`
- `project_roots`
- `project_idempotency_keys`
- `thread_artifacts`

Historical `CREATE TABLE` statements are **not** enough to determine the current schema. Several old state tables were deliberately dropped or moved into separate databases later in the chain; see [Schema evolution that matters](#schema-evolution-that-matters).

## `threads`

`threads` is the central persisted thread-metadata table. The final shape is reconstructed from `0001_threads.sql` plus later `ALTER TABLE` migrations and corroborated by the current `ThreadMetadata` runtime model.

### Identity and physical storage

| Column | Upstream-backed meaning |
| --- | --- |
| `id` | thread identifier; primary key |
| `rollout_path` | absolute rollout path on disk |

The runtime source treats the logical thread ID and physical rollout path as distinct. There is an explicit operation that can replace the rollout path while keeping metadata attached to the stable thread ID.

### Time and recency

| Column | Notes |
| --- | --- |
| `created_at` | legacy timestamp column retained for compatibility |
| `updated_at` | legacy timestamp column retained for compatibility |
| `created_at_ms` | millisecond creation timestamp added by migration `0025` |
| `updated_at_ms` | millisecond update timestamp added by migration `0025` |
| `recency_at` | product-recency timestamp introduced by `0039` |
| `recency_at_ms` | millisecond product-recency timestamp introduced by `0039` |
| `archived_at` | archive timestamp when present |
| `section_entered_at_ms` | time the thread most recently entered its current section |

Migration `0025` backfills the millisecond columns and installs compatibility triggers so older writers that only update the legacy timestamp columns still populate/update `*_ms`. Current runtime reads use the millisecond columns as the canonical `created_at` and `updated_at` values.

`recency_at` is deliberately separate from `updated_at`; the runtime model calls it the **product recency timestamp**. Migration `0039` initially seeds it from `updated_at` and adds a compatibility trigger for older writers.

### Source, model, and execution metadata

| Column | Upstream-backed meaning |
| --- | --- |
| `source` | stringified session source |
| `thread_source` | optional analytics source classification |
| `model_provider` | model-provider identifier |
| `model` | latest observed model for the thread |
| `reasoning_effort` | latest observed reasoning effort |
| `cwd` | working directory |
| `cli_version` | CLI version associated with the thread |
| `sandbox_policy` | stringified sandbox policy |
| `approval_mode` | stringified approval mode |
| `history_mode` | persisted thread-history contract, default `legacy` |
| `memory_mode` | memory-mode string, default `enabled` |

The runtime model explicitly distinguishes `history_mode` from ordinary display metadata. Current source includes a permanent promotion path that changes a thread to `paginated` history and preserves a suitable display name during that transition.

### Agent/sub-agent metadata

| Column | Upstream-backed meaning |
| --- | --- |
| `agent_nickname` | optional random nickname for an AgentControl-spawned sub-agent |
| `agent_role` | optional role assigned to a spawned sub-agent |
| `agent_path` | optional canonical agent path |

Spawn relationships themselves are not encoded by guessing from these strings; Codex has a separate `thread_spawn_edges` table.

### Display/discovery state

| Column | Upstream-backed meaning |
| --- | --- |
| `title` | best-effort thread title |
| `name` | explicit user-facing thread name when set |
| `preview` | best available preview for discovery/list display |
| `first_user_message` | first observed user message when present |
| `has_user_event` | legacy/inventory flag retained in the physical schema |
| `is_pinned` | pin flag introduced before the later section model |
| `thread_section_id` | optional FK to `thread_sections(id)` |
| `section_position` | sparse stable ordering position inside a section |

The migrations show that `preview` was initially backfilled from `first_user_message` and, where that was empty, from a goal objective that existed at that point in schema history. That backfill history should not be mistaken for a guarantee about every future preview value.

### Archive, project, VCS, and usage state

| Column | Upstream-backed meaning |
| --- | --- |
| `archived` | integer archive flag |
| `project_id` | optional FK to `projects(id)` |
| `git_sha` | git commit SHA when known |
| `git_branch` | git branch when known |
| `git_origin_url` | sanitized git origin URL when known |
| `tokens_used` | **last observed token usage**, not an event ledger |

The `tokens_used` label deserves a warning sticker. Current upstream `ThreadMetadata` documents it as **“The last observed token usage.”** In `state/src/extract.rs`, token-count events update the field from `token_count.info.total_token_usage.total_tokens` when that information is present.

Therefore, for TajsTokens purposes:

> `threads.tokens_used` is source-backed thread metadata containing the latest observed total from Codex's extraction path. It must not be treated merely from its name as an append-only token accounting event, a per-turn delta, or proof of complete lifetime accounting.

That distinction is exactly the kind of thing the architecture reset is meant to preserve.

## `thread_dynamic_tools`

Current columns:

- `thread_id`
- `position`
- `name`
- `description`
- `input_schema`
- `defer_loading` with default `0`
- `namespace`

Primary key: `(thread_id, position)`.

`thread_id` references `threads(id)` with `ON DELETE CASCADE`. This table is ordered tool-definition state for a thread. `defer_loading` and `namespace` were added after the original table, so a schema snapshot from an older Codex build may legitimately lack them.

## `thread_spawn_edges`

Columns:

- `parent_thread_id`
- `child_thread_id`
- `status`

`child_thread_id` is the primary key. Current runtime code calls this a **directional thread-spawn edge**, exposes statuses `open` and `closed`, and joins `child_thread_id` to `threads.id` when resolving agent paths. Direct children and transitive descendants are queried explicitly from this graph.

This is considerably stronger evidence than inferring parentage from filenames, titles, or similar-looking IDs.

## `thread_sections`

Columns:

- `id` primary key
- `name`
- `appearance` nullable JSON text

The migration seeds a `Pinned` section with a fixed UUID and later adds `appearance`. Threads may reference a section through `threads.thread_section_id`; section ordering is represented separately by `threads.section_position` and `section_entered_at_ms`.

## Projects

### `projects`

- `id` primary key
- `name`
- `metadata` JSON text, default `{}`
- `position`
- `created_at_ms`
- `updated_at_ms`

### `project_roots`

- `project_id` FK to `projects(id)` with `ON DELETE CASCADE`
- `position`
- `path`

Primary key: `(project_id, position)`.

### `project_idempotency_keys`

- `key` primary key
- `project_id`
- `created_at_ms`

Current project runtime code hydrates roots ordered by `position`, assigns threads by writing `threads.project_id`, and computes project recency from the maximum `threads.recency_at_ms` of non-archived member threads. The current schema also has a partial index for active project-thread recency.

## `thread_artifacts`

Columns:

- `id` primary key
- `thread_id` FK to `threads(id)` with `ON DELETE CASCADE`
- `artifact_type`
- `identity_key`
- `payload`
- `created_at`

Unique constraint: `(thread_id, artifact_type, identity_key)`.

`payload` is deliberately left as source-native text by the schema. Its existence does not authorize TajsTokens to durably duplicate it without a separate privacy/retention decision.

## Runtime/support tables

### `backfill_state`

Singleton table (`id = 1`) tracking backfill status, watermark, last success time, and update time. Upstream rollout code uses it to coordinate metadata backfill from rollout files into state.

### `rollout_migration_state`

Tracks per-migration progress through rollout migration with a last checked thread creation timestamp/ID and update time.

### `rollout_migration_skipped_rollouts`

Records rollout paths skipped by a migration together with size, modification timestamp, skip reason, and skip time. Primary key: `(migration_id, rollout_path)`.

### `remote_control_enrollments`

Current columns:

- `websocket_url`
- `account_id`
- `app_server_client_name`
- `server_id`
- `environment_id`
- `server_name`
- `updated_at`
- `remote_control_enabled`

Primary key: `(websocket_url, account_id, app_server_client_name)`.

This document records the schema only. It does not infer account identity or remote-control product semantics beyond what the source names and runtime code establish.

### `external_agent_config_imports`

Current columns:

- `import_id` primary key
- `completed_at_ms`
- `successes`
- `failures`
- `provider_id`

`successes` and `failures` are stored as text. Their inner format should be treated as opaque until the producing/consuming source is inspected.

---

# `logs_2.sqlite`

Current application table: `logs`.

Final columns after migration `0002_logs_feedback_log_body.sql`:

- `id` integer autoincrement primary key
- `ts`
- `ts_nanos`
- `level`
- `target`
- `feedback_log_body`
- `module_path`
- `file`
- `line`
- `thread_id`
- `process_uuid`
- `estimated_bytes`

The first logs migration used a `message` column. Migration `0002` rebuilds the table and copies `message` into `feedback_log_body`, then drops the old table. Therefore `message` is historical migration shape, not the current upstream column name.

Indexes support global time order, thread lookup/time order, and threadless process-specific time order.

---

# `goals_1.sqlite`

## `thread_goals`

Current columns:

- `thread_id` primary key
- `goal_id`
- `objective`
- `status`
- `token_budget`
- `tokens_used`
- `time_used_seconds`
- `created_at_ms`
- `updated_at_ms`

The schema constrains `status` to:

- `active`
- `paused`
- `blocked`
- `usage_limited`
- `budget_limited`
- `complete`

These goal-specific `tokens_used` values belong to the goal subsystem and should not be silently conflated with `state_5.threads.tokens_used` merely because human civilization reused the same column name.

## `thread_goal_continuation_deferrals`

Single column:

- `thread_id` primary key and FK to `thread_goals(thread_id)` with `ON DELETE CASCADE`

Presence represents persisted continuation-deferral state. This table's existence is source evidence; any higher-level product interpretation still belongs in a contract/read model.

---

# `memories_1.sqlite`

## `stage1_outputs`

Columns:

- `thread_id` primary key
- `source_updated_at`
- `raw_memory`
- `rollout_summary`
- `rollout_slug`
- `generated_at`
- `usage_count`
- `last_usage`
- `selected_for_phase2`
- `selected_for_phase2_source_updated_at`

This table contains content-bearing values (`raw_memory`, `rollout_summary`). TajsTokens may inspect locally where useful, but durable duplication/export is a separate privacy decision.

## `jobs`

Columns:

- `kind`
- `job_key`
- `status`
- `worker_id`
- `ownership_token`
- `started_at`
- `finished_at`
- `lease_until`
- `retry_at`
- `retry_remaining`
- `last_error`
- `input_watermark`
- `last_success_watermark`

Primary key: `(kind, job_key)`.

The associated index is shaped around `kind`, `status`, retry time, and lease time, supporting a leased/retryable background-job model.

---

# `queue_1.sqlite`

## `queued_items`

Columns:

- `id` primary key
- `thread_id`
- `payload_json`
- `queue_order`
- `created_at_ms`
- `updated_at_ms`

Unique index: `(thread_id, queue_order)`.

`payload_json` is source-native content. Its inner schema is not established by the SQLite DDL alone.

## `queued_thread_revisions`

Columns:

- `revision` integer autoincrement primary key
- `thread_id` unique

Insert/update/delete triggers on `queued_items` advance the affected thread's revision. This provides a persisted change token for queue state without requiring consumers to infer change from row counts or timestamps.

---

# `thread_history_1.sqlite`

This database is especially important because upstream source makes the projection relationship explicit rather than leaving us to perform divination on similarly named columns.

The current migration set contains four application tables:

- `thread_turns`
- `thread_items`
- `thread_realtime_items`
- `thread_history_projection_state`

## `thread_turns`

Current columns:

- `thread_id`
- `turn_id`
- `rollout_ordinal`
- `rollout_byte_offset`
- `rollout_end_ordinal`
- `rollout_end_byte_offset`
- `status`
- `error_json`
- `started_at`
- `completed_at`
- `duration_ms`
- `first_user_item_id`
- `final_agent_item_id`

Primary key: `(thread_id, turn_id)`.

The projection writer deliberately keeps the `rollout_ordinal` that first created a turn. When an in-progress turn later reaches a terminal state, it updates terminal position/status fields such as `rollout_end_ordinal` and `rollout_end_byte_offset` rather than rewriting the original creation position.

## `thread_items`

Current columns:

- `thread_id`
- `turn_id`
- `item_id`
- `rollout_ordinal`
- `updated_at_ordinal`
- `created_at_ms`
- `item_type`
- `item_json`

Primary key: `(thread_id, turn_id, item_id)`.

`item_type` was added after the original table and backfilled from `json_extract(item_json, '$.type')`. `updated_at_ordinal` was later added to separate initial creation position from the latest projection update position.

Current writer comments state that completed items are expected to be immutable and emitted once, while still defensively tolerating a duplicate by preserving original creation position/time and updating the stored snapshot plus `updated_at_ordinal`.

`item_json` is content-bearing source data. The SQLite schema proves storage shape, not permission to mirror its contents into TajsTokens durable evidence.

## `thread_realtime_items`

Columns:

- `thread_id`
- `item_id`
- `rollout_ordinal`
- `created_at_ms`
- `item_type`
- `item_json`

Primary key: `(thread_id, item_id)`.

The table is a separate realtime timeline lane. A partial index targets boundary item types `realtime_session_started` and `realtime_session_closed`. Deleting a thread's projection-state row triggers cleanup of its realtime rows.

This is evidence against interpreting realtime rows as ordinary `thread_items` merely because both contain IDs, ordinals, and JSON.

## `thread_history_projection_state`

Columns:

- `thread_id` primary key
- `next_rollout_byte_offset`
- `next_rollout_ordinal`

The projection writer applies projected turn/item/realtime changes and advances the JSONL byte/ordinal checkpoint in the **same SQLite transaction**. Its own comment states the invariant: if SQLite fails, the projection must remain behind the durable rollout rather than claiming data it did not materialize.

That establishes a useful source relationship:

> The thread-history database is a materialized projection of durable rollout history with an explicit progress checkpoint. The SQLite projection is not, by itself, evidence that the underlying rollout record can be discarded or that every historical relationship should be re-inferred from projection rows.

Timeline reads also resolve rollout lineage before querying projected segments. Accordingly, consumers should preserve the source IDs/ordinals they observe rather than casually assuming every `thread_id`-shaped value across every store has identical identity semantics in every revert/fork case.

---

# Schema evolution that matters

The state migration directory contains historical tables that are **not current `state_5.sqlite` tables**. Reading migrations as a union of every `CREATE TABLE` would manufacture a schema Codex does not currently have.

Important moves/removals:

| Historical state table(s) | What current migrations do | Current upstream location/status |
| --- | --- | --- |
| `logs` | dropped by state migration `0023` | `logs_2.sqlite` |
| `thread_goals` | created in state, then dropped by `0034` | `goals_1.sqlite` |
| `stage1_outputs`, `jobs` | old state copies dropped by `0035` | `memories_1.sqlite` |
| `device_key_bindings` | created then dropped by `0031` | not in current six-DB schema |
| `agent_jobs`, `agent_job_items` | created then dropped by `0042` | not in current six-DB schema |

This split is also why a local directory can legitimately contain several versioned SQLite files rather than one monolithic state database.

# What this means for TajsTokens source work

This upstream snapshot is strong enough to mark several things as **source-backed upstream structure**, while still keeping installed-runtime verification separate:

1. Codex currently manages multiple purpose-specific SQLite databases, not just one `state_*.sqlite` file.
2. `threads` is metadata/inventory state, and `threads.tokens_used` is a latest-observed total snapshot in the extraction path rather than an event ledger.
3. spawned-thread relationships have an explicit directional graph table.
4. projects, ordered roots, thread sections, dynamic tools, artifacts, queue revisions, goals, memories, logs, and thread-history projection all have distinct persisted structures.
5. thread history has an explicit transactional projection/checkpoint model over rollout data.
6. historical migration tables must not be reported as current simply because they once existed.

None of those statements automatically defines TajsTokens normalization or storage. They are evidence inputs to source contracts.

# Local verification checklist

When comparing this note to an installed Codex build, record the comparison rather than overwriting the upstream snapshot:

- exact installed Codex/desktop version when available;
- actual SQLite filenames present under the selected Codex home;
- `_sqlx_migrations` heads per database;
- `sqlite_master` table/index/trigger definitions;
- `PRAGMA table_xinfo` for the tables relied upon;
- any locally present columns/tables absent upstream;
- any upstream columns/tables absent locally;
- representative values only where needed, with content-bearing fields treated conservatively;
- whether a mismatch is version skew, desktop-only state, partial migration, or still unknown.

A future update to this note should change the upstream commit pin and record the delta. Do not silently rewrite old observations to make a newer Codex tree look retroactively inevitable.

# Upstream source map

Pinned to `openai/codex@eb078b4f44b0c8099d376440d63ce5bbb11675bd`:

- `codex-rs/state/src/sqlite.rs` - runtime DB filenames and connection posture
- `codex-rs/state/src/migrations.rs` - migration sets and compatibility behavior
- `codex-rs/state/migrations/` - `state_5.sqlite` schema history
- `codex-rs/state/logs_migrations/` - `logs_2.sqlite`
- `codex-rs/state/goals_migrations/` - `goals_1.sqlite`
- `codex-rs/state/memory_migrations/` - `memories_1.sqlite`
- `codex-rs/state/queue_migrations/` - `queue_1.sqlite`
- `codex-rs/state/thread_history_migrations/` - `thread_history_1.sqlite`
- `codex-rs/state/src/model/thread_metadata.rs` - canonical persisted thread metadata model/comments
- `codex-rs/state/src/extract.rs` - rollout-to-thread metadata extraction, including `tokens_used`
- `codex-rs/state/src/runtime/threads.rs` - thread queries, graph lookups, persisted metadata behavior
- `codex-rs/state/src/runtime/projects.rs` - project assignment/root/recency behavior
- `codex-rs/state/src/model/graph.rs` - spawn-edge status model
- `codex-rs/thread-store/src/local/thread_history.rs` and related thread-history modules - projection/checkpoint and paginated history behavior
