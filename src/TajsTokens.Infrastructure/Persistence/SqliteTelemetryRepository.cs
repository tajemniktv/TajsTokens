using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed class SqliteTelemetryRepository(string databasePath) : ITelemetryRepository, ISessionIngestionCheckpointStore
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"Telemetry database schema {version} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (version == 0)
        {
            // PR #1 previously created incompatible pre-release tables and wrote only synthetic
            // bootstrap data. Recreate them atomically, then version all future migrations.
            await ExecuteMigrationAsync(connection, """
                DROP TABLE IF EXISTS quota_snapshots;
                DROP TABLE IF EXISTS token_usage;
                DROP TABLE IF EXISTS ingestion_checkpoints;

                CREATE TABLE quota_snapshots (
                    provider TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    captured_at_utc TEXT NOT NULL,
                    used_percent REAL,
                    window_minutes INTEGER,
                    resets_at_utc TEXT,
                    source TEXT NOT NULL,
                    PRIMARY KEY(provider, profile, kind, captured_at_utc)
                );

                CREATE TABLE token_usage (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    provider TEXT NOT NULL,
                    client TEXT NOT NULL,
                    profile TEXT,
                    observed_at_utc TEXT NOT NULL,
                    model TEXT NOT NULL,
                    session_id TEXT,
                    thread_id TEXT,
                    repository TEXT,
                    agent_id TEXT,
                    uncached_input_tokens INTEGER NOT NULL,
                    cache_read_tokens INTEGER NOT NULL,
                    cache_write_tokens INTEGER NOT NULL,
                    non_reasoning_output_tokens INTEGER NOT NULL,
                    reasoning_output_tokens INTEGER NOT NULL,
                    reported_total_tokens INTEGER
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

                CREATE TABLE ingestion_checkpoints (
                    file_path TEXT PRIMARY KEY,
                    last_byte_offset INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    last_session_id TEXT,
                    parser_version TEXT NOT NULL,
                    source_identity TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_quota_snapshots_lookup
                    ON quota_snapshots(provider, profile, kind, captured_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_token_usage_observed ON token_usage(observed_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_usage_events_time ON usage_events(timestamp_utc DESC);

                PRAGMA user_version = 2;
                """, cancellationToken);
            return;
        }

        if (version == 1)
        {
            await ExecuteMigrationAsync(connection, """
                ALTER TABLE ingestion_checkpoints ADD COLUMN source_identity TEXT;
                PRAGMA user_version = 2;
                """, cancellationToken);
        }
    }

    public Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO quota_snapshots(provider, profile, kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, source)
            VALUES($provider, $profile, $kind, $captured, $used, $window, $resets, $source)
            ON CONFLICT(provider, profile, kind, captured_at_utc) DO UPDATE SET
              used_percent = excluded.used_percent,
              window_minutes = excluded.window_minutes,
              resets_at_utc = excluded.resets_at_utc,
              source = excluded.source;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$provider", snapshot.Provider);
                cmd.Parameters.AddWithValue("$profile", snapshot.Profile);
                cmd.Parameters.AddWithValue("$kind", snapshot.Kind.ToString());
                cmd.Parameters.AddWithValue("$captured", SerializeUtc(snapshot.CapturedAtUtc));
                cmd.Parameters.AddWithValue("$used", DbValue(snapshot.UsedPercent));
                cmd.Parameters.AddWithValue("$window", DbValue(snapshot.WindowMinutes));
                cmd.Parameters.AddWithValue("$resets", snapshot.ResetsAtUtc is null ? DBNull.Value : SerializeUtc(snapshot.ResetsAtUtc.Value));
                cmd.Parameters.AddWithValue("$source", snapshot.Source);
            },
            cancellationToken);

    public Task AddTokenUsageAsync(TokenUsage usage, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO token_usage(
                provider, client, profile, observed_at_utc, model, session_id, thread_id, repository, agent_id,
                uncached_input_tokens, cache_read_tokens, cache_write_tokens, non_reasoning_output_tokens,
                reasoning_output_tokens, reported_total_tokens)
            VALUES(
                $provider, $client, $profile, $observed, $model, $session, $thread, $repo, $agent,
                $uncachedInput, $cacheRead, $cacheWrite, $nonReasoningOutput, $reasoningOutput, $reportedTotal);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$provider", usage.Provider);
                cmd.Parameters.AddWithValue("$client", usage.Client);
                cmd.Parameters.AddWithValue("$profile", DbValue(usage.Profile));
                cmd.Parameters.AddWithValue("$observed", SerializeUtc(usage.ObservedAtUtc));
                cmd.Parameters.AddWithValue("$model", usage.Model);
                cmd.Parameters.AddWithValue("$session", DbValue(usage.SessionId));
                cmd.Parameters.AddWithValue("$thread", DbValue(usage.ThreadId));
                cmd.Parameters.AddWithValue("$repo", DbValue(usage.Repository));
                cmd.Parameters.AddWithValue("$agent", DbValue(usage.AgentId));
                cmd.Parameters.AddWithValue("$uncachedInput", usage.Breakdown.UncachedInput);
                cmd.Parameters.AddWithValue("$cacheRead", usage.Breakdown.CacheRead);
                cmd.Parameters.AddWithValue("$cacheWrite", usage.Breakdown.CacheWrite);
                cmd.Parameters.AddWithValue("$nonReasoningOutput", usage.Breakdown.NonReasoningOutput);
                cmd.Parameters.AddWithValue("$reasoningOutput", usage.Breakdown.ReasoningOutput);
                cmd.Parameters.AddWithValue("$reportedTotal", DbValue(usage.Breakdown.ReportedTotal));
            },
            cancellationToken);

    public Task AddSessionAsync(CodexSession session, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
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
                cmd.Parameters.AddWithValue("$thread", DbValue(session.ThreadId));
                cmd.Parameters.AddWithValue("$repo", session.Repository);
                cmd.Parameters.AddWithValue("$start", SerializeUtc(session.StartedAtUtc));
                cmd.Parameters.AddWithValue("$last", session.LastActivityAtUtc is null ? DBNull.Value : SerializeUtc(session.LastActivityAtUtc.Value));
                cmd.Parameters.AddWithValue("$status", session.Status);
            },
            cancellationToken);

    public Task AddOrUpdateAgentAsync(Agent agent, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
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
                cmd.Parameters.AddWithValue("$lastSeen", SerializeUtc(agent.LastSeenUtc));
                cmd.Parameters.AddWithValue("$model", DbValue(agent.Model));
            },
            cancellationToken);

    public Task AddAgentRelationshipAsync(AgentRelationship relationship, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT OR REPLACE INTO agent_relationships(parent_agent_id, child_agent_id, linked_at_utc)
            VALUES($parent, $child, $linked);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$parent", relationship.ParentAgentId);
                cmd.Parameters.AddWithValue("$child", relationship.ChildAgentId);
                cmd.Parameters.AddWithValue("$linked", SerializeUtc(relationship.LinkedAtUtc));
            },
            cancellationToken);

    public Task AddUsageEventAsync(UsageEvent usageEvent, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT OR REPLACE INTO usage_events(event_id, session_id, timestamp_utc, event_type, summary, token_delta)
            VALUES($id, $session, $time, $type, $summary, $delta);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", usageEvent.EventId);
                cmd.Parameters.AddWithValue("$session", usageEvent.SessionId);
                cmd.Parameters.AddWithValue("$time", SerializeUtc(usageEvent.TimestampUtc));
                cmd.Parameters.AddWithValue("$type", usageEvent.EventType);
                cmd.Parameters.AddWithValue("$summary", usageEvent.Summary);
                cmd.Parameters.AddWithValue("$delta", DbValue(usageEvent.TokenDelta));
            },
            cancellationToken);

    public Task AddResetEventAsync(ResetEvent resetEvent, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT OR REPLACE INTO reset_events(event_id, kind, detected_at_utc, effective_at_utc, source)
            VALUES($id, $kind, $detected, $effective, $source);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", resetEvent.EventId);
                cmd.Parameters.AddWithValue("$kind", resetEvent.Kind.ToString());
                cmd.Parameters.AddWithValue("$detected", SerializeUtc(resetEvent.DetectedAtUtc));
                cmd.Parameters.AddWithValue("$effective", SerializeUtc(resetEvent.EffectiveAtUtc));
                cmd.Parameters.AddWithValue("$source", resetEvent.Source);
            },
            cancellationToken);

    public Task AddAnnouncementAsync(Announcement announcement, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT OR REPLACE INTO announcements(announcement_id, published_at_utc, source, title, body, link)
            VALUES($id, $published, $source, $title, $body, $link);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", announcement.AnnouncementId);
                cmd.Parameters.AddWithValue("$published", SerializeUtc(announcement.PublishedAtUtc));
                cmd.Parameters.AddWithValue("$source", announcement.Source);
                cmd.Parameters.AddWithValue("$title", announcement.Title);
                cmd.Parameters.AddWithValue("$body", announcement.Body);
                cmd.Parameters.AddWithValue("$link", DbValue(announcement.Link?.ToString()));
            },
            cancellationToken);

    public async Task<IReadOnlyList<QuotaSnapshot>> GetRecentQuotaSnapshotsAsync(
        QuotaWindowKind kind,
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, provider, profile, source
            FROM quota_snapshots
            WHERE kind = $kind AND provider = $provider AND profile = $profile
            ORDER BY captured_at_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));

        var results = new List<QuotaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new QuotaSnapshot(
                Enum.Parse<QuotaWindowKind>(reader.GetString(0)),
                ParseUtc(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return results;
    }

    public async Task<FileIngestionCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_path, last_byte_offset, updated_at_utc, last_session_id, parser_version, source_identity
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
            ParseUtc(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public Task SaveCheckpointAsync(FileIngestionCheckpoint checkpoint, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO ingestion_checkpoints(file_path, last_byte_offset, updated_at_utc, last_session_id, parser_version, source_identity)
            VALUES($path, $offset, $updated, $session, $parser, $identity)
            ON CONFLICT(file_path) DO UPDATE SET
              last_byte_offset = excluded.last_byte_offset,
              updated_at_utc = excluded.updated_at_utc,
              last_session_id = excluded.last_session_id,
              parser_version = excluded.parser_version,
              source_identity = excluded.source_identity;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$path", checkpoint.FilePath);
                cmd.Parameters.AddWithValue("$offset", checkpoint.LastByteOffset);
                cmd.Parameters.AddWithValue("$updated", SerializeUtc(checkpoint.UpdatedAtUtc));
                cmd.Parameters.AddWithValue("$session", DbValue(checkpoint.LastSessionId));
                cmd.Parameters.AddWithValue("$parser", checkpoint.ParserVersion);
                cmd.Parameters.AddWithValue("$identity", DbValue(checkpoint.SourceIdentity));
            },
            cancellationToken);

    private static async Task ExecuteMigrationAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql; // nosemgrep: migration SQL is compile-time-only and contains no external values.
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task ExecutePreparedCommandAsync(string sql, Action<SqliteCommand> configure, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = sql; // nosemgrep: private callers pass compile-time SQL; all external values are parameters.
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string SerializeUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
