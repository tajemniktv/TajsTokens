using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>
/// Phase 3 observatory persistence layered into the existing telemetry database. The base telemetry
/// repository remains the owner of PRAGMA user_version; this component versions only its additive
/// observatory tables so it can evolve without making the Phase 1/2 provider path depend on rollout parsing.
/// </summary>
public sealed class SqliteCodexObservatoryStore(string databasePath) : ICodexObservatoryStore
{
    private const int ObservatorySchemaVersion = 1;
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS observatory_schema (
                    component TEXT PRIMARY KEY,
                    version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS codex_native_token_events (
                    source_event_id TEXT PRIMARY KEY,
                    source_file TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    agent_id TEXT,
                    observed_at_utc TEXT NOT NULL,
                    model TEXT,
                    reasoning_effort TEXT,
                    counter_epoch INTEGER NOT NULL,
                    uncached_input_tokens INTEGER NOT NULL,
                    cache_read_tokens INTEGER NOT NULL,
                    cache_write_tokens INTEGER NOT NULL,
                    non_reasoning_output_tokens INTEGER NOT NULL,
                    reasoning_output_tokens INTEGER NOT NULL,
                    reported_total_tokens INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS codex_counter_state (
                    source_file TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    counter_epoch INTEGER NOT NULL,
                    input_tokens INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL,
                    cache_write_input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    reasoning_output_tokens INTEGER NOT NULL,
                    total_tokens INTEGER NOT NULL,
                    last_source_event_id TEXT NOT NULL,
                    PRIMARY KEY(source_file, session_id)
                );

                CREATE TABLE IF NOT EXISTS context_observations (
                    event_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    agent_id TEXT,
                    observed_at_utc TEXT NOT NULL,
                    model TEXT,
                    input_tokens INTEGER,
                    context_window_tokens INTEGER,
                    is_compaction INTEGER NOT NULL,
                    record_bytes INTEGER
                );

                CREATE TABLE IF NOT EXISTS rollout_records (
                    source_record_id TEXT PRIMARY KEY,
                    file_path TEXT NOT NULL,
                    session_id TEXT,
                    event_class TEXT NOT NULL,
                    record_bytes INTEGER NOT NULL,
                    observed_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS rollout_files (
                    file_path TEXT PRIMARY KEY,
                    session_id TEXT,
                    size_bytes INTEGER NOT NULL,
                    last_seen_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_native_tokens_session_time
                    ON codex_native_token_events(session_id, observed_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_context_session_time
                    ON context_observations(session_id, observed_at_utc DESC);
                CREATE INDEX IF NOT EXISTS idx_rollout_records_file
                    ON rollout_records(file_path);
                CREATE INDEX IF NOT EXISTS idx_rollout_records_session
                    ON rollout_records(session_id, observed_at_utc DESC);

                INSERT INTO observatory_schema(component, version)
                VALUES('codex-observatory', 1)
                ON CONFLICT(component) DO UPDATE SET version = MAX(version, excluded.version);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();

            var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM observatory_schema WHERE component = 'codex-observatory';";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (version > ObservatorySchemaVersion)
            {
                throw new InvalidOperationException($"Codex observatory schema {version} is newer than supported version {ObservatorySchemaVersion}.");
            }

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task UpsertSessionAsync(CodexSession session, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ExecuteAsync(
            """
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
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", session.SessionId);
                command.Parameters.AddWithValue("$thread", DbValue(session.ThreadId));
                command.Parameters.AddWithValue("$repo", session.Repository);
                command.Parameters.AddWithValue("$start", SerializeUtc(session.StartedAtUtc));
                command.Parameters.AddWithValue("$last", session.LastActivityAtUtc is null ? DBNull.Value : SerializeUtc(session.LastActivityAtUtc.Value));
                command.Parameters.AddWithValue("$status", session.Status);
            },
            cancellationToken);
    }

    public async Task UpsertAgentAsync(Agent agent, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO agents(agent_id, session_id, name, state, last_seen_utc, model)
            VALUES($id, $session, $name, $state, $lastSeen, $model)
            ON CONFLICT(agent_id) DO UPDATE SET
              session_id = excluded.session_id,
              name = CASE WHEN excluded.name = '' THEN agents.name ELSE excluded.name END,
              state = excluded.state,
              last_seen_utc = MAX(agents.last_seen_utc, excluded.last_seen_utc),
              model = COALESCE(excluded.model, agents.model);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", agent.AgentId);
                command.Parameters.AddWithValue("$session", agent.SessionId);
                command.Parameters.AddWithValue("$name", agent.Name);
                command.Parameters.AddWithValue("$state", agent.State.ToString());
                command.Parameters.AddWithValue("$lastSeen", SerializeUtc(agent.LastSeenUtc));
                command.Parameters.AddWithValue("$model", DbValue(agent.Model));
            },
            cancellationToken);
    }

    public async Task UpsertAgentRelationshipAsync(AgentRelationship relationship, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO agent_relationships(parent_agent_id, child_agent_id, linked_at_utc)
            VALUES($parent, $child, $linked)
            ON CONFLICT(parent_agent_id, child_agent_id) DO UPDATE SET
              linked_at_utc = MIN(agent_relationships.linked_at_utc, excluded.linked_at_utc);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$parent", relationship.ParentAgentId);
                command.Parameters.AddWithValue("$child", relationship.ChildAgentId);
                command.Parameters.AddWithValue("$linked", SerializeUtc(relationship.LinkedAtUtc));
            },
            cancellationToken);
    }

    public async Task UpsertUsageEventAsync(UsageEvent usageEvent, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO usage_events(event_id, session_id, timestamp_utc, event_type, summary, token_delta)
            VALUES($id, $session, $time, $type, $summary, $delta)
            ON CONFLICT(event_id) DO NOTHING;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", usageEvent.EventId);
                command.Parameters.AddWithValue("$session", usageEvent.SessionId);
                command.Parameters.AddWithValue("$time", SerializeUtc(usageEvent.TimestampUtc));
                command.Parameters.AddWithValue("$type", usageEvent.EventType);
                command.Parameters.AddWithValue("$summary", usageEvent.Summary);
                command.Parameters.AddWithValue("$delta", DbValue(usageEvent.TokenDelta));
            },
            cancellationToken);
    }

    public async Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO quota_snapshots(provider, profile, kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, source)
            VALUES($provider, $profile, $kind, $captured, $used, $window, $resets, $source)
            ON CONFLICT(provider, profile, kind, captured_at_utc) DO UPDATE SET
              used_percent = excluded.used_percent,
              window_minutes = excluded.window_minutes,
              resets_at_utc = excluded.resets_at_utc,
              source = excluded.source;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$provider", snapshot.Provider);
                command.Parameters.AddWithValue("$profile", snapshot.Profile);
                command.Parameters.AddWithValue("$kind", snapshot.Kind.ToString());
                command.Parameters.AddWithValue("$captured", SerializeUtc(snapshot.CapturedAtUtc));
                command.Parameters.AddWithValue("$used", DbValue(snapshot.UsedPercent));
                command.Parameters.AddWithValue("$window", DbValue(snapshot.WindowMinutes));
                command.Parameters.AddWithValue("$resets", snapshot.ResetsAtUtc is null ? DBNull.Value : SerializeUtc(snapshot.ResetsAtUtc.Value));
                command.Parameters.AddWithValue("$source", snapshot.Source);
            },
            cancellationToken);
    }

    public async Task ApplyCumulativeTokenObservationAsync(CodexCumulativeTokenObservation observation, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT counter_epoch, input_tokens, cached_input_tokens, cache_write_input_tokens,
                   output_tokens, reasoning_output_tokens, total_tokens, last_source_event_id
            FROM codex_counter_state
            WHERE source_file = $file AND session_id = $session;
            """;
        read.Parameters.AddWithValue("$file", observation.SourceFile);
        read.Parameters.AddWithValue("$session", observation.SessionId);

        CounterState? previous = null;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                previous = new CounterState(
                    reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7));
            }
        }

        var reset = previous is not null &&
                    (observation.TotalTokens < previous.Total ||
                     observation.InputTokens < previous.Input ||
                     observation.CachedInputTokens < previous.Cached ||
                     observation.CacheWriteInputTokens < previous.CacheWrite ||
                     observation.OutputTokens < previous.Output ||
                     observation.ReasoningOutputTokens < previous.Reasoning);
        var epoch = previous is null ? 0 : previous.Epoch + (reset ? 1 : 0);

        long Delta(long current, long prior) => previous is null || reset ? current : Math.Max(0, current - prior);
        var inputDelta = Delta(observation.InputTokens, previous?.Input ?? 0);
        var cachedDelta = Delta(observation.CachedInputTokens, previous?.Cached ?? 0);
        var cacheWriteDelta = Delta(observation.CacheWriteInputTokens, previous?.CacheWrite ?? 0);
        var outputDelta = Delta(observation.OutputTokens, previous?.Output ?? 0);
        var reasoningDelta = Delta(observation.ReasoningOutputTokens, previous?.Reasoning ?? 0);
        var totalDelta = Delta(observation.TotalTokens, previous?.Total ?? 0);
        var uncachedDelta = Math.Max(0, inputDelta - cachedDelta - cacheWriteDelta);
        var nonReasoningOutputDelta = Math.Max(0, outputDelta - reasoningDelta);

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO codex_native_token_events(
                source_event_id, source_file, session_id, agent_id, observed_at_utc, model, reasoning_effort,
                counter_epoch, uncached_input_tokens, cache_read_tokens, cache_write_tokens,
                non_reasoning_output_tokens, reasoning_output_tokens, reported_total_tokens)
            VALUES($event, $file, $session, $agent, $observed, $model, $reasoning, $epoch,
                   $uncached, $cached, $cacheWrite, $output, $reasoningOutput, $total)
            ON CONFLICT(source_event_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$event", observation.SourceEventId);
        insert.Parameters.AddWithValue("$file", observation.SourceFile);
        insert.Parameters.AddWithValue("$session", observation.SessionId);
        insert.Parameters.AddWithValue("$agent", DbValue(observation.AgentId));
        insert.Parameters.AddWithValue("$observed", SerializeUtc(observation.ObservedAtUtc));
        insert.Parameters.AddWithValue("$model", DbValue(observation.Model));
        insert.Parameters.AddWithValue("$reasoning", DbValue(observation.ReasoningEffort));
        insert.Parameters.AddWithValue("$epoch", epoch);
        insert.Parameters.AddWithValue("$uncached", uncachedDelta);
        insert.Parameters.AddWithValue("$cached", cachedDelta);
        insert.Parameters.AddWithValue("$cacheWrite", cacheWriteDelta);
        insert.Parameters.AddWithValue("$output", nonReasoningOutputDelta);
        insert.Parameters.AddWithValue("$reasoningOutput", reasoningDelta);
        insert.Parameters.AddWithValue("$total", totalDelta);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        // Advance the cumulative state even for a zero-delta/repeated snapshot. The event id keeps the
        // persisted delta idempotent, while the latest raw counter state is what detects future epochs.
        var upsertState = connection.CreateCommand();
        upsertState.Transaction = transaction;
        upsertState.CommandText = """
            INSERT INTO codex_counter_state(
                source_file, session_id, counter_epoch, input_tokens, cached_input_tokens,
                cache_write_input_tokens, output_tokens, reasoning_output_tokens, total_tokens, last_source_event_id)
            VALUES($file, $session, $epoch, $input, $cached, $cacheWrite, $output, $reasoning, $total, $event)
            ON CONFLICT(source_file, session_id) DO UPDATE SET
              counter_epoch = excluded.counter_epoch,
              input_tokens = excluded.input_tokens,
              cached_input_tokens = excluded.cached_input_tokens,
              cache_write_input_tokens = excluded.cache_write_input_tokens,
              output_tokens = excluded.output_tokens,
              reasoning_output_tokens = excluded.reasoning_output_tokens,
              total_tokens = excluded.total_tokens,
              last_source_event_id = excluded.last_source_event_id;
            """;
        upsertState.Parameters.AddWithValue("$file", observation.SourceFile);
        upsertState.Parameters.AddWithValue("$session", observation.SessionId);
        upsertState.Parameters.AddWithValue("$epoch", epoch);
        upsertState.Parameters.AddWithValue("$input", observation.InputTokens);
        upsertState.Parameters.AddWithValue("$cached", observation.CachedInputTokens);
        upsertState.Parameters.AddWithValue("$cacheWrite", observation.CacheWriteInputTokens);
        upsertState.Parameters.AddWithValue("$output", observation.OutputTokens);
        upsertState.Parameters.AddWithValue("$reasoning", observation.ReasoningOutputTokens);
        upsertState.Parameters.AddWithValue("$total", observation.TotalTokens);
        upsertState.Parameters.AddWithValue("$event", observation.SourceEventId);
        await upsertState.ExecuteNonQueryAsync(cancellationToken);

        transaction.Commit();
    }

    public async Task UpsertContextObservationAsync(CodexContextObservation observation, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await ExecuteAsync(
            """
            INSERT INTO context_observations(
                event_id, session_id, agent_id, observed_at_utc, model, input_tokens,
                context_window_tokens, is_compaction, record_bytes)
            VALUES($event, $session, $agent, $observed, $model, $input, $window, $compaction, $bytes)
            ON CONFLICT(event_id) DO NOTHING;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$event", observation.EventId);
                command.Parameters.AddWithValue("$session", observation.SessionId);
                command.Parameters.AddWithValue("$agent", DbValue(observation.AgentId));
                command.Parameters.AddWithValue("$observed", SerializeUtc(observation.ObservedAtUtc));
                command.Parameters.AddWithValue("$model", DbValue(observation.Model));
                command.Parameters.AddWithValue("$input", DbValue(observation.InputTokens));
                command.Parameters.AddWithValue("$window", DbValue(observation.ContextWindowTokens));
                command.Parameters.AddWithValue("$compaction", observation.IsCompaction ? 1 : 0);
                command.Parameters.AddWithValue("$bytes", DbValue(observation.RecordBytes));
            },
            cancellationToken);
    }

    public async Task RecordRolloutRecordAsync(
        string sourceRecordId,
        string filePath,
        string? sessionId,
        string eventClass,
        long recordBytes,
        long fileSizeBytes,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO rollout_records(source_record_id, file_path, session_id, event_class, record_bytes, observed_at_utc)
            VALUES($id, $file, $session, $class, $bytes, $observed)
            ON CONFLICT(source_record_id) DO NOTHING;
            """;
        record.Parameters.AddWithValue("$id", sourceRecordId);
        record.Parameters.AddWithValue("$file", filePath);
        record.Parameters.AddWithValue("$session", DbValue(sessionId));
        record.Parameters.AddWithValue("$class", eventClass);
        record.Parameters.AddWithValue("$bytes", Math.Max(0, recordBytes));
        record.Parameters.AddWithValue("$observed", SerializeUtc(observedAtUtc));
        await record.ExecuteNonQueryAsync(cancellationToken);

        var file = connection.CreateCommand();
        file.Transaction = transaction;
        file.CommandText = """
            INSERT INTO rollout_files(file_path, session_id, size_bytes, last_seen_at_utc)
            VALUES($file, $session, $size, $seen)
            ON CONFLICT(file_path) DO UPDATE SET
              session_id = COALESCE(excluded.session_id, rollout_files.session_id),
              size_bytes = excluded.size_bytes,
              last_seen_at_utc = MAX(rollout_files.last_seen_at_utc, excluded.last_seen_at_utc);
            """;
        file.Parameters.AddWithValue("$file", filePath);
        file.Parameters.AddWithValue("$session", DbValue(sessionId));
        file.Parameters.AddWithValue("$size", Math.Max(0, fileSizeBytes));
        file.Parameters.AddWithValue("$seen", SerializeUtc(observedAtUtc));
        await file.ExecuteNonQueryAsync(cancellationToken);

        transaction.Commit();
    }

    public async Task<IReadOnlyList<CodexSessionOverview>> GetSessionOverviewsAsync(int take, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id,
                   (SELECT ar.parent_agent_id FROM agent_relationships ar WHERE ar.child_agent_id = s.session_id LIMIT 1),
                   COALESCE((SELECT a.name FROM agents a WHERE a.agent_id = s.session_id LIMIT 1), s.session_id),
                   s.repository, s.started_at_utc, s.last_activity_at_utc, s.status,
                   (SELECT a.model FROM agents a WHERE a.agent_id = s.session_id LIMIT 1),
                   COALESCE((SELECT SUM(t.uncached_input_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.cache_read_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.cache_write_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.non_reasoning_output_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.reasoning_output_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.reported_total_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT COUNT(*) FROM context_observations c WHERE c.session_id = s.session_id AND c.is_compaction = 1), 0),
                   (SELECT MAX(CASE WHEN c.context_window_tokens > 0 AND c.input_tokens IS NOT NULL
                                    THEN c.input_tokens * 100.0 / c.context_window_tokens END)
                    FROM context_observations c WHERE c.session_id = s.session_id),
                   COALESCE((SELECT SUM(r.record_bytes) FROM rollout_records r WHERE r.session_id = s.session_id), 0),
                   COALESCE((SELECT COUNT(*) FROM usage_events e WHERE e.session_id = s.session_id), 0)
            FROM sessions s
            WHERE EXISTS(SELECT 1 FROM rollout_files f WHERE f.session_id = s.session_id)
            ORDER BY COALESCE(s.last_activity_at_utc, s.started_at_utc) DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", Math.Max(0, take));

        var results = new List<CodexSessionOverview>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CodexSessionOverview(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                new CodexNativeTokenTotals(reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13)),
                reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetDouble(15),
                reader.GetInt64(16),
                reader.GetInt32(17)));
        }

        return results;
    }

    public async Task<IReadOnlyList<Agent>> GetAgentsAsync(string? sessionId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sessionId is null
            ? "SELECT agent_id, session_id, name, state, last_seen_utc, model FROM agents ORDER BY last_seen_utc DESC;"
            : "SELECT agent_id, session_id, name, state, last_seen_utc, model FROM agents WHERE session_id = $session OR agent_id = $session ORDER BY last_seen_utc DESC;";
        if (sessionId is not null)
        {
            command.Parameters.AddWithValue("$session", sessionId);
        }

        var results = new List<Agent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new Agent(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Enum.TryParse<AgentRuntimeState>(reader.GetString(3), out var state) ? state : AgentRuntimeState.Unknown,
                ParseUtc(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    public async Task<IReadOnlyList<AgentRelationship>> GetAgentRelationshipsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT parent_agent_id, child_agent_id, linked_at_utc FROM agent_relationships ORDER BY linked_at_utc;";
        var results = new List<AgentRelationship>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AgentRelationship(reader.GetString(0), reader.GetString(1), ParseUtc(reader.GetString(2))));
        }

        return results;
    }

    public async Task<IReadOnlyList<UsageEvent>> GetTimelineAsync(string sessionId, int take, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, session_id, timestamp_utc, event_type, summary, token_delta
            FROM usage_events WHERE session_id = $session
            ORDER BY timestamp_utc DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));
        var results = new List<UsageEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsageEvent(reader.GetString(0), reader.GetString(1), ParseUtc(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }
        return results;
    }

    public async Task<IReadOnlyList<CodexContextObservation>> GetContextObservationsAsync(string sessionId, int take, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, session_id, agent_id, observed_at_utc, model, input_tokens,
                   context_window_tokens, is_compaction, record_bytes
            FROM context_observations WHERE session_id = $session
            ORDER BY observed_at_utc DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));
        var results = new List<CodexContextObservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CodexContextObservation(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseUtc(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt32(7) != 0, reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }
        return results;
    }

    public async Task<IReadOnlyList<CodexRolloutStorageSummary>> GetRolloutStorageAsync(int take, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.file_path, f.session_id, f.size_bytes,
                   COALESCE((SELECT COUNT(*) FROM rollout_records r WHERE r.file_path = f.file_path), 0),
                   COALESCE((SELECT MAX(r.record_bytes) FROM rollout_records r WHERE r.file_path = f.file_path), 0),
                   f.last_seen_at_utc
            FROM rollout_files f ORDER BY f.size_bytes DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", Math.Max(0, take));
        var results = new List<CodexRolloutStorageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CodexRolloutStorageSummary(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(4), ParseUtc(reader.GetString(5))));
        }
        return results;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private async Task ExecuteAsync(string sql, Action<SqliteCommand> configure, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql; // nosemgrep: private callers supply compile-time SQL; values are parameters.
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
    private static string SerializeUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed record CounterState(
        int Epoch,
        long Input,
        long Cached,
        long CacheWrite,
        long Output,
        long Reasoning,
        long Total,
        string LastSourceEventId);
}
