# Sanitized Codex rollout fixtures

These fixtures are synthetic structural regressions derived from rollout shapes observed during Phase 3 planning. They contain no real prompt text, reasoning text, tool output, source code, credentials, account identifiers, or personal paths.

Covered shapes:

- `root-counter-reset.jsonl`: repeated cumulative snapshots, a cumulative counter reset/epoch, 258,400-token context telemetry, near-full context, compaction, post-compaction context, embedded 5h/weekly quota snapshots, and completion.
- `child-inherited-prefix.jsonl`: copied parent prefix followed by the owning subagent `session_meta`, no `subagent_history_start_ordinal`, parent relationship metadata, child-only token accounting, embedded quota telemetry, and completion.

Tests generate malformed trailing records, file replacement, and giant filler records dynamically so those boundary cases remain deterministic without storing large content-bearing payloads in the repository.

The raw Aug 30-31 personal rollout corpus must never be committed here.
