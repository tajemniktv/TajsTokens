# Codex SQLite archaeology report

**Captured:** 2026-09-01 (Europe/Warsaw)  
**Mode:** read-only inspection; no Codex database content was modified, vacuumed, migrated, or written. SQLite may create/refresh transient `-shm`/empty `-wal` sidecars when opening a WAL snapshot; those are not part of the reported main-file content.  
**Purpose:** decide what TajsTokens can obtain from Codex's own SQLite state instead of opening every rollout JSONL.

## Executive findings

- `state_5.sqlite` is a compact, indexed thread catalog. The supplied snapshot `database.sqlite` contains 318 threads; the live copy at `%USERPROFILE%\\.codex\\state_5.sqlite` contained 319 at inspection time. The 318 common rows were identical on the key metadata fields, so the supplied file is a coherent earlier snapshot.
- `threads.tokens_used` is strongly supported as the latest cumulative `total_tokens` value from the corresponding rollout: among 167 copied rollouts containing parseable `token_count` events, 166 (99.4%) matched the state value exactly. One differed by 48,409 tokens. Another 149 non-zero state rows had no parseable `token_count` event in the rollout, which makes the state aggregate valuable as a summary but not a substitute for event history.
- Parent/child topology is useful but not perfectly self-consistent: all 42 edges point to existing thread IDs, children have a single parent by schema, and there are 11 parents, but two edge children have no `thread_source='subagent'` marker and one marked subagent is not an edge child. Use `thread_spawn_edges` as the primary relationship source and rollout metadata as a repair/fallback path.
- Every usable thread in the supplied snapshot has a non-empty, unique `rollout_path`; all 318 paths existed on this machine, including 282 active and 36 archived paths. Paths are operational locators only and should not be persisted into TajsTokens' privacy-safe telemetry surface.
- `updated_at_ms` is the best cheap change candidate because it is populated for every thread, has a dedicated index, and has millisecond precision. It is not a proof of every rollout mutation: one sampled thread had a latest token event about 21 seconds after its state `updated_at_ms`, and some rows have later state updates unrelated to token events. Use a tuple fingerprint `(updated_at_ms, tokens_used, model, reasoning_effort, archived, rollout_path)` with a small overlap/recheck window.
- `thread_history_1.sqlite`, `logs_2.sqlite`, and `memories_1.sqlite` contain content-bearing JSON/text. They are not safe primary telemetry inputs. `goals_1.sqlite` has useful status/token/time summaries for three threads; `queue_1.sqlite` was empty.
- TajsTokens' current database is 34,086,912 bytes with 34,562 rollout-record rows, 15,842 activity rows, 10,495 quota rows, 5,254 context rows, and 5,219 native token rows. Rollout metadata is already batched, but semantic upserts still open a connection per call. State-driven file selection should reduce reads substantially; semantic-write batching remains a separate optimization.

## Files and SQLite header/profile inventory

Sidecar `-wal`/`-shm` files were not manually changed. The copied Codex files below are in the repository root so another agent can inspect the same snapshots.

| database | file size | journal | page size | pages | freelist | `user_version` | `schema_version` |
|---|---:|---|---:|---:|---:|---:|---:|
| `database.sqlite` (supplied state snapshot) | 2,961,408 | WAL | 4,096 | 723 | 2 | 0 | 107 |
| `state_5.sqlite` (live Codex state at inspection) | 2,969,600 | WAL | 4,096 | 725 | 0 | 0 | 107 |
| `goals_1.sqlite` | 45,056 | WAL | 4,096 | 11 | 3 | 0 | 3 |
| `memories_1.sqlite` | 708,608 | WAL | 4,096 | 178 | 0 | 0 | 5 |
| `queue_1.sqlite` | 40,960 | WAL | 4,096 | 10 | 0 | 0 | 7 |
| `logs_2.sqlite` | 549,953,536 | WAL | 4,096 | 134,266 | 85,402 | 0 | 14 |
| `thread_history_1.sqlite` | 255,012,864 | WAL | 4,096 | 62,391 | 0 | 0 | 20 |
| TajsTokens `telemetry.db` | 34,086,912 | DELETE | 4,096 | 8,322 | 0 | 5 | 40 |

The live state database was read while Codex was running. A live-turn mutation experiment was **not** performed: initiating a normal Codex turn would be an external/state-changing action. The snapshot comparison is evidence about a copied interval, not a controlled turn experiment.

## `state_5.sqlite` / `database.sqlite`

### Objects, row counts, and migrations

The supplied snapshot has these row counts (the live file had 319 `threads` rows; other counts matched at inspection):

| table | rows |
|---|---:|
| `_sqlx_migrations` | 51 |
| `backfill_state` | 1 |
| `external_agent_config_imports` | 0 |
| `project_idempotency_keys` | 20 |
| `project_roots` | 23 |
| `projects` | 17 |
| `remote_control_enrollments` | 2 |
| `rollout_migration_skipped_rollouts` | 0 |
| `rollout_migration_state` | 0 |
| `thread_artifacts` | 0 |
| `thread_dynamic_tools` | 285 |
| `thread_sections` | 3 |
| `thread_spawn_edges` | 42 |
| `threads` | 318 |

Successful migration descriptions, in order: `threads`; `logs`; `logs thread id`; `thread dynamic tools`; `threads cli version`; `memories`; `threads first user message`; `backfill state`; `stage1 outputs rollout slug`; `logs process id`; `logs partition prune indexes`; `logs estimated bytes`; `threads agent nickname`; `agent jobs`; `agent jobs max runtime seconds`; `memory usage`; `phase2 selection flag`; `phase2 selection snapshot`; `thread dynamic tools defer loading`; `threads model reasoning effort`; `thread spawn edges`; `threads agent path`; `drop logs`; `remote control enrollments`; `thread timestamps millis`; `thread dynamic tools namespace`; `threads cwd sort indexes`; `device key bindings`; `thread goals`; `threads thread source`; `drop device key bindings`; `threads preview`; `thread goal stopped statuses`; `drop thread goals`; `drop memory tables`; `threads visible sort indexes`; `remote control enrollments enabled`; `external agent config imports`; `threads recency at`; `threads history mode`; `threads name`; `drop agent jobs`; `threads is pinned`; `external agent config imports provider id`; `threads section`; `threads section order`; `rollout migration state`; `thread section appearance`; `projects`; `threads section empty preview indexes`; `thread artifacts`.

### Full `PRAGMA table_info(threads)`

`cid | name | type | notnull | default | pk`

```text
0  | id               | TEXT    | 0 | NULL    | 1
1  | rollout_path     | TEXT    | 1 | NULL    | 0
2  | created_at       | INTEGER | 1 | NULL    | 0
3  | updated_at       | INTEGER | 1 | NULL    | 0
4  | source           | TEXT    | 1 | NULL    | 0
5  | model_provider   | TEXT    | 1 | NULL    | 0
6  | cwd              | TEXT    | 1 | NULL    | 0
7  | title            | TEXT    | 1 | NULL    | 0
8  | sandbox_policy   | TEXT    | 1 | NULL    | 0
9  | approval_mode    | TEXT    | 1 | NULL    | 0
10 | tokens_used      | INTEGER | 1 | 0       | 0
11 | has_user_event   | INTEGER | 1 | 0       | 0
12 | archived         | INTEGER | 1 | 0       | 0
13 | archived_at      | INTEGER | 0 | NULL    | 0
14 | git_sha          | TEXT    | 0 | NULL    | 0
15 | git_branch       | TEXT    | 0 | NULL    | 0
16 | git_origin_url   | TEXT    | 0 | NULL    | 0
17 | cli_version      | TEXT    | 1 | ''      | 0
18 | first_user_message | TEXT  | 1 | ''      | 0
19 | agent_nickname   | TEXT    | 0 | NULL    | 0
20 | agent_role       | TEXT    | 0 | NULL    | 0
21 | memory_mode      | TEXT    | 1 | 'enabled' | 0
22 | model            | TEXT    | 0 | NULL    | 0
23 | reasoning_effort | TEXT    | 0 | NULL    | 0
24 | agent_path       | TEXT    | 0 | NULL    | 0
25 | created_at_ms    | INTEGER | 0 | NULL    | 0
26 | updated_at_ms    | INTEGER | 0 | NULL    | 0
27 | thread_source    | TEXT    | 0 | NULL    | 0
28 | preview          | TEXT    | 1 | ''      | 0
29 | recency_at       | INTEGER | 1 | 0       | 0
30 | recency_at_ms    | INTEGER | 1 | 0       | 0
31 | history_mode     | TEXT    | 1 | 'legacy' | 0
32 | name             | TEXT    | 0 | NULL    | 0
33 | is_pinned        | INTEGER | 1 | 0       | 0
34 | thread_section_id | TEXT   | 0 | NULL    | 0
35 | section_position | INTEGER | 0 | NULL    | 0
36 | section_entered_at_ms | INTEGER | 0 | NULL | 0
37 | project_id       | TEXT    | 0 | NULL    | 0
```

### Full `PRAGMA table_info(thread_spawn_edges)`

```text
0 | parent_thread_id | TEXT | 1 | NULL | 0
1 | child_thread_id  | TEXT | 1 | NULL | 1
2 | status           | TEXT | 1 | NULL | 0
```

### Indexes on `threads` and `thread_spawn_edges`

```sql
CREATE INDEX idx_thread_spawn_edges_parent_status
  ON thread_spawn_edges(parent_thread_id, status);
-- implicit UNIQUE primary-key index: child_thread_id

CREATE INDEX idx_threads_archived ON threads(archived);
CREATE INDEX idx_threads_archived_cwd_created_at_ms
  ON threads(archived, cwd, created_at_ms DESC, id DESC);
CREATE INDEX idx_threads_archived_cwd_recency_at_ms
  ON threads(archived, cwd, recency_at_ms DESC, id DESC);
CREATE INDEX idx_threads_archived_cwd_updated_at_ms
  ON threads(archived, cwd, updated_at_ms DESC, id DESC);
CREATE INDEX idx_threads_created_at ON threads(created_at DESC, id DESC);
CREATE INDEX idx_threads_created_at_ms ON threads(created_at_ms DESC, id DESC);
CREATE INDEX idx_threads_pinned_recency_at_ms
  ON threads(archived, recency_at_ms DESC, id DESC)
  WHERE is_pinned = 1 AND preview <> '';
CREATE INDEX idx_threads_project_id
  ON threads(project_id, archived, created_at_ms DESC, id DESC)
  WHERE project_id IS NOT NULL;
CREATE INDEX idx_threads_provider ON threads(model_provider);
CREATE INDEX idx_threads_recency_at_ms ON threads(recency_at_ms DESC, id DESC);
CREATE INDEX idx_threads_section_position
  ON threads(archived, thread_section_id, section_position ASC, id ASC)
  WHERE thread_section_id IS NOT NULL;
CREATE INDEX idx_threads_section_recency_at_ms
  ON threads(archived, thread_section_id, recency_at_ms DESC, id DESC)
  WHERE thread_section_id IS NOT NULL;
CREATE INDEX idx_threads_source ON threads(source);
CREATE INDEX idx_threads_updated_at ON threads(updated_at DESC, id DESC);
CREATE INDEX idx_threads_updated_at_ms ON threads(updated_at_ms DESC, id DESC);
CREATE INDEX idx_threads_visible_created_at_ms
  ON threads(archived, created_at_ms DESC) WHERE preview <> '';
CREATE INDEX idx_threads_visible_recency_at_ms
  ON threads(archived, recency_at_ms DESC, id DESC) WHERE preview <> '';
CREATE INDEX idx_threads_visible_updated_at_ms
  ON threads(archived, updated_at_ms DESC) WHERE preview <> '';
```

### Full state table schema (SQLite `sqlite_master`)

```sql
CREATE TABLE _sqlx_migrations (
    version BIGINT PRIMARY KEY, description TEXT NOT NULL,
    installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL
);
CREATE TABLE backfill_state (
    id INTEGER PRIMARY KEY CHECK (id = 1), status TEXT NOT NULL,
    last_watermark TEXT, last_success_at INTEGER, updated_at INTEGER NOT NULL
);
CREATE TABLE external_agent_config_imports (
    import_id TEXT PRIMARY KEY, completed_at_ms INTEGER NOT NULL,
    successes TEXT NOT NULL, failures TEXT NOT NULL, provider_id TEXT
);
CREATE TABLE project_idempotency_keys (
    key TEXT PRIMARY KEY, project_id TEXT NOT NULL, created_at_ms INTEGER NOT NULL
);
CREATE TABLE project_roots (
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    position INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY (project_id, position)
);
CREATE TABLE projects (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, metadata TEXT NOT NULL DEFAULT '{}',
    position INTEGER NOT NULL, created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL
);
CREATE TABLE remote_control_enrollments (
    websocket_url TEXT NOT NULL, account_id TEXT NOT NULL,
    app_server_client_name TEXT NOT NULL, server_id TEXT NOT NULL,
    environment_id TEXT NOT NULL, server_name TEXT NOT NULL, updated_at INTEGER NOT NULL,
    remote_control_enabled INTEGER,
    PRIMARY KEY (websocket_url, account_id, app_server_client_name)
);
CREATE TABLE rollout_migration_skipped_rollouts (
    migration_id TEXT NOT NULL, rollout_path TEXT NOT NULL,
    rollout_size_bytes INTEGER NOT NULL, rollout_modified_at_ns INTEGER NOT NULL,
    skip_reason TEXT NOT NULL, skipped_at INTEGER NOT NULL,
    PRIMARY KEY (migration_id, rollout_path)
);
CREATE TABLE rollout_migration_state (
    migration_id TEXT PRIMARY KEY, last_checked_thread_created_at INTEGER,
    last_checked_thread_id TEXT, updated_at INTEGER NOT NULL
);
CREATE TABLE thread_artifacts (
    id TEXT PRIMARY KEY, thread_id TEXT NOT NULL REFERENCES threads(id) ON DELETE CASCADE,
    artifact_type TEXT NOT NULL, identity_key TEXT NOT NULL, payload TEXT NOT NULL,
    created_at INTEGER NOT NULL, UNIQUE (thread_id, artifact_type, identity_key)
);
CREATE TABLE thread_dynamic_tools (
    thread_id TEXT NOT NULL, position INTEGER NOT NULL, name TEXT NOT NULL,
    description TEXT NOT NULL, input_schema TEXT NOT NULL,
    defer_loading INTEGER NOT NULL DEFAULT 0, namespace TEXT,
    PRIMARY KEY(thread_id, position), FOREIGN KEY(thread_id) REFERENCES threads(id) ON DELETE CASCADE
);
CREATE TABLE thread_sections (id TEXT PRIMARY KEY, name TEXT NOT NULL, appearance TEXT);
CREATE TABLE thread_spawn_edges (
    parent_thread_id TEXT NOT NULL, child_thread_id TEXT NOT NULL PRIMARY KEY,
    status TEXT NOT NULL
);
CREATE TABLE threads (
    id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL, created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL, source TEXT NOT NULL, model_provider TEXT NOT NULL,
    cwd TEXT NOT NULL, title TEXT NOT NULL, sandbox_policy TEXT NOT NULL,
    approval_mode TEXT NOT NULL, tokens_used INTEGER NOT NULL DEFAULT 0,
    has_user_event INTEGER NOT NULL DEFAULT 0, archived INTEGER NOT NULL DEFAULT 0,
    archived_at INTEGER, git_sha TEXT, git_branch TEXT, git_origin_url TEXT,
    cli_version TEXT NOT NULL DEFAULT '', first_user_message TEXT NOT NULL DEFAULT '',
    agent_nickname TEXT, agent_role TEXT, memory_mode TEXT NOT NULL DEFAULT 'enabled',
    model TEXT, reasoning_effort TEXT, agent_path TEXT, created_at_ms INTEGER,
    updated_at_ms INTEGER, thread_source TEXT, preview TEXT NOT NULL DEFAULT '',
    recency_at INTEGER NOT NULL DEFAULT 0, recency_at_ms INTEGER NOT NULL DEFAULT 0,
    history_mode TEXT NOT NULL DEFAULT 'legacy', name TEXT,
    is_pinned INTEGER NOT NULL DEFAULT 0,
    thread_section_id TEXT REFERENCES thread_sections(id) ON DELETE SET NULL,
    section_position INTEGER, section_entered_at_ms INTEGER,
    project_id TEXT REFERENCES projects(id) ON DELETE SET NULL
);
CREATE INDEX idx_projects_position ON projects(position ASC, id ASC);
CREATE INDEX idx_thread_artifacts_thread_created_id
  ON thread_artifacts(thread_id, created_at, id);
CREATE INDEX idx_thread_dynamic_tools_thread ON thread_dynamic_tools(thread_id);
-- The remaining idx_threads_* and idx_thread_spawn_edges_* definitions are listed above.
CREATE TRIGGER threads_created_at_ms_after_insert
AFTER INSERT ON threads WHEN NEW.created_at_ms IS NULL BEGIN
  UPDATE threads SET created_at_ms = NEW.created_at * 1000 WHERE id = NEW.id;
END;
CREATE TRIGGER threads_created_at_ms_after_update
AFTER UPDATE OF created_at ON threads
WHEN NEW.created_at != OLD.created_at AND NEW.created_at_ms IS OLD.created_at_ms BEGIN
  UPDATE threads SET created_at_ms = NEW.created_at * 1000 WHERE id = NEW.id;
END;
CREATE TRIGGER threads_recency_at_after_insert
AFTER INSERT ON threads WHEN NEW.recency_at_ms = 0 BEGIN
  UPDATE threads SET recency_at = NEW.updated_at,
                     recency_at_ms = COALESCE(NEW.updated_at_ms, NEW.updated_at * 1000)
  WHERE id = NEW.id;
END;
CREATE TRIGGER threads_updated_at_ms_after_insert
AFTER INSERT ON threads WHEN NEW.updated_at_ms IS NULL BEGIN
  UPDATE threads SET updated_at_ms = NEW.updated_at * 1000 WHERE id = NEW.id;
END;
CREATE TRIGGER threads_updated_at_ms_after_update
AFTER UPDATE OF updated_at ON threads
WHEN NEW.updated_at != OLD.updated_at AND NEW.updated_at_ms IS OLD.updated_at_ms BEGIN
  UPDATE threads SET updated_at_ms = NEW.updated_at * 1000 WHERE id = NEW.id;
END;
```

The five `threads_*` triggers fill millisecond fields from second fields when omitted and initialize `recency_at*` from `updated_at*` when `recency_at_ms=0`. They do not establish a formal change-log table.

### Completeness and topology

For the supplied 318-row snapshot:

| field | non-null/non-empty rows | note |
|---|---:|---|
| `rollout_path` | 318/318 | unique, filename UUID matched `threads.id` for all rows |
| `model` | 317/318 | one null |
| `reasoning_effort` | 317/318 | one null |
| `tokens_used` | 318/318 | two zero values; sum 7,655,435,032; max 591,020,122 |
| `agent_nickname` | 42/318 | expected mostly for subagents |
| `agent_role` | 2/318 | sparse |
| `agent_path` | 40/318 | sparse, mostly subagent rows |
| `thread_source` | 185/318 | 144 `user`, 41 `subagent`, 133 null |
| `project_id` | 0/318 | projects exist (17 rows) but no thread is linked |

Topology checks: 42 edges, 11 distinct parents, 42 distinct children, no missing parent/child IDs, no self-cycles, no child with multiple parents, and all edge statuses were `open`. Parents were active; 40 children were active and two archived. Two edge children had a null/non-subagent source marker, and one `thread_source='subagent'` row was not present as an edge child. This is sufficient to avoid reconstructing ordinary topology from every rollout, but not sufficient to treat the source marker as authoritative.

### Recent metadata sample (no title, preview, or message text)

The following is from the supplied snapshot. Paths are represented by rollout basename only; agent paths are reduced to their final component.

| id | created UTC | updated UTC | recency UTC | source | thread source | model | reasoning | tokens | archived | rollout file | agent / role-path |
|---|---|---|---|---|---|---|---|---:|---:|---|---|
| `01a05b1f-ae70-7433-a5bc-d2b3ac0ede30` | 2026-09-01 03:59:58.577Z | 2026-09-01 05:10:35.017Z | 2026-09-01 05:01:25.104Z | vscode | user | gpt-5.6-luna | xhigh | 3,849,381 | 0 | `rollout-2026-09-01T05-59-58-01a05b1f-ae70-7433-a5bc-d2b3ac0ede30.jsonl` | null / null |
| `01a05b2f-fdf2-78f0-815d-441200008937` | 2026-09-01 04:17:46.995Z | 2026-09-01 04:37:45.327Z | 2026-09-01 04:37:26.321Z | vscode | user | gpt-5.6-luna | high | 198,442 | 0 | `rollout-2026-09-01T06-17-46-01a05b2f-fdf2-78f0-815d-441200008937.jsonl` | null / null |
| `01a05ae9-fb1d-7833-8a6a-4728df5ec804` | 2026-09-01 03:01:18.749Z | 2026-09-01 03:38:31.487Z | 2026-09-01 03:01:25.047Z | vscode | user | gpt-5.6-luna | xhigh | 27,856,372 | 0 | `rollout-2026-09-01T05-01-19-01a05ae9-fb1d-7833-8a6a-4728df5ec804.jsonl` | null / null |
| `01a05ac5-1bdd-75b0-9d74-e03536438743` | 2026-09-01 02:21:02.301Z | 2026-09-01 02:56:55.298Z | 2026-09-01 02:24:58.272Z | vscode | user | gpt-5.6-luna | xhigh | 25,548,305 | 0 | `rollout-2026-09-01T04-21-02-01a05ac5-1bdd-75b0-9d74-e03536438743.jsonl` | null / null |
| `01a05abc-167d-7541-b39e-97ba24f49b5e` | 2026-09-01 02:11:11.101Z | 2026-09-01 02:44:58.819Z | 2026-09-01 02:11:57.989Z | vscode | user | gpt-5.6-luna | xhigh | 24,009,233 | 0 | `rollout-2026-09-01T04-11-11-01a05abc-167d-7541-b39e-97ba24f49b5e.jsonl` | null / null |
| `01a05ac6-743c-76d0-8c10-2f730e08436b` | 2026-09-01 02:22:30.460Z | 2026-09-01 02:22:37.972Z | 2026-09-01 02:22:38.772Z | vscode | user | gpt-5.6-luna | xhigh | 80,774 | 1 | `rollout-2026-09-01T04-22-31-01a05ac6-743c-76d0-8c10-2f730e08436b.jsonl` | null / null |
| `01a05a9e-9462-7b80-9bff-351c53b2bd84` | 2026-09-01 01:38:57.250Z | 2026-09-01 02:12:30.294Z | 2026-09-01 01:39:25.105Z | vscode | user | gpt-5.6-luna | xhigh | 26,657,122 | 0 | `rollout-2026-09-01T03-38-57-01a05a9e-9462-7b80-9bff-351c53b2bd84.jsonl` | null / null |
| `01a05a8d-d3a7-7e83-8caa-983e1878c2d8` | 2026-09-01 01:20:39.335Z | 2026-09-01 01:38:18.971Z | 2026-09-01 01:21:02.109Z | vscode | user | gpt-5.6-luna | xhigh | 8,838,678 | 0 | `rollout-2026-09-01T03-20-39-01a05a8d-d3a7-7e83-8caa-983e1878c2d8.jsonl` | null / null |
| `01a05a83-2df6-7252-8d10-b80c2c69b9fc` | 2026-09-01 01:09:01.558Z | 2026-09-01 01:20:23.163Z | 2026-09-01 01:09:25.558Z | vscode | user | gpt-5.6-luna | xhigh | 5,020,726 | 0 | `rollout-2026-09-01T03-09-02-01a05a83-2df6-7252-8d10-b80c2c69b9fc.jsonl` | null / null |
| `01a05a84-5296-7c91-8303-438445304aee` | 2026-09-01 01:10:16.470Z | 2026-09-01 01:16:16.122Z | 2026-09-01 01:10:19.603Z | subagent | subagent | gpt-5.6-luna | xhigh | 2,286,136 | 0 | `rollout-2026-09-01T03-10-16-01a05a84-5296-7c91-8303-438445304aee.jsonl` | Noether / research_pending |
| `01a05a84-309a-7441-aa19-8d8694258965` | 2026-09-01 01:10:07.770Z | 2026-09-01 01:13:56.458Z | 2026-09-01 01:10:10.610Z | subagent | subagent | gpt-5.6-luna | xhigh | 1,173,510 | 0 | `rollout-2026-09-01T03-10-08-01a05a84-309a-7441-aa19-8d8694258965.jsonl` | Kant / mine_tint |
| `01a05a25-1657-71a0-b3f3-fc49169b6ee8` | 2026-08-31 23:26:15.127Z | 2026-08-31 23:53:28.171Z | 2026-08-31 23:27:10.210Z | vscode | user | gpt-5.6-luna | xhigh | 17,842,354 | 0 | `rollout-2026-09-01T01-26-15-01a05a25-1657-71a0-b3f3-fc49169b6ee8.jsonl` | null / null |
| `01a05a07-3871-7943-8446-232d3b20f20a` | 2026-08-31 22:53:37.777Z | 2026-08-31 23:23:30.337Z | 2026-08-31 22:54:46.839Z | vscode | user | gpt-5.6-luna | xhigh | 21,246,558 | 0 | `rollout-2026-09-01T00-53-38-01a05a07-3871-7943-8446-232d3b20f20a.jsonl` | null / null |
| `01a05633-4c8b-7173-a000-3385d637b055` | 2026-08-31 05:03:17.644Z | 2026-08-31 05:58:06.633Z | 2026-08-31 05:03:54.340Z | vscode | user | gpt-5.6-luna | xhigh | 45,779,859 | 0 | `rollout-2026-08-31T07-03-18-01a05633-4c8b-7173-a000-3385d637b055.jsonl` | null / null |
| `01a05634-f844-7653-864c-2874b6cf53fc` | 2026-08-31 05:05:07.140Z | 2026-08-31 05:16:35.408Z | 2026-08-31 05:05:10.814Z | subagent | subagent | gpt-5.6-luna | xhigh | 8,748,009 | 0 | `rollout-2026-08-31T07-05-07-01a05634-f844-7653-864c-2874b6cf53fc.jsonl` | Dalton / audit_auto |

### `tokens_used` and rollout evidence

The copied state values were compared with the latest top-level `event_msg`/`token_count` `info.total_token_usage.total_tokens` in each referenced JSONL, without printing message content:

| comparison | result |
|---|---:|
| threads in snapshot | 318 |
| referenced rollouts found | 318 |
| rollouts with parseable `token_count` events | 167 |
| exact state/latest-rollout matches | 166 |
| differing matches | 1 (state was 11,898,739; latest event was 11,410,330) |
| non-zero state rows without a parseable token event | 149 |

The one mismatch is a reason to retain a reconciliation check, not a reason to discard the field. The field is cumulative per thread, not an event stream and not a disjoint input/cache/output breakdown. Do not sum every historical `token_count` event; use the latest value or positive deltas between verified state observations.

### Cheap change detection

All 318 snapshot rows had non-null `updated_at_ms`, `created_at_ms`, and non-zero `recency_at_ms`; all millisecond values were genuinely sub-second relative to the integer-second fields. In the snapshot, `recency_at_ms` equaled `updated_at_ms` for 154 rows, was newer for 9, and `updated_at_ms` was newer for 155. The state DB has indexes on `updated_at_ms` and `recency_at_ms` (including archived/cwd composites).

Use:

```text
WHERE updated_at_ms > :watermark
ORDER BY updated_at_ms, id
```

and compare `(updated_at_ms, tokens_used, model, reasoning_effort, archived, rollout_path)` per thread. Re-read a small overlap (for example, the last minute) because the source has no durable change sequence, can be read during a WAL commit, and one sampled latest token event was later than `updated_at_ms`. `recency_at_ms` is primarily a display-order field; `tokens_used` alone misses metadata/path/archive changes. Keep a fallback full checkpoint walk when the source schema/version changes or the DB is unavailable.

### Before/after mutation experiment

`state_5.sqlite` is the useful baseline for this comparison because the older supplied `database.sqlite` snapshot predates the thread. Comparing the 319-row `state_5.sqlite` copy with `state_5_mutated.sqlite`:

| field for thread `01a05c52-16e8-7152-a439-18bf1c3b1b7a` | baseline | mutated | delta |
|---|---:|---:|---:|
| `updated_at` (UTC seconds) | 2026-09-01 09:41:19 | 2026-09-01 09:48:07 | +408 s |
| `updated_at_ms` | 2026-09-01 09:41:19.341Z | 2026-09-01 09:48:07.476Z | +408,135 ms |
| `tokens_used` | 2,417,604 | 5,594,597 | +3,176,993 |
| `recency_at_ms` | 2026-09-01 09:37:12.238Z | unchanged | 0 |
| `model` / `reasoning_effort` | gpt-5.6-luna / xhigh | unchanged | — |
| `archived` / `rollout_path` | unchanged | unchanged | — |

Only this one thread row changed; all other thread rows and every other state table had identical row counts/content hashes between the two snapshots. The associated rollout contained 20 `token_count` events in the interval. The event nearest the baseline state was `2026-09-01T09:41:19.338Z` with `total_tokens=2,417,604`; the event nearest the mutated state was `2026-09-01T09:48:07.472Z` with `total_tokens=5,594,597`. The state timestamps lagged those event timestamps by only 3–4 ms.

**Conclusion:** for this normal-turn sample, `updated_at_ms` is a reliable cheap trigger and `tokens_used` is the latest cumulative rollout total. `recency_at_ms` is not a change journal and must not drive ingestion. The implementation should query changed rows by `updated_at_ms`, include `tokens_used` in the fingerprint, and then open only the associated rollout paths. This is strong evidence for the proposed state-DB-first design, with the previously noted overlap/fallback safeguards for WAL races, schema drift, and rare state/rollout lag.

## Companion Codex databases

### `goals_1.sqlite`

Migrations: `thread goals`; `thread goal continuation deferrals`. Tables/indexes:

```sql
CREATE TABLE _sqlx_migrations (
  version BIGINT PRIMARY KEY, description TEXT NOT NULL,
  installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL
);
CREATE TABLE thread_goals (
  thread_id TEXT PRIMARY KEY NOT NULL, goal_id TEXT NOT NULL, objective TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('active','paused','blocked','usage_limited','budget_limited','complete')),
  token_budget INTEGER, tokens_used INTEGER NOT NULL DEFAULT 0,
  time_used_seconds INTEGER NOT NULL DEFAULT 0,
  created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL
);
CREATE TABLE thread_goal_continuation_deferrals (
  thread_id TEXT PRIMARY KEY NOT NULL REFERENCES thread_goals(thread_id) ON DELETE CASCADE
);
```

Counts: `_sqlx_migrations` 2, `thread_goals` 3, `thread_goal_continuation_deferrals` 0; no explicit secondary indexes. All three goal thread IDs existed in `state_5.sqlite`. Statuses were two `paused` and one `usage_limited`; aggregate `tokens_used` 7,351,297 and `time_used_seconds` 26,839. Objectives are private text and were not exported.

**Use:** status, token/time budget summaries, and goal lifecycle can enrich a session detail view. Keep `objective` out of normal telemetry unless the user explicitly opts in.

### `memories_1.sqlite`

Migration: `memories`. Tables/indexes:

```sql
CREATE TABLE _sqlx_migrations (
  version BIGINT PRIMARY KEY, description TEXT NOT NULL,
  installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL
);
CREATE TABLE jobs (
  kind TEXT NOT NULL, job_key TEXT NOT NULL, status TEXT NOT NULL,
  worker_id TEXT, ownership_token TEXT, started_at INTEGER, finished_at INTEGER,
  lease_until INTEGER, retry_at INTEGER, retry_remaining INTEGER NOT NULL,
  last_error TEXT, input_watermark INTEGER, last_success_watermark INTEGER,
  PRIMARY KEY (kind, job_key)
);
CREATE TABLE stage1_outputs (
  thread_id TEXT PRIMARY KEY, source_updated_at INTEGER NOT NULL,
  raw_memory TEXT NOT NULL, rollout_summary TEXT NOT NULL, rollout_slug TEXT,
  generated_at INTEGER NOT NULL, usage_count INTEGER, last_usage INTEGER,
  selected_for_phase2 INTEGER NOT NULL DEFAULT 0,
  selected_for_phase2_source_updated_at INTEGER
);
CREATE INDEX idx_jobs_kind_status_retry_lease
  ON jobs(kind, status, retry_at, lease_until);
CREATE INDEX idx_stage1_outputs_source_updated_at
  ON stage1_outputs(source_updated_at DESC, thread_id DESC);
```

Counts: `_sqlx_migrations` 1, `jobs` 117, `stage1_outputs` 73. Jobs were mostly `memory_stage1/done` (113), with 3 errors and 1 pending global consolidation. All 73 stage rows had rollout slugs; 69 were selected for phase 2. `raw_memory` and `rollout_summary` are content-bearing and were not read/exported.

**Use:** only operational freshness/status or selected flags, if needed. Do not use raw memory/summary as telemetry.

### `queue_1.sqlite`

Migrations: `queued items`; `queued thread revisions`. Tables/indexes/triggers:

```sql
CREATE TABLE _sqlx_migrations (
  version BIGINT PRIMARY KEY, description TEXT NOT NULL,
  installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL
);
CREATE TABLE queued_items (
  id TEXT PRIMARY KEY NOT NULL, thread_id TEXT NOT NULL,
  payload_json TEXT NOT NULL, queue_order INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL
);
CREATE TABLE queued_thread_revisions (
  revision INTEGER PRIMARY KEY AUTOINCREMENT,
  thread_id TEXT NOT NULL UNIQUE
);
CREATE UNIQUE INDEX queued_items_thread_order_idx
  ON queued_items(thread_id, queue_order);
```

The snapshot contained zero queued items and zero revisions (plus the empty `sqlite_sequence` table). The three revision triggers advance a per-thread revision after insert/update/delete. The payload is private and was not inspected.

### `logs_2.sqlite`

Migrations: `logs`; `logs feedback log body`. Tables/indexes:

```sql
CREATE TABLE _sqlx_migrations (
  version BIGINT PRIMARY KEY, description TEXT NOT NULL,
  installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL
);
CREATE TABLE logs (
  id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, ts_nanos INTEGER NOT NULL,
  level TEXT NOT NULL, target TEXT NOT NULL, feedback_log_body TEXT,
  module_path TEXT, file TEXT, line INTEGER, thread_id TEXT, process_uuid TEXT,
  estimated_bytes INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_logs_process_uuid_threadless_ts
  ON logs(process_uuid, ts DESC, ts_nanos DESC, id DESC) WHERE thread_id IS NULL;
CREATE INDEX idx_logs_thread_id ON logs(thread_id);
CREATE INDEX idx_logs_thread_id_ts
  ON logs(thread_id, ts DESC, ts_nanos DESC, id DESC);
CREATE INDEX idx_logs_ts ON logs(ts DESC, ts_nanos DESC, id DESC);
```

Counts: `_sqlx_migrations` 2, `logs` 126,970, `sqlite_sequence` 1. The file is 549,953,536 bytes with 85,402 free pages. 48,073 rows have no thread ID; 99 distinct process UUIDs and 228 distinct thread IDs occur. Levels were TRACE 48,472; INFO 39,335; DEBUG 34,502; WARN 4,260; ERROR 401. `feedback_log_body` was non-empty in all 126,970 rows and was intentionally not dumped.

**Use:** none for ordinary TajsTokens telemetry. It is a large content/log store; at most use bounded, explicitly opt-in diagnostics with aggressive redaction.

### `thread_history_1.sqlite`

Migrations: `thread history`; `thread items item type`; `turn rollout positions`; `thread items updated at ordinal`; `thread realtime items`; `thread turn ends`. Tables/indexes/triggers:

```sql
CREATE TABLE _sqlx_migrations (
  version BIGINT PRIMARY KEY, description TEXT NOT NULL,
  installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  success BOOLEAN NOT NULL, checksum BLOB NOT NULL, execution_time BIGINT NOT NULL
);
CREATE TABLE thread_history_projection_state (
  thread_id TEXT PRIMARY KEY, next_rollout_byte_offset INTEGER NOT NULL,
  next_rollout_ordinal INTEGER NOT NULL
);
CREATE TABLE thread_items (
  thread_id TEXT NOT NULL, turn_id TEXT NOT NULL, item_id TEXT NOT NULL,
  rollout_ordinal INTEGER NOT NULL, created_at_ms INTEGER NOT NULL,
  item_json TEXT NOT NULL, item_type TEXT NOT NULL DEFAULT '',
  updated_at_ordinal INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (thread_id, turn_id, item_id)
);
CREATE TABLE thread_realtime_items (
  thread_id TEXT NOT NULL, item_id TEXT NOT NULL, rollout_ordinal INTEGER NOT NULL,
  created_at_ms INTEGER NOT NULL, item_type TEXT NOT NULL, item_json TEXT NOT NULL,
  PRIMARY KEY (thread_id, item_id)
);
CREATE TABLE thread_turns (
  thread_id TEXT NOT NULL, turn_id TEXT NOT NULL, rollout_ordinal INTEGER NOT NULL,
  status TEXT NOT NULL, error_json TEXT, started_at INTEGER, completed_at INTEGER,
  duration_ms INTEGER, first_user_item_id TEXT, final_agent_item_id TEXT,
  rollout_byte_offset INTEGER, rollout_end_ordinal INTEGER, rollout_end_byte_offset INTEGER,
  PRIMARY KEY (thread_id, turn_id)
);
CREATE UNIQUE INDEX idx_thread_items_page ON thread_items(thread_id, rollout_ordinal);
CREATE INDEX idx_thread_items_by_turn_page ON thread_items(thread_id, turn_id, rollout_ordinal);
CREATE INDEX idx_thread_items_by_turn_updated_page ON thread_items(thread_id, turn_id, updated_at_ordinal);
CREATE INDEX idx_thread_items_updated_page ON thread_items(thread_id, updated_at_ordinal);
CREATE INDEX idx_thread_items_user_messages ON thread_items(thread_id, rollout_ordinal) WHERE item_type = 'userMessage';
CREATE UNIQUE INDEX idx_thread_realtime_items_page ON thread_realtime_items(thread_id, rollout_ordinal);
CREATE INDEX idx_thread_realtime_items_boundary ON thread_realtime_items(thread_id, rollout_ordinal) WHERE item_type IN ('realtime_session_started','realtime_session_closed');
CREATE UNIQUE INDEX idx_thread_turns_page ON thread_turns(thread_id, rollout_ordinal);
CREATE INDEX idx_thread_turns_end_page ON thread_turns(thread_id, rollout_end_ordinal, turn_id) WHERE rollout_end_ordinal IS NOT NULL;
```

Counts: `_sqlx_migrations` 6, `thread_history_projection_state` 100, `thread_items` 40,425, `thread_realtime_items` 0, `thread_turns` 261. `thread_items` covers 100 threads and 260 turns; item types are predominantly `reasoning` (19,024), `commandExecution` (15,486), `fileChange` (2,875), and `userMessage` (202). `item_json` is content-bearing and was not read/exported.

**Use:** only if a future, explicit privacy-preserving projection is designed. It is not a safe replacement for rollout parsing today.

## TajsTokens `telemetry.db`

Path inspected: `%LOCALAPPDATA%\\TajsTokens\\telemetry.db`. Header: DELETE journal, 4,096-byte pages, 8,322 pages, zero freelist pages, `user_version=5`, SQLite schema version 40. Component versions were `codex-observatory=3` and `phase4-intelligence=1`.

### Tables, indexes, and row counts

| table | rows | relevant role |
|---|---:|---|
| `rollout_records` | 34,562 | per-record content-free storage metadata |
| `usage_events` | 15,842 | content-free timeline/activity |
| `quota_snapshots` | 10,495 | provider/rollout quota observations |
| `context_observations` | 5,254 | context/compaction observations |
| `codex_native_token_events` | 5,219 | disjoint native-shadow token deltas |
| `codex_counter_state` | 42 | latest cumulative counter per session |
| `codex_parser_state` | 42 | parser resume/semantic state |
| `rollout_files` | 42 | stable source identity/file label |
| `ingestion_checkpoints` | 42 | exact complete-record byte offsets |
| `sessions` | 42 | normalized sessions |
| `agents` | 42 | normalized agents |
| `agent_relationships` | 23 | normalized parent/child links |
| `observatory_schema` | 1 | component schema version |
| `intelligence_schema` | 1 | component schema version |
| `token_usage` | 0 | broad-provider history table, currently unused |
| `forecast_snapshots` | 0 | forecast history, currently empty |
| `quota_reset_events` | 0 | Phase 4 reset events, currently empty |
| `reset_events` | 0 | legacy/reset table, currently empty |
| `repositories` | 0 | identity table, currently unused |
| `workspaces` | 0 | identity table, currently unused |
| `announcements` | 0 | empty |
| `sqlite_sequence` | 0 | SQLite bookkeeping |

Indexes:

```sql
CREATE INDEX idx_agent_relationships_child ON agent_relationships(child_agent_id);
CREATE INDEX idx_context_session_time ON context_observations(session_id, observed_at_utc DESC);
CREATE INDEX idx_forecast_snapshots_lookup ON forecast_snapshots(provider, profile, kind, generated_at_utc DESC);
CREATE INDEX idx_native_tokens_observed_time ON codex_native_token_events(observed_at_utc DESC);
CREATE INDEX idx_native_tokens_session_time ON codex_native_token_events(session_id, observed_at_utc DESC);
CREATE INDEX idx_quota_reset_events_time ON quota_reset_events(detected_at_utc DESC);
CREATE INDEX idx_quota_reset_events_window ON quota_reset_events(provider, profile, kind, detected_at_utc DESC);
CREATE INDEX idx_quota_snapshots_lookup ON quota_snapshots(provider, profile, kind, captured_at_utc DESC);
CREATE INDEX idx_rollout_files_path ON rollout_files(file_path);
CREATE INDEX idx_rollout_records_identity ON rollout_records(source_identity);
CREATE INDEX idx_rollout_records_session ON rollout_records(session_id, observed_at_utc DESC);
CREATE INDEX idx_token_usage_observed ON token_usage(observed_at_utc DESC);
CREATE INDEX idx_usage_events_time ON usage_events(timestamp_utc DESC);
CREATE INDEX idx_workspaces_repository ON workspaces(repository_id);
```

Full table definitions from `sqlite_master`:

```sql
CREATE TABLE agent_relationships (
  parent_agent_id TEXT NOT NULL, child_agent_id TEXT NOT NULL,
  linked_at_utc TEXT NOT NULL, PRIMARY KEY(parent_agent_id, child_agent_id)
);
CREATE TABLE agents (
  agent_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, name TEXT NOT NULL,
  state TEXT NOT NULL, last_seen_utc TEXT NOT NULL, model TEXT
);
CREATE TABLE announcements (
  announcement_id TEXT PRIMARY KEY, published_at_utc TEXT NOT NULL,
  source TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL, link TEXT
);
CREATE TABLE codex_counter_state (
  session_id TEXT PRIMARY KEY, source_file TEXT NOT NULL, counter_epoch INTEGER NOT NULL,
  input_tokens INTEGER NOT NULL, cached_input_tokens INTEGER NOT NULL,
  cache_write_input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
  reasoning_output_tokens INTEGER NOT NULL, total_tokens INTEGER NOT NULL,
  last_source_event_id TEXT NOT NULL, last_observed_at_utc TEXT NOT NULL
);
CREATE TABLE codex_native_token_events (
  source_event_id TEXT PRIMARY KEY, source_file TEXT NOT NULL, session_id TEXT NOT NULL,
  agent_id TEXT, observed_at_utc TEXT NOT NULL, model TEXT, reasoning_effort TEXT,
  counter_epoch INTEGER NOT NULL, uncached_input_tokens INTEGER NOT NULL,
  cache_read_tokens INTEGER NOT NULL, cache_write_tokens INTEGER NOT NULL,
  non_reasoning_output_tokens INTEGER NOT NULL, reasoning_output_tokens INTEGER NOT NULL,
  reported_total_tokens INTEGER NOT NULL
);
CREATE TABLE codex_parser_state (
  source_identity TEXT PRIMARY KEY, byte_offset INTEGER NOT NULL, session_id TEXT NOT NULL,
  parent_session_id TEXT, agent_name TEXT NOT NULL, repository TEXT NOT NULL,
  started_at_utc TEXT NOT NULL, current_model TEXT, reasoning_effort TEXT,
  context_window_tokens INTEGER, updated_at_utc TEXT NOT NULL
);
CREATE TABLE context_observations (
  event_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, agent_id TEXT,
  observed_at_utc TEXT NOT NULL, model TEXT, input_tokens INTEGER,
  context_window_tokens INTEGER, is_compaction INTEGER NOT NULL, record_bytes INTEGER
);
CREATE TABLE forecast_snapshots (
  provider TEXT NOT NULL, profile TEXT NOT NULL, kind TEXT NOT NULL,
  generated_at_utc TEXT NOT NULL, burn_rate_percent_per_hour REAL,
  estimated_exhaustion_at_utc TEXT, survives_until_reset INTEGER,
  sustainable_percent_per_hour REAL, confidence REAL NOT NULL,
  state TEXT NOT NULL DEFAULT 'Learning', burn_pressure REAL,
  projected_remaining_at_reset_percent REAL, trend TEXT,
  is_quantized_flat INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(provider, profile, kind, generated_at_utc)
);
CREATE TABLE ingestion_checkpoints (
  file_path TEXT PRIMARY KEY, last_byte_offset INTEGER NOT NULL,
  updated_at_utc TEXT NOT NULL, last_session_id TEXT, parser_version TEXT NOT NULL,
  source_identity TEXT
);
CREATE TABLE intelligence_schema (component TEXT PRIMARY KEY, version INTEGER NOT NULL);
CREATE TABLE observatory_schema (component TEXT PRIMARY KEY, version INTEGER NOT NULL);
CREATE TABLE quota_reset_events (
  event_id TEXT PRIMARY KEY, kind TEXT NOT NULL, provider TEXT NOT NULL,
  profile TEXT NOT NULL, detected_at_utc TEXT NOT NULL, effective_at_utc TEXT NOT NULL,
  before_used_percent REAL, after_used_percent REAL, previous_reset_at_utc TEXT,
  current_reset_at_utc TEXT, classification TEXT NOT NULL, confidence REAL NOT NULL,
  source TEXT NOT NULL, explanation TEXT NOT NULL
);
CREATE TABLE quota_snapshots (
  provider TEXT NOT NULL, profile TEXT NOT NULL, kind TEXT NOT NULL,
  captured_at_utc TEXT NOT NULL, used_percent REAL, window_minutes INTEGER,
  resets_at_utc TEXT, source TEXT NOT NULL,
  PRIMARY KEY(provider, profile, kind, captured_at_utc)
);
CREATE TABLE repositories (
  repository_id TEXT PRIMARY KEY, name TEXT NOT NULL, root_path TEXT,
  remote_url TEXT, first_seen_at_utc TEXT NOT NULL, last_seen_at_utc TEXT NOT NULL
);
CREATE TABLE reset_events (
  event_id TEXT PRIMARY KEY, kind TEXT NOT NULL, detected_at_utc TEXT NOT NULL,
  effective_at_utc TEXT NOT NULL, source TEXT NOT NULL
);
CREATE TABLE rollout_files (
  source_identity TEXT PRIMARY KEY, file_path TEXT NOT NULL, session_id TEXT,
  size_bytes INTEGER NOT NULL, last_seen_at_utc TEXT NOT NULL
);
CREATE TABLE rollout_records (
  source_record_id TEXT PRIMARY KEY, source_identity TEXT NOT NULL,
  file_path TEXT NOT NULL, session_id TEXT, event_class TEXT NOT NULL,
  record_bytes INTEGER NOT NULL, observed_at_utc TEXT NOT NULL
);
CREATE TABLE sessions (
  session_id TEXT PRIMARY KEY, thread_id TEXT, repository TEXT NOT NULL,
  started_at_utc TEXT NOT NULL, last_activity_at_utc TEXT, status TEXT NOT NULL
);
CREATE TABLE token_usage (
  id INTEGER PRIMARY KEY AUTOINCREMENT, provider TEXT NOT NULL, client TEXT NOT NULL,
  profile TEXT, observed_at_utc TEXT NOT NULL, model TEXT NOT NULL,
  session_id TEXT, thread_id TEXT, repository TEXT, agent_id TEXT,
  uncached_input_tokens INTEGER NOT NULL, cache_read_tokens INTEGER NOT NULL,
  cache_write_tokens INTEGER NOT NULL, non_reasoning_output_tokens INTEGER NOT NULL,
  reasoning_output_tokens INTEGER NOT NULL, reported_total_tokens INTEGER
);
CREATE TABLE usage_events (
  event_id TEXT PRIMARY KEY, session_id TEXT NOT NULL, timestamp_utc TEXT NOT NULL,
  event_type TEXT NOT NULL, summary TEXT NOT NULL, token_delta REAL
);
CREATE TABLE workspaces (
  workspace_id TEXT PRIMARY KEY, path TEXT NOT NULL, repository_id TEXT,
  first_seen_at_utc TEXT NOT NULL, last_seen_at_utc TEXT NOT NULL
);
```

### Repetition and write profile

- `rollout_records` is the largest table (34,562 rows), followed by `usage_events` (15,842), `quota_snapshots` (10,495), `context_observations` (5,254), and native token events (5,219).
- Primary-key duplicates were absent for rollout/native/context/quota rows. Six duplicate-looking `usage_events` groups existed (six excess rows), caused by repeated same-session/time/type/summary inserts; the event ID primary key still prevents exact source-event duplication.
- Native token events had 74 zero-delta rows (including 74 with zero reported total), consistent with out-of-order/duplicate/flat observations being retained for idempotence and audit.
- Rollout event classes were led by `item_completed` 12,173, `reasoning` 5,723, `token_count` 5,205, `custom_tool_call_output` 4,862, and `custom_tool_call` 4,862. Usage-event types included 5,219 `token_count`, 4,876 each of custom tool call/output, 206 each of function call/output, 127 `turn_context`, 101 `agent_message`, 91 `task_started`, 69 `task_complete`, and 36 `compaction`.
- Quota rows were 5,217 `codex-rollout:primary`, 5,216 `codex-rollout:secondary`, and 62 `codex-app-server:codex`.

The current implementation's high-volume writer batches `rollout_records` in groups of 128 (about 270 metadata transactions for this corpus), with a prepared insert and one write gate. Semantic persistence in `CodexSessionIngestionService.PersistParsedTelemetryAsync` still calls the Observatory store separately for sessions, agents, relationships, usage events, cumulative token state/events, each quota lane, and context. The store's generic `ExecuteAsync` opens a connection and executes an autocommit command per call; cumulative-token writes use their own transaction. Connection pooling reduces physical setup cost but does not remove command, journal, and commit overhead.

The highest-value next optimization is therefore:

1. use `state_5.sqlite` to select changed thread IDs and metadata;
2. open only those threads' rollout paths;
3. seek each TajsTokens checkpoint and parse complete new records;
4. batch content-free semantic mutations per bounded batch while preserving per-session cumulative-counter ordering;
5. commit the corresponding byte checkpoint only after semantic and metadata writes succeed.

Do not parallelize SQLite writers arbitrarily. Parse/read work can be bounded and parallel where ordering permits; one intentional writer per TajsTokens database remains the safe default.

## Architecture recommendation

### Safe to source from Codex SQLite

- `threads.id`, `rollout_path` as an ephemeral file locator, `created_at_ms`, `updated_at_ms`, `recency_at_ms`, `archived`/`archived_at`.
- `model_provider`, `model`, `reasoning_effort`, `tokens_used` (latest cumulative total, clearly labelled as state-reported).
- `thread_source`, `agent_nickname`, and a carefully reduced agent-role/path classification.
- `thread_spawn_edges` for parent/child topology, with ID-existence and cycle checks.
- Optional project names/root labels only after path sanitization; this snapshot has no linked `project_id` rows.
- Goal status/token/time summaries from `goals_1.sqlite`, without objectives.

Persist only normalized labels/identities in TajsTokens. Never copy absolute paths, titles, previews, first messages, sandbox JSON, or raw provider state payloads into the normal telemetry store.

### Still requires rollout JSONL

- disjoint input/cache-write/cache-read/non-reasoning/reasoning token deltas and their event timestamps;
- context-window input/window observations and compaction events;
- content-free activity/timeline event classes and per-record byte/storage metadata;
- embedded quota observations and source/event provenance;
- parser ownership, inherited-prefix exclusion, counter epochs/resets, and exact complete-record byte boundaries.

### Fallback-only or excluded sources

- `logs_2.sqlite.feedback_log_body`, `thread_history_1.sqlite.item_json`, and `memories_1.sqlite` raw memory/summary fields are content-bearing and should remain excluded unless an explicit redacted projection is later designed.
- `queue_1.sqlite.payload_json` is currently empty and should not be treated as telemetry.
- Codex state metadata is fallback/summary accounting when rollout events are absent; it is not a replacement for native disjoint accounting or provider-authoritative quota.

### Compatibility and correctness risks

- These are private Codex schemas. File suffixes, migration descriptions, columns, indexes, WAL behavior, and timestamp semantics may change without a stable public contract.
- `user_version` is 0; `_sqlx_migrations` is an implementation table, not a compatibility guarantee. Gate imports on a recognized table/column fingerprint and fail open when unknown.
- Read a consistent snapshot of WAL databases. Never copy only the main file while a `-wal` sidecar contains committed pages.
- Handle path rotation/replacement separately from stable thread identity. Keep TajsTokens' existing exact-byte checkpoint and source-identity rules.
- Treat `tokens_used` as a cumulative summary and reconcile it against rollout totals when available. Never sum repeated historical state values.
- State rows can be deleted/archived or updated for UI metadata without a new token event; maintain a bounded fallback scan and do not advance rollout checkpoints past unapplied data.

### Expected performance benefit

The supplied snapshot has 318 threads while the current TajsTokens database has only 42 normalized sessions and 34,562 normalized rollout records. An indexed state query can identify the small changed set on an idle refresh and avoid opening/stat'ing/parsing the other rollout files. In the best idle case this changes work from a walk of roughly 318 files to one SQLite query plus zero/one rollout opens; during active work it limits parsing to the changed threads. The exact speedup is workload- and filesystem-dependent and was not benchmarked here, but the reduction in file reads and semantic upserts should be material. A controlled live-turn benchmark remains user-owned.

## Proposed data flow

```text
Codex state_5.sqlite (read-only snapshot)
  -> schema/fingerprint check
  -> changed-thread query on updated_at_ms with overlap
  -> ephemeral rollout_path lookup + topology/metadata normalization
  -> only changed rollout JSONL files
  -> existing TajsTokens byte checkpoint
  -> bounded parse and ordered semantic batch
  -> one TajsTokens writer transaction/batch
  -> checkpoint after commit
```

This preserves the current privacy/evidence boundary: Codex SQLite supplies cheap discovery and cumulative summaries; rollout JSONL remains the source for event-level accounting and context/activity detail.
