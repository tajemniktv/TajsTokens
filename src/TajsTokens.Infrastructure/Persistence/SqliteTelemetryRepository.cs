using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed class SqliteTelemetryRepository(string databasePath) : ITelemetryRepository, ISessionIngestionCheckpointStore
{
    private const int CurrentSchemaVersion = 7;
    private const int IntelligenceSchemaVersion = 1;
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    private readonly SemaphoreSlim _intelligenceInitializeGate = new(1, 1);
    private volatile bool _intelligenceInitialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // WAL preserves one coherent read snapshot without making dashboard readers block
        // ingestion/quota commits for the lifetime of the read transaction.
        var journalMode = connection.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=WAL;";
        var activeJournalMode = Convert.ToString(await journalMode.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(activeJournalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Failed to enable SQLite WAL mode (active mode: {activeJournalMode ?? "unknown"}).");
        }

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
                DROP TABLE IF EXISTS workspaces;
                DROP TABLE IF EXISTS repositories;
                DROP TABLE IF EXISTS forecast_snapshots;

                CREATE TABLE quota_snapshots (
                    provider TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    captured_at_utc TEXT NOT NULL,
                    used_percent REAL,
                    window_minutes INTEGER,
                    resets_at_utc TEXT,
                    source TEXT NOT NULL,
                    PRIMARY KEY(provider, profile, kind, captured_at_utc, source)
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

                CREATE TABLE repositories (
                    repository_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT,
                    remote_url TEXT,
                    first_seen_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NOT NULL
                );

                CREATE TABLE workspaces (
                    workspace_id TEXT PRIMARY KEY,
                    path TEXT NOT NULL,
                    repository_id TEXT,
                    first_seen_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NOT NULL
                );

                CREATE TABLE forecast_snapshots (
                    provider TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    generated_at_utc TEXT NOT NULL,
                    burn_rate_percent_per_hour REAL,
                    estimated_exhaustion_at_utc TEXT,
                    survives_until_reset INTEGER,
                    sustainable_percent_per_hour REAL,
                    confidence REAL NOT NULL,
                    state TEXT NOT NULL DEFAULT 'Learning',
                    burn_pressure REAL,
                    projected_remaining_at_reset_percent REAL,
                    trend TEXT,
                    is_quantized_flat INTEGER NOT NULL DEFAULT 0,
                    quota_source TEXT,
                    quota_authority TEXT NOT NULL DEFAULT 'Unknown',
                    quota_captured_at_utc TEXT,
                    quota_window_minutes INTEGER,
                    quota_resets_at_utc TEXT,
                    PRIMARY KEY(provider, profile, kind, generated_at_utc)
                );

                CREATE INDEX IF NOT EXISTS idx_quota_snapshots_lookup
                    ON quota_snapshots(provider, profile, kind, captured_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_token_usage_observed ON token_usage(observed_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_usage_events_time ON usage_events(timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_agent_relationships_child ON agent_relationships(child_agent_id);
                CREATE INDEX IF NOT EXISTS idx_workspaces_repository ON workspaces(repository_id);
                CREATE INDEX IF NOT EXISTS idx_forecast_snapshots_lookup
                    ON forecast_snapshots(provider, profile, kind, generated_at_utc DESC);

                PRAGMA user_version = 7;
                """, cancellationToken);
            return;
        }

        if (version == 1)
        {
            await ExecuteMigrationAsync(connection, """
                ALTER TABLE ingestion_checkpoints ADD COLUMN source_identity TEXT;
                PRAGMA user_version = 2;
                """, cancellationToken);
            version = 2;
        }

        if (version == 2)
        {
            await ExecuteMigrationAsync(connection, """
                CREATE TABLE repositories (
                    repository_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT,
                    remote_url TEXT,
                    first_seen_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NOT NULL
                );

                CREATE TABLE workspaces (
                    workspace_id TEXT PRIMARY KEY,
                    path TEXT NOT NULL,
                    repository_id TEXT,
                    first_seen_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NOT NULL
                );

                CREATE TABLE forecast_snapshots (
                    provider TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    generated_at_utc TEXT NOT NULL,
                    burn_rate_percent_per_hour REAL,
                    estimated_exhaustion_at_utc TEXT,
                    survives_until_reset INTEGER,
                    sustainable_percent_per_hour REAL,
                    confidence REAL NOT NULL,
                    PRIMARY KEY(provider, profile, kind, generated_at_utc)
                );

                CREATE INDEX IF NOT EXISTS idx_workspaces_repository ON workspaces(repository_id);
                CREATE INDEX IF NOT EXISTS idx_forecast_snapshots_lookup
                    ON forecast_snapshots(provider, profile, kind, generated_at_utc DESC);

                PRAGMA user_version = 3;
                """, cancellationToken);
            version = 3;
        }

        if (version == 3)
        {
            // Phase 3.5 added reset-aware forecast semantics. Defaults preserve the meaning of old
            // persisted rows while new rows round-trip the full decision state instead of silently
            // degrading back to Learning after a restart.
            await ExecuteMigrationAsync(connection, """
                ALTER TABLE forecast_snapshots ADD COLUMN state TEXT NOT NULL DEFAULT 'Learning';
                ALTER TABLE forecast_snapshots ADD COLUMN burn_pressure REAL;
                ALTER TABLE forecast_snapshots ADD COLUMN projected_remaining_at_reset_percent REAL;
                ALTER TABLE forecast_snapshots ADD COLUMN trend TEXT;
                ALTER TABLE forecast_snapshots ADD COLUMN is_quantized_flat INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 4;
                """, cancellationToken);
            version = 4;
        }

        if (version == 4)
        {
            await ExecuteMigrationAsync(connection, """
                CREATE TABLE IF NOT EXISTS agent_relationships (
                    parent_agent_id TEXT NOT NULL,
                    child_agent_id TEXT NOT NULL,
                    linked_at_utc TEXT NOT NULL,
                    PRIMARY KEY(parent_agent_id, child_agent_id)
                );
                CREATE INDEX IF NOT EXISTS idx_agent_relationships_child ON agent_relationships(child_agent_id);
                PRAGMA user_version = 5;
                """, cancellationToken);
            version = 5;
        }

        if (version == 5)
        {
            await ExecuteMigrationAsync(connection, """
                ALTER TABLE forecast_snapshots ADD COLUMN quota_source TEXT;
                ALTER TABLE forecast_snapshots ADD COLUMN quota_authority TEXT NOT NULL DEFAULT 'Unknown';
                ALTER TABLE forecast_snapshots ADD COLUMN quota_captured_at_utc TEXT;
                PRAGMA user_version = 6;
                """, cancellationToken);
            version = 6;
        }

        if (version == 6)
        {
            // Preserve the existing v6 rows while expanding quota identity to include source.
            // SQLite cannot alter a primary key in place, so rebuild only this table inside the
            // migration transaction. Forecast lineage gains the remaining reset/window fields here
            // as well; v6 remains an immutable historical schema definition.
            var hasQuotaSnapshots = await TableExistsAsync(connection, "quota_snapshots", cancellationToken);
            var quotaMigration = hasQuotaSnapshots
                ? """
                    DROP INDEX IF EXISTS idx_quota_snapshots_lookup;
                    ALTER TABLE quota_snapshots RENAME TO quota_snapshots_v6;

                    CREATE TABLE quota_snapshots (
                        provider TEXT NOT NULL,
                        profile TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        used_percent REAL,
                        window_minutes INTEGER,
                        resets_at_utc TEXT,
                        source TEXT NOT NULL,
                        PRIMARY KEY(provider, profile, kind, captured_at_utc, source)
                    );

                    INSERT INTO quota_snapshots(
                        provider, profile, kind, captured_at_utc, used_percent,
                        window_minutes, resets_at_utc, source)
                    SELECT provider, profile, kind, captured_at_utc, used_percent,
                           window_minutes, resets_at_utc, source
                    FROM quota_snapshots_v6;

                    DROP TABLE quota_snapshots_v6;

                    CREATE INDEX idx_quota_snapshots_lookup
                        ON quota_snapshots(provider, profile, kind, captured_at_utc DESC);
                  """
                : """
                    CREATE TABLE quota_snapshots (
                        provider TEXT NOT NULL,
                        profile TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        used_percent REAL,
                        window_minutes INTEGER,
                        resets_at_utc TEXT,
                        source TEXT NOT NULL,
                        PRIMARY KEY(provider, profile, kind, captured_at_utc, source)
                    );
                    CREATE INDEX idx_quota_snapshots_lookup
                        ON quota_snapshots(provider, profile, kind, captured_at_utc DESC);
                  """;
            await ExecuteMigrationAsync(connection, quotaMigration + """
                ALTER TABLE forecast_snapshots ADD COLUMN quota_window_minutes INTEGER;
                ALTER TABLE forecast_snapshots ADD COLUMN quota_resets_at_utc TEXT;
                PRAGMA user_version = 7;
                """, cancellationToken);
        }
    }

    /// <summary>
    /// Initializes the additive Phase 4 persistence component through the repository-owned writer
    /// boundary. Intelligence query services remain read-only after this step.
    /// </summary>
    public async Task InitializeIntelligenceAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (_intelligenceInitialized)
        {
            return;
        }

        await _intelligenceInitializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_intelligenceInitialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS intelligence_schema (
                    component TEXT PRIMARY KEY,
                    version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS quota_reset_events (
                    event_id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    detected_at_utc TEXT NOT NULL,
                    effective_at_utc TEXT NOT NULL,
                    before_used_percent REAL,
                    after_used_percent REAL,
                    previous_reset_at_utc TEXT,
                    current_reset_at_utc TEXT,
                    classification TEXT NOT NULL,
                    confidence REAL NOT NULL,
                    source TEXT NOT NULL,
                    explanation TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_quota_reset_events_time
                    ON quota_reset_events(detected_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_quota_reset_events_window
                    ON quota_reset_events(provider, profile, kind, detected_at_utc DESC);

                INSERT INTO intelligence_schema(component, version)
                VALUES('phase4-intelligence', 1)
                ON CONFLICT(component) DO UPDATE SET version = MAX(version, excluded.version);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM intelligence_schema WHERE component = 'phase4-intelligence';";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (version > IntelligenceSchemaVersion)
            {
                throw new InvalidOperationException($"Intelligence schema {version} is newer than supported version {IntelligenceSchemaVersion}.");
            }

            _intelligenceInitialized = true;
        }
        finally
        {
            _intelligenceInitializeGate.Release();
        }
    }

    public async Task<int> UpsertQuotaResetEventAsync(QuotaResetEvent resetEvent, CancellationToken cancellationToken)
    {
        await InitializeIntelligenceAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var existsCommand = connection.CreateCommand();
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = "SELECT 1 FROM quota_reset_events WHERE event_id = $id LIMIT 1;";
        existsCommand.Parameters.AddWithValue("$id", resetEvent.EventId);
        var existed = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quota_reset_events(
                event_id, kind, provider, profile, detected_at_utc, effective_at_utc,
                before_used_percent, after_used_percent, previous_reset_at_utc, current_reset_at_utc,
                classification, confidence, source, explanation)
            VALUES($id, $kind, $provider, $profile, $detected, $effective,
                   $before, $after, $previousReset, $currentReset,
                   $classification, $confidence, $source, $explanation)
            ON CONFLICT(event_id) DO UPDATE SET
                kind = excluded.kind,
                provider = excluded.provider,
                profile = excluded.profile,
                detected_at_utc = excluded.detected_at_utc,
                effective_at_utc = excluded.effective_at_utc,
                before_used_percent = excluded.before_used_percent,
                after_used_percent = excluded.after_used_percent,
                previous_reset_at_utc = excluded.previous_reset_at_utc,
                current_reset_at_utc = excluded.current_reset_at_utc,
                classification = excluded.classification,
                confidence = excluded.confidence,
                source = excluded.source,
                explanation = excluded.explanation;
            """;
        command.Parameters.AddWithValue("$id", resetEvent.EventId);
        command.Parameters.AddWithValue("$kind", resetEvent.Kind.ToString());
        command.Parameters.AddWithValue("$provider", resetEvent.Provider);
        command.Parameters.AddWithValue("$profile", resetEvent.Profile);
        command.Parameters.AddWithValue("$detected", SerializeUtc(resetEvent.DetectedAtUtc));
        command.Parameters.AddWithValue("$effective", SerializeUtc(resetEvent.EffectiveAtUtc));
        command.Parameters.AddWithValue("$before", DbValue(resetEvent.BeforeUsedPercent));
        command.Parameters.AddWithValue("$after", DbValue(resetEvent.AfterUsedPercent));
        command.Parameters.AddWithValue("$previousReset", resetEvent.PreviousResetAtUtc is null ? DBNull.Value : SerializeUtc(resetEvent.PreviousResetAtUtc.Value));
        command.Parameters.AddWithValue("$currentReset", resetEvent.CurrentResetAtUtc is null ? DBNull.Value : SerializeUtc(resetEvent.CurrentResetAtUtc.Value));
        command.Parameters.AddWithValue("$classification", resetEvent.Classification.ToString());
        command.Parameters.AddWithValue("$confidence", resetEvent.Confidence);
        command.Parameters.AddWithValue("$source", resetEvent.Source);
        command.Parameters.AddWithValue("$explanation", resetEvent.Explanation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return existed ? 0 : 1;
    }

    public Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO quota_snapshots(provider, profile, kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, source)
            VALUES($provider, $profile, $kind, $captured, $used, $window, $resets, $source)
            ON CONFLICT(provider, profile, kind, captured_at_utc, source) DO UPDATE SET
              used_percent = excluded.used_percent,
              window_minutes = excluded.window_minutes,
              resets_at_utc = excluded.resets_at_utc;
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

    public Task UpsertRepositoryAsync(RepositoryIdentity repository, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO repositories(repository_id, name, root_path, remote_url, first_seen_at_utc, last_seen_at_utc)
            VALUES($id, $name, $rootPath, $remoteUrl, $firstSeen, $lastSeen)
            ON CONFLICT(repository_id) DO UPDATE SET
              name = excluded.name,
              root_path = COALESCE(excluded.root_path, repositories.root_path),
              remote_url = COALESCE(excluded.remote_url, repositories.remote_url),
              first_seen_at_utc = MIN(repositories.first_seen_at_utc, excluded.first_seen_at_utc),
              last_seen_at_utc = MAX(repositories.last_seen_at_utc, excluded.last_seen_at_utc);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", repository.RepositoryId);
                cmd.Parameters.AddWithValue("$name", repository.Name);
                cmd.Parameters.AddWithValue("$rootPath", DbValue(repository.RootPath));
                cmd.Parameters.AddWithValue("$remoteUrl", DbValue(repository.RemoteUrl));
                cmd.Parameters.AddWithValue("$firstSeen", SerializeUtc(repository.FirstSeenAtUtc));
                cmd.Parameters.AddWithValue("$lastSeen", SerializeUtc(repository.LastSeenAtUtc));
            },
            cancellationToken);

    public Task UpsertWorkspaceAsync(WorkspaceIdentity workspace, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO workspaces(workspace_id, path, repository_id, first_seen_at_utc, last_seen_at_utc)
            VALUES($id, $path, $repositoryId, $firstSeen, $lastSeen)
            ON CONFLICT(workspace_id) DO UPDATE SET
              path = excluded.path,
              repository_id = COALESCE(excluded.repository_id, workspaces.repository_id),
              first_seen_at_utc = MIN(workspaces.first_seen_at_utc, excluded.first_seen_at_utc),
              last_seen_at_utc = MAX(workspaces.last_seen_at_utc, excluded.last_seen_at_utc);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", workspace.WorkspaceId);
                cmd.Parameters.AddWithValue("$path", workspace.Path);
                cmd.Parameters.AddWithValue("$repositoryId", DbValue(workspace.RepositoryId));
                cmd.Parameters.AddWithValue("$firstSeen", SerializeUtc(workspace.FirstSeenAtUtc));
                cmd.Parameters.AddWithValue("$lastSeen", SerializeUtc(workspace.LastSeenAtUtc));
            },
            cancellationToken);

    public Task UpsertForecastSnapshotAsync(ForecastSnapshot snapshot, CancellationToken cancellationToken) =>
        ExecutePreparedCommandAsync(
            """
            INSERT INTO forecast_snapshots(
                provider, profile, kind, generated_at_utc, burn_rate_percent_per_hour,
                estimated_exhaustion_at_utc, survives_until_reset, sustainable_percent_per_hour, confidence,
                state, burn_pressure, projected_remaining_at_reset_percent, trend, is_quantized_flat,
                quota_source, quota_authority, quota_captured_at_utc, quota_window_minutes, quota_resets_at_utc)
            VALUES($provider, $profile, $kind, $generated, $burnRate, $exhaustion, $survives, $sustainable, $confidence,
                   $state, $pressure, $remainingAtReset, $trend, $quantizedFlat,
                   $quotaSource, $quotaAuthority, $quotaCaptured, $quotaWindow, $quotaReset)
            ON CONFLICT(provider, profile, kind, generated_at_utc) DO UPDATE SET
              burn_rate_percent_per_hour = excluded.burn_rate_percent_per_hour,
              estimated_exhaustion_at_utc = excluded.estimated_exhaustion_at_utc,
              survives_until_reset = excluded.survives_until_reset,
              sustainable_percent_per_hour = excluded.sustainable_percent_per_hour,
              confidence = excluded.confidence,
              state = excluded.state,
              burn_pressure = excluded.burn_pressure,
              projected_remaining_at_reset_percent = excluded.projected_remaining_at_reset_percent,
              trend = excluded.trend,
              is_quantized_flat = excluded.is_quantized_flat,
              quota_source = excluded.quota_source,
              quota_authority = excluded.quota_authority,
              quota_captured_at_utc = excluded.quota_captured_at_utc,
              quota_window_minutes = excluded.quota_window_minutes,
              quota_resets_at_utc = excluded.quota_resets_at_utc;
            """,
            cmd =>
            {
                var forecast = snapshot.Forecast;
                cmd.Parameters.AddWithValue("$provider", snapshot.Provider);
                cmd.Parameters.AddWithValue("$profile", snapshot.Profile);
                cmd.Parameters.AddWithValue("$kind", forecast.Kind.ToString());
                cmd.Parameters.AddWithValue("$generated", SerializeUtc(forecast.GeneratedAtUtc));
                cmd.Parameters.AddWithValue("$burnRate", DbValue(forecast.BurnRatePercentPerHour));
                cmd.Parameters.AddWithValue("$exhaustion", forecast.EstimatedExhaustionAtUtc is null ? DBNull.Value : SerializeUtc(forecast.EstimatedExhaustionAtUtc.Value));
                cmd.Parameters.AddWithValue("$survives", DbValue(forecast.SurvivesUntilReset is bool survives ? (survives ? 1 : 0) : null));
                cmd.Parameters.AddWithValue("$sustainable", DbValue(forecast.SustainablePercentPerHour));
                cmd.Parameters.AddWithValue("$confidence", forecast.Confidence);
                cmd.Parameters.AddWithValue("$state", forecast.State.ToString());
                cmd.Parameters.AddWithValue("$pressure", DbValue(forecast.BurnPressure));
                cmd.Parameters.AddWithValue("$remainingAtReset", DbValue(forecast.ProjectedRemainingAtResetPercent));
                cmd.Parameters.AddWithValue("$trend", DbValue(forecast.Trend));
                cmd.Parameters.AddWithValue("$quantizedFlat", forecast.IsQuantizedFlat ? 1 : 0);
                cmd.Parameters.AddWithValue("$quotaSource", DbValue(snapshot.QuotaSource));
                cmd.Parameters.AddWithValue("$quotaAuthority", snapshot.QuotaAuthority.ToString());
                cmd.Parameters.AddWithValue("$quotaCaptured", snapshot.QuotaCapturedAtUtc is null ? DBNull.Value : SerializeUtc(snapshot.QuotaCapturedAtUtc.Value));
                cmd.Parameters.AddWithValue("$quotaWindow", DbValue(snapshot.QuotaWindowMinutes));
                cmd.Parameters.AddWithValue("$quotaReset", snapshot.QuotaResetsAtUtc is null ? DBNull.Value : SerializeUtc(snapshot.QuotaResetsAtUtc.Value));
            },
            cancellationToken);

    public async Task<IReadOnlyList<QuotaSnapshot>> GetRecentQuotaSnapshotsAsync(
        QuotaWindowKind kind,
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken,
        DateTimeOffset? capturedAtUpperBoundUtc = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, provider, profile, source
            FROM quota_snapshots
            WHERE kind = $kind AND provider = $provider AND profile = $profile
              AND ($capturedAtUpperBound IS NULL OR captured_at_utc <= $capturedAtUpperBound)
            ORDER BY captured_at_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));
        command.Parameters.AddWithValue(
            "$capturedAtUpperBound",
            capturedAtUpperBoundUtc is null ? DBNull.Value : SerializeUtc(capturedAtUpperBoundUtc.Value));

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

    public async Task<IReadOnlyList<ForecastSnapshot>> GetRecentForecastSnapshotsAsync(
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
            SELECT provider, profile, kind, generated_at_utc, burn_rate_percent_per_hour,
                   estimated_exhaustion_at_utc, survives_until_reset, sustainable_percent_per_hour, confidence,
                   state, burn_pressure, projected_remaining_at_reset_percent, trend, is_quantized_flat,
                   quota_source, quota_authority, quota_captured_at_utc, quota_window_minutes, quota_resets_at_utc
            FROM forecast_snapshots
            WHERE kind = $kind AND provider = $provider AND profile = $profile
            ORDER BY generated_at_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));

        var results = new List<ForecastSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = !reader.IsDBNull(9) && Enum.TryParse<ForecastState>(reader.GetString(9), out var parsedState)
                ? parsedState
                : ForecastState.Learning;

            results.Add(new ForecastSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                new Forecast(
                    Enum.Parse<QuotaWindowKind>(reader.GetString(2)),
                    ParseUtc(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.GetDouble(8),
                    state,
                    reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    !reader.IsDBNull(13) && reader.GetInt32(13) != 0),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) || !Enum.TryParse<QuotaObservationAuthority>(reader.GetString(15), out var authority) ? QuotaObservationAuthority.Unknown : authority,
                reader.IsDBNull(16) ? null : ParseUtc(reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.IsDBNull(18) ? null : ParseUtc(reader.GetString(18))));
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

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string SerializeUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
