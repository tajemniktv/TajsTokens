using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed class SqliteTelemetryRepository(string databasePath) : ITelemetryRepository, ISessionIngestionCheckpointStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS quota_snapshots (
                kind TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                used_tokens REAL NOT NULL,
                limit_tokens REAL NOT NULL,
                resets_at_utc TEXT NOT NULL,
                PRIMARY KEY(kind, captured_at_utc)
            );

            CREATE TABLE IF NOT EXISTS token_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at_utc TEXT NOT NULL,
                model TEXT NOT NULL,
                scope TEXT NOT NULL,
                input_tokens REAL NOT NULL,
                cached_input_tokens REAL NOT NULL,
                output_tokens REAL NOT NULL,
                reasoning_tokens REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                thread_id TEXT,
                repository TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                last_activity_at_utc TEXT,
                status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS agents (
                agent_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                name TEXT NOT NULL,
                state TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                model TEXT
            );

            CREATE TABLE IF NOT EXISTS agent_relationships (
                parent_agent_id TEXT NOT NULL,
                child_agent_id TEXT NOT NULL,
                linked_at_utc TEXT NOT NULL,
                PRIMARY KEY(parent_agent_id, child_agent_id)
            );

            CREATE TABLE IF NOT EXISTS usage_events (
                event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                summary TEXT NOT NULL,
                token_delta REAL
            );

            CREATE TABLE IF NOT EXISTS reset_events (
                event_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                detected_at_utc TEXT NOT NULL,
                effective_at_utc TEXT NOT NULL,
                source TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS announcements (
                announcement_id TEXT PRIMARY KEY,
                published_at_utc TEXT NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                link TEXT
            );

            CREATE TABLE IF NOT EXISTS ingestion_checkpoints (
                file_path TEXT PRIMARY KEY,
                last_byte_offset INTEGER NOT NULL,
                updated_at_utc TEXT NOT NULL,
                last_session_id TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_quota_snapshots_captured ON quota_snapshots(captured_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_token_usage_observed ON token_usage(observed_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_usage_events_time ON usage_events(timestamp_utc DESC);
        """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            """
            INSERT INTO quota_snapshots(kind, captured_at_utc, used_tokens, limit_tokens, resets_at_utc)
            VALUES($kind, $captured, $used, $limit, $resets)
            ON CONFLICT(kind, captured_at_utc) DO UPDATE SET
              used_tokens = excluded.used_tokens,
              limit_tokens = excluded.limit_tokens,
              resets_at_utc = excluded.resets_at_utc;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$kind", snapshot.Kind.ToString());
                cmd.Parameters.AddWithValue("$captured", snapshot.CapturedAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$used", snapshot.UsedTokens);
                cmd.Parameters.AddWithValue("$limit", snapshot.LimitTokens);
                cmd.Parameters.AddWithValue("$resets", snapshot.ResetsAtUtc.UtcDateTime);
            },
            cancellationToken);
    }

    public async Task AddTokenUsageAsync(TokenUsage usage, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            """
            INSERT INTO token_usage(observed_at_utc, model, scope, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens)
            VALUES($observed, $model, $scope, $input, $cached, $output, $reasoning);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$observed", usage.ObservedAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$model", usage.Model);
                cmd.Parameters.AddWithValue("$scope", usage.Scope);
                cmd.Parameters.AddWithValue("$input", usage.Breakdown.Input);
                cmd.Parameters.AddWithValue("$cached", usage.Breakdown.CachedInput);
                cmd.Parameters.AddWithValue("$output", usage.Breakdown.Output);
                cmd.Parameters.AddWithValue("$reasoning", usage.Breakdown.Reasoning);
            },
            cancellationToken);
    }

    public Task AddSessionAsync(CodexSession session, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT INTO sessions(session_id, thread_id, repository, started_at_utc, last_activity_at_utc, status)
            VALUES($id, $thread, $repo, $start, $last, $status)
            ON CONFLICT(session_id) DO UPDATE SET
              thread_id = excluded.thread_id,
              repository = excluded.repository,
              last_activity_at_utc = excluded.last_activity_at_utc,
              status = excluded.status;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", session.SessionId);
                cmd.Parameters.AddWithValue("$thread", (object?)session.ThreadId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$repo", session.Repository);
                cmd.Parameters.AddWithValue("$start", session.StartedAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$last", session.LastActivityAtUtc?.UtcDateTime ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$status", session.Status);
            },
            cancellationToken);

    public Task AddOrUpdateAgentAsync(Agent agent, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT INTO agents(agent_id, session_id, name, state, last_seen_utc, model)
            VALUES($id, $sessionId, $name, $state, $lastSeen, $model)
            ON CONFLICT(agent_id) DO UPDATE SET
              session_id = excluded.session_id,
              name = excluded.name,
              state = excluded.state,
              last_seen_utc = excluded.last_seen_utc,
              model = excluded.model;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", agent.AgentId);
                cmd.Parameters.AddWithValue("$sessionId", agent.SessionId);
                cmd.Parameters.AddWithValue("$name", agent.Name);
                cmd.Parameters.AddWithValue("$state", agent.State.ToString());
                cmd.Parameters.AddWithValue("$lastSeen", agent.LastSeenUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$model", (object?)agent.Model ?? DBNull.Value);
            },
            cancellationToken);

    public Task AddAgentRelationshipAsync(AgentRelationship relationship, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT OR REPLACE INTO agent_relationships(parent_agent_id, child_agent_id, linked_at_utc)
            VALUES($parent, $child, $linked);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$parent", relationship.ParentAgentId);
                cmd.Parameters.AddWithValue("$child", relationship.ChildAgentId);
                cmd.Parameters.AddWithValue("$linked", relationship.LinkedAtUtc.UtcDateTime);
            },
            cancellationToken);

    public Task AddUsageEventAsync(UsageEvent usageEvent, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT OR REPLACE INTO usage_events(event_id, session_id, timestamp_utc, event_type, summary, token_delta)
            VALUES($id, $session, $time, $type, $summary, $delta);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", usageEvent.EventId);
                cmd.Parameters.AddWithValue("$session", usageEvent.SessionId);
                cmd.Parameters.AddWithValue("$time", usageEvent.TimestampUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$type", usageEvent.EventType);
                cmd.Parameters.AddWithValue("$summary", usageEvent.Summary);
                cmd.Parameters.AddWithValue("$delta", usageEvent.TokenDelta ?? (object)DBNull.Value);
            },
            cancellationToken);

    public Task AddResetEventAsync(ResetEvent resetEvent, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT OR REPLACE INTO reset_events(event_id, kind, detected_at_utc, effective_at_utc, source)
            VALUES($id, $kind, $detected, $effective, $source);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", resetEvent.EventId);
                cmd.Parameters.AddWithValue("$kind", resetEvent.Kind.ToString());
                cmd.Parameters.AddWithValue("$detected", resetEvent.DetectedAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$effective", resetEvent.EffectiveAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$source", resetEvent.Source);
            },
            cancellationToken);

    public Task AddAnnouncementAsync(Announcement announcement, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT OR REPLACE INTO announcements(announcement_id, published_at_utc, source, title, body, link)
            VALUES($id, $published, $source, $title, $body, $link);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", announcement.AnnouncementId);
                cmd.Parameters.AddWithValue("$published", announcement.PublishedAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$source", announcement.Source);
                cmd.Parameters.AddWithValue("$title", announcement.Title);
                cmd.Parameters.AddWithValue("$body", announcement.Body);
                cmd.Parameters.AddWithValue("$link", announcement.Link?.ToString() ?? (object)DBNull.Value);
            },
            cancellationToken);

    public async Task<IReadOnlyList<QuotaSnapshot>> GetRecentQuotaSnapshotsAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT kind, captured_at_utc, used_tokens, limit_tokens, resets_at_utc
            FROM quota_snapshots
            ORDER BY captured_at_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", take);

        var results = new List<QuotaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new QuotaSnapshot(
                Enum.Parse<QuotaWindowKind>(reader.GetString(0)),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetDouble(2),
                reader.GetDouble(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return results;
    }

    public async Task<FileIngestionCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_path, last_byte_offset, updated_at_utc, last_session_id
            FROM ingestion_checkpoints
            WHERE file_path = $path;
            """;
        command.Parameters.AddWithValue("$path", filePath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FileIngestionCheckpoint(
            reader.GetString(0),
            reader.GetInt64(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public Task SaveCheckpointAsync(FileIngestionCheckpoint checkpoint, CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync(
            """
            INSERT INTO ingestion_checkpoints(file_path, last_byte_offset, updated_at_utc, last_session_id)
            VALUES($path, $offset, $updated, $session)
            ON CONFLICT(file_path) DO UPDATE SET
              last_byte_offset = excluded.last_byte_offset,
              updated_at_utc = excluded.updated_at_utc,
              last_session_id = excluded.last_session_id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$path", checkpoint.FilePath);
                cmd.Parameters.AddWithValue("$offset", checkpoint.LastByteOffset);
                cmd.Parameters.AddWithValue("$updated", checkpoint.UpdatedAtUtc.UtcDateTime);
                cmd.Parameters.AddWithValue("$session", checkpoint.LastSessionId ?? (object)DBNull.Value);
            },
            cancellationToken);

    private async Task ExecuteNonQueryAsync(string sql, Action<SqliteCommand> configure, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
