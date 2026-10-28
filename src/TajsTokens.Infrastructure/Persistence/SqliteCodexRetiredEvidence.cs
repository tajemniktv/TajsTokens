// Taj's Tokens | SqliteCodexRetiredEvidence.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>
/// Preserves the numeric facts available before a source generation is replaced. Payloads are
/// compile-time allowlists of existing columns, never source JSON or arbitrary row serialization.
/// These are historical interpretations, not another input to the active accounting ledger.
/// </summary>
internal static class SqliteCodexRetiredEvidence
{
    public const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS codex_source_retirements (
            retirement_id TEXT PRIMARY KEY,
            source_file TEXT NOT NULL,
            replacement_source_identity TEXT NOT NULL,
            retired_at_utc TEXT NOT NULL,
            reason TEXT NOT NULL,
            contract_version INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS codex_retired_source_generations (
            retirement_id TEXT NOT NULL REFERENCES codex_source_retirements(retirement_id),
            source_identity TEXT NOT NULL,
            session_id TEXT,
            physical_size_bytes INTEGER NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            PRIMARY KEY(retirement_id, source_identity)
        );
        CREATE INDEX IF NOT EXISTS idx_retired_generations_identity
            ON codex_retired_source_generations(source_identity);
        CREATE TABLE IF NOT EXISTS codex_retired_numeric_observations (
            retirement_id TEXT NOT NULL REFERENCES codex_source_retirements(retirement_id),
            observation_kind TEXT NOT NULL CHECK(observation_kind IN
                ('native-token', 'counter-state', 'quota', 'context', 'workload', 'usage-timeline')),
            source_key TEXT NOT NULL,
            source_identity TEXT,
            evidence_json TEXT NOT NULL,
            PRIMARY KEY(retirement_id, observation_kind, source_key)
        );
        CREATE INDEX IF NOT EXISTS idx_retired_numeric_identity
            ON codex_retired_numeric_observations(source_identity, observation_kind);
        """;

    public static async Task PreserveAsync(SqliteConnection connection, SqliteTransaction transaction,
        string safeFileLabel, string replacementIdentity, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO codex_source_retirements(retirement_id, source_file,
                replacement_source_identity, retired_at_utc, reason, contract_version)
            VALUES($retirement, $file, $replacement, $retired, 'superseded-source-identity', 1);
            INSERT INTO codex_retired_source_generations(retirement_id, source_identity,
                session_id, physical_size_bytes, last_seen_at_utc)
            SELECT $retirement, source_identity, session_id, size_bytes, last_seen_at_utc
            FROM rollout_files WHERE file_path = $file AND source_identity <> $replacement;

            INSERT INTO codex_retired_numeric_observations
            SELECT $retirement, 'native-token', n.source_event_id,
                (SELECT r.source_identity FROM rollout_records r WHERE r.source_record_id = n.source_event_id),
                json_object('SourceEventId', n.source_event_id, 'SourceFile', n.source_file,
                    'SessionId', n.session_id, 'AgentId', n.agent_id,
                    'ObservedAtUtc', n.observed_at_utc, 'CapturedAtUtc', n.captured_at_utc,
                    'SourceEventAtUtc', n.source_event_at_utc, 'SourceTimestampQuality', n.source_timestamp_quality,
                    'Model', n.model, 'ReasoningEffort', n.reasoning_effort, 'CounterEpoch', n.counter_epoch,
                    'UncachedInputTokens', n.uncached_input_tokens, 'CacheReadTokens', n.cache_read_tokens,
                    'CacheWriteTokens', n.cache_write_tokens, 'NonReasoningOutputTokens', n.non_reasoning_output_tokens,
                    'ReasoningOutputTokens', n.reasoning_output_tokens, 'ReportedTotalTokens', n.reported_total_tokens)
            FROM codex_native_token_events n WHERE n.source_file = $file OR n.source_event_id IN (
                SELECT source_record_id FROM rollout_records WHERE source_identity IN (
                    SELECT source_identity FROM codex_retired_source_generations WHERE retirement_id = $retirement));

            INSERT INTO codex_retired_numeric_observations
            SELECT $retirement, 'counter-state', s.session_id,
                (SELECT r.source_identity FROM rollout_records r WHERE r.source_record_id = s.last_source_event_id),
                json_object('SessionId', s.session_id, 'SourceFile', s.source_file,
                    'CounterEpoch', s.counter_epoch, 'InputTokens', s.input_tokens,
                    'CachedInputTokens', s.cached_input_tokens, 'CacheWriteInputTokens', s.cache_write_input_tokens,
                    'OutputTokens', s.output_tokens, 'ReasoningOutputTokens', s.reasoning_output_tokens,
                    'TotalTokens', s.total_tokens, 'LastSourceEventId', s.last_source_event_id,
                    'LastObservedAtUtc', s.last_observed_at_utc, 'HasCumulativeBaseline', s.has_cumulative_baseline,
                    'LastSourceEventAtUtc', s.last_source_event_at_utc)
            FROM codex_counter_state s WHERE s.source_file = $file OR s.last_source_event_id IN (
                SELECT source_record_id FROM rollout_records WHERE source_identity IN (
                    SELECT source_identity FROM codex_retired_source_generations WHERE retirement_id = $retirement));

            INSERT INTO codex_retired_numeric_observations
            SELECT $retirement, 'quota', json_array(q.provider, q.profile, q.kind, q.captured_at_utc,
                q.source, q.account_key, q.observation_id), q.source_identity,
                json_object('Provider', q.provider, 'Profile', q.profile, 'Kind', q.kind,
                    'CapturedAtUtc', q.captured_at_utc, 'UsedPercent', q.used_percent,
                    'WindowMinutes', q.window_minutes, 'ResetsAtUtc', q.resets_at_utc,
                    'Source', q.source, 'AccountKey', q.account_key, 'ObservationId', q.observation_id,
                    'SourceIdentity', q.source_identity, 'SessionId', q.session_id,
                    'LimitId', q.limit_id, 'PlanType', q.plan_type, 'Lane', q.lane,
                    'CollectedAtUtc', q.collected_at_utc, 'HasSourceTimestamp', q.has_source_timestamp)
            FROM quota_snapshots q WHERE q.source_identity IN (
                SELECT source_identity FROM codex_retired_source_generations WHERE retirement_id = $retirement);

            INSERT INTO codex_retired_numeric_observations
            SELECT $retirement, 'context', c.event_id, r.source_identity,
                json_object('EventId', c.event_id, 'SessionId', c.session_id, 'AgentId', c.agent_id,
                    'ObservedAtUtc', c.observed_at_utc, 'CapturedAtUtc', c.captured_at_utc,
                    'SourceEventAtUtc', c.source_event_at_utc, 'SourceTimestampQuality', c.source_timestamp_quality,
                    'Model', c.model, 'InputTokens', c.input_tokens, 'ContextWindowTokens', c.context_window_tokens,
                    'IsCompaction', c.is_compaction, 'RecordBytes', c.record_bytes)
            FROM rollout_records r JOIN context_observations c ON c.event_id = r.source_record_id || ':ctx'
            WHERE r.source_identity IN (
                SELECT source_identity FROM codex_retired_source_generations WHERE retirement_id = $retirement);

            INSERT INTO codex_retired_numeric_observations
            SELECT $retirement, 'workload', w.source_record_id, w.source_identity,
                json_object('SourceRecordId', w.source_record_id, 'SourceIdentity', w.source_identity,
                    'SourceFile', w.source_file, 'StartByteOffset', w.start_byte_offset, 'EndByteOffset', w.end_byte_offset,
                    'SessionId', w.session_id, 'EventType', w.event_type, 'ObservedAtUtc', w.observed_at_utc,
                    'CapturedAtUtc', w.captured_at_utc, 'TurnId', w.turn_id, 'RootTurnId', w.root_turn_id,
                    'ParentThreadId', w.parent_thread_id, 'Model', w.model, 'ReasoningEffort', w.reasoning_effort,
                    'ContextWindowTokens', w.context_window_tokens, 'ContractVersion', w.contract_version,
                    'StartedAtUnixSeconds', w.started_at_unix_seconds, 'CompletedAtUnixSeconds', w.completed_at_unix_seconds,
                    'DurationMilliseconds', w.duration_ms, 'TimeToFirstTokenMilliseconds', w.time_to_first_token_ms,
                    'SessionSourceKind', w.session_source_kind, 'ServiceTier', w.service_tier)
            FROM codex_workload_observations w WHERE w.source_identity IN (
                SELECT source_identity FROM codex_retired_source_generations WHERE retirement_id = $retirement);

            INSERT INTO codex_retired_numeric_observations
            SELECT $retirement, 'usage-timeline', u.event_id, r.source_identity,
                json_object('EventId', u.event_id, 'SessionId', u.session_id, 'TimestampUtc', u.timestamp_utc,
                    'EventType', u.event_type, 'TokenDelta', u.token_delta)
            FROM rollout_records r JOIN usage_events u ON u.event_id IN (r.source_record_id, r.source_record_id || ':token')
            WHERE r.source_identity IN (
                SELECT source_identity FROM codex_retired_source_generations WHERE retirement_id = $retirement);
            """;
        command.Parameters.AddWithValue("$retirement", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$file", safeFileLabel);
        command.Parameters.AddWithValue("$replacement", replacementIdentity);
        command.Parameters.AddWithValue("$retired", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
