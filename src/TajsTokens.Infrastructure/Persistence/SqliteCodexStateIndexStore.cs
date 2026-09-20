// Taj's Tokens | SqliteCodexStateIndexStore.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Infrastructure.Persistence;

internal sealed record CodexStateThreadFingerprint(
    string ThreadId,
    long UpdatedAtMs,
    long TokensUsed,
    string? Model,
    string? ReasoningEffort,
    bool Archived,
    string RolloutPathHash)
{
    public bool Matches(CodexStateThread thread)
    {
        return UpdatedAtMs == thread.UpdatedAtMs &&
               TokensUsed == thread.TokensUsed &&
               string.Equals(Model, thread.Model, StringComparison.Ordinal) &&
               string.Equals(ReasoningEffort, thread.ReasoningEffort, StringComparison.Ordinal) &&
               Archived == thread.Archived &&
               string.Equals(RolloutPathHash, thread.RolloutPathHash, StringComparison.Ordinal);
    }

    public bool CatalogMetadataMatches(CodexStateThread thread)
    {
        return string.Equals(Model, thread.Model, StringComparison.Ordinal) &&
               string.Equals(ReasoningEffort, thread.ReasoningEffort, StringComparison.Ordinal) &&
               Archived == thread.Archived &&
               string.Equals(RolloutPathHash, thread.RolloutPathHash, StringComparison.Ordinal);
    }
}

/// <summary>
///     Owns the small TajsTokens-side cursor/fingerprint state for Codex's optional private SQLite
///     catalog. It intentionally stores only privacy-safe change evidence, never provider-owned paths.
/// </summary>
internal sealed class SqliteCodexStateIndexStore
{
    private const int SchemaVersion = 6;
    private const string Component = "codex-state-index";
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteCodexStateIndexStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

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

            SqliteCommand bootstrap = connection.CreateCommand();
            bootstrap.CommandText = """
                                    CREATE TABLE IF NOT EXISTS codex_state_index_schema (
                                        component TEXT PRIMARY KEY,
                                        version INTEGER NOT NULL
                                    );
                                    """;
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);

            SqliteCommand versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM codex_state_index_schema WHERE component = $component;";
            versionCommand.Parameters.AddWithValue("$component", Component);
            object? rawVersion = await versionCommand.ExecuteScalarAsync(cancellationToken);
            int version = rawVersion is null || rawVersion is DBNull
                ? 0
                : Convert.ToInt32(rawVersion, CultureInfo.InvariantCulture);

            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Codex state index schema {version} is newer than supported version {SchemaVersion}.");
            }

            if (version == 0)
            {
                SqliteCommand migration = connection.CreateCommand();
                migration.CommandText = """
                                        CREATE TABLE IF NOT EXISTS codex_state_thread_fingerprints (
                                            thread_id TEXT PRIMARY KEY,
                                            updated_at_ms INTEGER NOT NULL,
                                            tokens_used INTEGER NOT NULL,
                                            model TEXT,
                                            reasoning_effort TEXT,
                                            archived INTEGER NOT NULL,
                                            rollout_path_hash TEXT NOT NULL,
                                            applied_at_utc TEXT NOT NULL
                                        );

                                        CREATE INDEX IF NOT EXISTS idx_codex_state_fingerprints_updated
                                            ON codex_state_thread_fingerprints(updated_at_ms, thread_id);

                                        CREATE TABLE IF NOT EXISTS codex_state_sync (
                                            component TEXT PRIMARY KEY,
                                            watermark_updated_at_ms INTEGER NOT NULL,
                                            updated_at_utc TEXT NOT NULL
                                        );

                                        INSERT INTO codex_state_sync(component, watermark_updated_at_ms, updated_at_utc)
                                        VALUES('codex-state-index', 0, '0001-01-01T00:00:00.0000000+00:00')
                                        ON CONFLICT(component) DO NOTHING;

                                        INSERT INTO codex_state_index_schema(component, version)
                                        VALUES('codex-state-index', 1);
                                        """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
                version = 1;
            }

            if (version == 1)
            {
                // The indexed fast path must revisit unchanged historical files after the
                // typed-v4 workload parser upgrade, not only files Codex happened to update.
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                                        DELETE FROM codex_state_thread_fingerprints;
                                        UPDATE codex_state_sync SET watermark_updated_at_ms = 0 WHERE component = 'codex-state-index';
                                        UPDATE codex_state_index_schema SET version = 2 WHERE component = 'codex-state-index';
                                        """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                version = 2;
            }

            if (version == 2)
            {
                // Revisit unchanged catalog files for typed-v5 quota provenance. Only the
                // acceleration cache is invalidated; native evidence and byte checkpoints survive.
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                                        DELETE FROM codex_state_thread_fingerprints;
                                        UPDATE codex_state_sync SET watermark_updated_at_ms = 0 WHERE component = 'codex-state-index';
                                        UPDATE codex_state_index_schema SET version = 3 WHERE component = 'codex-state-index';
                                        """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                version = 3;
            }

            if (version == 3)
            {
                // typed-v6 adds tier-setting evidence. Byte checkpoints and retained facts survive;
                // only disposable selection hints must be invalidated to revisit unchanged files.
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                                        DELETE FROM codex_state_thread_fingerprints;
                                        UPDATE codex_state_sync SET watermark_updated_at_ms = 0 WHERE component = 'codex-state-index';
                                        UPDATE codex_state_index_schema SET version = 4 WHERE component = 'codex-state-index';
                                        """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                version = 4;
            }

            if (version == 4)
            {
                // Re-select indexed Desktop suffixed paths after ownership parsing was corrected.
                // Unaffected files keep their parser version and byte checkpoints; alternates
                // remain outside catalog-selected acquisition.
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                                        DELETE FROM codex_state_thread_fingerprints;
                                        UPDATE codex_state_sync SET watermark_updated_at_ms = 0 WHERE component = 'codex-state-index';
                                        UPDATE codex_state_index_schema SET version = 5 WHERE component = 'codex-state-index';
                                        """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                version = 5;
            }

            if (version == 5)
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand migration = connection.CreateCommand();
                migration.Transaction = transaction;
                migration.CommandText = """
                                        DELETE FROM codex_state_thread_fingerprints;
                                        UPDATE codex_state_sync SET watermark_updated_at_ms = 0 WHERE component = 'codex-state-index';
                                        UPDATE codex_state_index_schema SET version = 6 WHERE component = 'codex-state-index';
                                        """;
                await migration.ExecuteNonQueryAsync(cancellationToken);
                transaction.Commit();
                version = 6;
            }

            if (version != SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Codex state index schema {version} is not supported by this build (expected {SchemaVersion}).");
            }

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<long> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT watermark_updated_at_ms FROM codex_state_sync WHERE component = $component;";
        command.Parameters.AddWithValue("$component", Component);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? 0
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, CodexStateThreadFingerprint>> GetFingerprintsAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
                              SELECT thread_id, updated_at_ms, tokens_used, model, reasoning_effort, archived, rollout_path_hash
                              FROM codex_state_thread_fingerprints;
                              """;

        var results = new Dictionary<string, CodexStateThreadFingerprint>(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fingerprint = new CodexStateThreadFingerprint(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.GetString(6));
            results[fingerprint.ThreadId] = fingerprint;
        }

        return results;
    }

    public async Task<long?> GetPersistedCounterTotalAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT total_tokens FROM codex_counter_state WHERE session_id = $session LIMIT 1;";
        command.Parameters.AddWithValue("$session", sessionId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task CommitAsync(
        IReadOnlyCollection<CodexStateThread> appliedThreads,
        long watermarkUpdatedAtMs,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction();

        string appliedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        SqliteCommand upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
                             INSERT INTO codex_state_thread_fingerprints(
                                 thread_id, updated_at_ms, tokens_used, model, reasoning_effort, archived,
                                 rollout_path_hash, applied_at_utc)
                             VALUES($thread, $updated, $tokens, $model, $reasoning, $archived, $pathHash, $applied)
                             ON CONFLICT(thread_id) DO UPDATE SET
                                 updated_at_ms = excluded.updated_at_ms,
                                 tokens_used = excluded.tokens_used,
                                 model = excluded.model,
                                 reasoning_effort = excluded.reasoning_effort,
                                 archived = excluded.archived,
                                 rollout_path_hash = excluded.rollout_path_hash,
                                 applied_at_utc = excluded.applied_at_utc;
                             """;
        SqliteParameter threadParameter = upsert.Parameters.Add("$thread", SqliteType.Text);
        SqliteParameter updatedParameter = upsert.Parameters.Add("$updated", SqliteType.Integer);
        SqliteParameter tokensParameter = upsert.Parameters.Add("$tokens", SqliteType.Integer);
        SqliteParameter modelParameter = upsert.Parameters.Add("$model", SqliteType.Text);
        SqliteParameter reasoningParameter = upsert.Parameters.Add("$reasoning", SqliteType.Text);
        SqliteParameter archivedParameter = upsert.Parameters.Add("$archived", SqliteType.Integer);
        SqliteParameter pathHashParameter = upsert.Parameters.Add("$pathHash", SqliteType.Text);
        SqliteParameter appliedParameter = upsert.Parameters.Add("$applied", SqliteType.Text);

        foreach (CodexStateThread thread in appliedThreads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            threadParameter.Value = thread.ThreadId;
            updatedParameter.Value = thread.UpdatedAtMs;
            tokensParameter.Value = thread.TokensUsed;
            modelParameter.Value = thread.Model is null ? DBNull.Value : thread.Model;
            reasoningParameter.Value = thread.ReasoningEffort is null ? DBNull.Value : thread.ReasoningEffort;
            archivedParameter.Value = thread.Archived ? 1 : 0;
            pathHashParameter.Value = thread.RolloutPathHash;
            appliedParameter.Value = appliedAt;
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        SqliteCommand watermark = connection.CreateCommand();
        watermark.Transaction = transaction;
        watermark.CommandText = """
                                UPDATE codex_state_sync
                                SET watermark_updated_at_ms = $watermark,
                                    updated_at_utc = $updated
                                WHERE component = $component;
                                """;
        watermark.Parameters.AddWithValue("$watermark", Math.Max(0, watermarkUpdatedAtMs));
        watermark.Parameters.AddWithValue("$updated", appliedAt);
        watermark.Parameters.AddWithValue("$component", Component);
        await watermark.ExecuteNonQueryAsync(cancellationToken);

        transaction.Commit();
    }
}