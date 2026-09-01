using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Infrastructure.Persistence;

internal interface ICodexSemanticBatchWriter
{
    Task WriteBatchAsync(IReadOnlyList<ParsedRolloutRecord> records, CancellationToken cancellationToken);
}

/// <summary>
/// Persists the high-volume, straightforward Codex semantic projections in one SQLite transaction.
/// Cumulative token observations retain the existing store's ordered counter-epoch logic and are
/// applied sequentially after the projection transaction. If a later token write fails, the source
/// checkpoint is not advanced; replay is safe because every projection upsert is idempotent.
/// </summary>
internal sealed class SqliteCodexSemanticBatchWriter(
    string databasePath,
    ICodexObservatoryStore observatoryStore) : ICodexSemanticBatchWriter
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    private readonly ICodexObservatoryStore _observatoryStore = observatoryStore;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task WriteBatchAsync(
        IReadOnlyList<ParsedRolloutRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await _observatoryStore.InitializeAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await WriteProjectionBatchAsync(connection, transaction, records, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Counter observations deliberately remain ordered and use the established replay/reset
            // implementation. This still removes the much larger session/agent/activity/quota/context
            // autocommit fan-out while keeping the riskiest accounting logic single-sourced.
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.TokenObservation is not null)
                {
                    await _observatoryStore.ApplyCumulativeTokenObservationAsync(
                        record.TokenObservation,
                        cancellationToken);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task WriteProjectionBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ParsedRolloutRecord> records,
        CancellationToken cancellationToken)
    {
        using var session = BuildSessionCommand(connection, transaction);
        using var agent = BuildAgentCommand(connection, transaction);
        using var relationship = BuildRelationshipCommand(connection, transaction);
        using var usage = BuildUsageCommand(connection, transaction);
        using var quota = BuildQuotaCommand(connection, transaction);
        using var context = BuildContextCommand(connection, transaction);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.Session is not null)
            {
                Set(session, "$id", record.Session.SessionId);
                Set(session, "$thread", DbValue(record.Session.ThreadId));
                Set(session, "$repo", SafeRepositoryLabel(record.Session.Repository));
                Set(session, "$start", SerializeUtc(record.Session.StartedAtUtc));
                Set(session, "$last", record.Session.LastActivityAtUtc is null
                    ? DBNull.Value
                    : SerializeUtc(record.Session.LastActivityAtUtc.Value));
                Set(session, "$status", record.Session.Status);
                await session.ExecuteNonQueryAsync(cancellationToken);
            }

            if (record.Agent is not null)
            {
                Set(agent, "$id", record.Agent.AgentId);
                Set(agent, "$session", record.Agent.SessionId);
                Set(agent, "$name", record.Agent.Name);
                Set(agent, "$state", record.Agent.State.ToString());
                Set(agent, "$lastSeen", SerializeUtc(record.Agent.LastSeenUtc));
                Set(agent, "$model", DbValue(record.Agent.Model));
                await agent.ExecuteNonQueryAsync(cancellationToken);
            }

            if (record.Relationship is not null)
            {
                Set(relationship, "$parent", record.Relationship.ParentAgentId);
                Set(relationship, "$child", record.Relationship.ChildAgentId);
                Set(relationship, "$linked", SerializeUtc(record.Relationship.LinkedAtUtc));
                await relationship.ExecuteNonQueryAsync(cancellationToken);
            }

            if (record.UsageEvent is not null)
            {
                Set(usage, "$id", record.UsageEvent.EventId);
                Set(usage, "$session", record.UsageEvent.SessionId);
                Set(usage, "$time", SerializeUtc(record.UsageEvent.TimestampUtc));
                Set(usage, "$type", record.UsageEvent.EventType);
                Set(usage, "$summary", record.UsageEvent.Summary);
                Set(usage, "$delta", DbValue(record.UsageEvent.TokenDelta));
                await usage.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var snapshot in record.QuotaSnapshots)
            {
                Set(quota, "$provider", snapshot.Provider);
                Set(quota, "$profile", snapshot.Profile);
                Set(quota, "$kind", snapshot.Kind.ToString());
                Set(quota, "$captured", SerializeUtc(snapshot.CapturedAtUtc));
                Set(quota, "$used", DbValue(snapshot.UsedPercent));
                Set(quota, "$window", DbValue(snapshot.WindowMinutes));
                Set(quota, "$resets", snapshot.ResetsAtUtc is null
                    ? DBNull.Value
                    : SerializeUtc(snapshot.ResetsAtUtc.Value));
                Set(quota, "$source", snapshot.Source);
                await quota.ExecuteNonQueryAsync(cancellationToken);
            }

            if (record.ContextObservation is not null)
            {
                Set(context, "$event", record.ContextObservation.EventId);
                Set(context, "$session", record.ContextObservation.SessionId);
                Set(context, "$agent", DbValue(record.ContextObservation.AgentId));
                Set(context, "$observed", SerializeUtc(record.ContextObservation.ObservedAtUtc));
                Set(context, "$model", DbValue(record.ContextObservation.Model));
                Set(context, "$input", DbValue(record.ContextObservation.InputTokens));
                Set(context, "$window", DbValue(record.ContextObservation.ContextWindowTokens));
                Set(context, "$compaction", record.ContextObservation.IsCompaction ? 1 : 0);
                Set(context, "$bytes", DbValue(record.ContextObservation.RecordBytes));
                await context.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static SqliteCommand BuildSessionCommand(SqliteConnection connection, SqliteTransaction transaction) =>
        BuildCommand(connection, transaction, """
            INSERT INTO sessions(session_id, thread_id, repository, started_at_utc, last_activity_at_utc, status)
            VALUES($id, $thread, $repo, $start, $last, $status)
            ON CONFLICT(session_id) DO UPDATE SET
              thread_id = COALESCE(excluded.thread_id, sessions.thread_id),
              repository = CASE WHEN excluded.repository = '(unknown)' THEN sessions.repository ELSE excluded.repository END,
              started_at_utc = MIN(sessions.started_at_utc, excluded.started_at_utc),
              last_activity_at_utc = CASE
                  WHEN sessions.last_activity_at_utc IS NULL THEN excluded.last_activity_at_utc
                  WHEN excluded.last_activity_at_utc IS NULL THEN sessions.last_activity_at_utc
                  ELSE MAX(sessions.last_activity_at_utc, excluded.last_activity_at_utc)
              END,
              status = excluded.status;
            """, "$id", "$thread", "$repo", "$start", "$last", "$status");

    private static SqliteCommand BuildAgentCommand(SqliteConnection connection, SqliteTransaction transaction) =>
        BuildCommand(connection, transaction, """
            INSERT INTO agents(agent_id, session_id, name, state, last_seen_utc, model)
            VALUES($id, $session, $name, $state, $lastSeen, $model)
            ON CONFLICT(agent_id) DO UPDATE SET
              session_id = excluded.session_id,
              name = CASE
                  WHEN excluded.name IN ('', 'Codex session', 'Root agent', 'Subagent')
                       AND agents.name NOT IN ('', 'Codex session', 'Root agent', 'Subagent')
                  THEN agents.name
                  ELSE excluded.name
              END,
              state = excluded.state,
              last_seen_utc = MAX(agents.last_seen_utc, excluded.last_seen_utc),
              model = COALESCE(excluded.model, agents.model);
            """, "$id", "$session", "$name", "$state", "$lastSeen", "$model");

    private static SqliteCommand BuildRelationshipCommand(SqliteConnection connection, SqliteTransaction transaction) =>
        BuildCommand(connection, transaction, """
            INSERT INTO agent_relationships(parent_agent_id, child_agent_id, linked_at_utc)
            VALUES($parent, $child, $linked)
            ON CONFLICT(parent_agent_id, child_agent_id) DO UPDATE SET
              linked_at_utc = MIN(agent_relationships.linked_at_utc, excluded.linked_at_utc);
            """, "$parent", "$child", "$linked");

    private static SqliteCommand BuildUsageCommand(SqliteConnection connection, SqliteTransaction transaction) =>
        BuildCommand(connection, transaction, """
            INSERT INTO usage_events(event_id, session_id, timestamp_utc, event_type, summary, token_delta)
            VALUES($id, $session, $time, $type, $summary, $delta)
            ON CONFLICT(event_id) DO NOTHING;
            """, "$id", "$session", "$time", "$type", "$summary", "$delta");

    private static SqliteCommand BuildQuotaCommand(SqliteConnection connection, SqliteTransaction transaction) =>
        BuildCommand(connection, transaction, """
            INSERT INTO quota_snapshots(provider, profile, kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, source)
            VALUES($provider, $profile, $kind, $captured, $used, $window, $resets, $source)
            ON CONFLICT(provider, profile, kind, captured_at_utc) DO UPDATE SET
              used_percent = excluded.used_percent,
              window_minutes = excluded.window_minutes,
              resets_at_utc = excluded.resets_at_utc,
              source = excluded.source;
            """, "$provider", "$profile", "$kind", "$captured", "$used", "$window", "$resets", "$source");

    private static SqliteCommand BuildContextCommand(SqliteConnection connection, SqliteTransaction transaction) =>
        BuildCommand(connection, transaction, """
            INSERT INTO context_observations(
                event_id, session_id, agent_id, observed_at_utc, model, input_tokens,
                context_window_tokens, is_compaction, record_bytes)
            VALUES($event, $session, $agent, $observed, $model, $input, $window, $compaction, $bytes)
            ON CONFLICT(event_id) DO NOTHING;
            """, "$event", "$session", "$agent", "$observed", "$model", "$input", "$window", "$compaction", "$bytes");

    private static SqliteCommand BuildCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params string[] parameterNames)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameterName in parameterNames)
        {
            command.Parameters.Add(new SqliteParameter(parameterName, DBNull.Value));
        }
        command.Prepare();
        return command;
    }

    private static void Set(SqliteCommand command, string parameterName, object value) =>
        command.Parameters[parameterName].Value = value;

    private static string SafeRepositoryLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "(unknown)", StringComparison.Ordinal))
        {
            return "(unknown)";
        }

        var normalized = value.Replace('\\', '/').Trim().TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        var label = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        if (label.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            label = label[..^4];
        }

        label = label.Trim();
        if (label.Length == 0)
        {
            return "(unknown)";
        }

        return label.Length <= 160 ? label : label[..160];
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string SerializeUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
