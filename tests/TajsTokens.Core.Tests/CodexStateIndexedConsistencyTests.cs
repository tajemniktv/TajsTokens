using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexStateIndexedConsistencyTests
{
    [Fact]
    public async Task RefreshAsync_DoesNotFingerprintStateWhileRolloutBoundaryIsStillPending()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-ahead-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var telemetryPath = Path.Combine(directory.FullName, "telemetry.db");
        var statePath = Path.Combine(codexHome.FullName, "state_5.sqlite");
        var threadId = "thread-state-ahead";
        var rolloutPath = Path.Combine(sessions.FullName, "state-ahead.jsonl");
        await File.WriteAllTextAsync(rolloutPath, "{}\n");
        var rolloutWriteMs = new DateTimeOffset(File.GetLastWriteTimeUtc(rolloutPath)).ToUnixTimeMilliseconds();

        try
        {
            await CreateStateDatabaseAsync(
                statePath,
                [new StateThread(threadId, rolloutPath, rolloutWriteMs - 1_000, rolloutWriteMs, 123)]);

            var repository = new SqliteTelemetryRepository(telemetryPath);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(telemetryPath);
            await store.InitializeAsync(CancellationToken.None);
            var ingestion = new PendingThenCompleteIngestionService(threadId);
            var service = new CodexObservatoryService(
                ingestion,
                store,
                new CodexStateCatalog(codexHome.FullName),
                new SqliteCodexStateIndexStore(telemetryPath),
                [sessions.FullName]);

            await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(1, ingestion.Calls);
            Assert.Equal(0L, await CountAsync(telemetryPath, "codex_state_thread_fingerprints"));

            await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, ingestion.Calls);
            Assert.Equal(1L, await CountAsync(telemetryPath, "codex_state_thread_fingerprints"));

            await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, ingestion.Calls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_ReconcilesNewSpawnEdgeWithoutReopeningUnchangedRollouts()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-edge-only-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var telemetryPath = Path.Combine(directory.FullName, "telemetry.db");
        var statePath = Path.Combine(codexHome.FullName, "state_5.sqlite");
        var parentPath = Path.Combine(sessions.FullName, "parent.jsonl");
        var childPath = Path.Combine(sessions.FullName, "child.jsonl");
        await File.WriteAllTextAsync(parentPath, "{}\n");
        await File.WriteAllTextAsync(childPath, "{}\n");
        var nowMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();

        try
        {
            await CreateStateDatabaseAsync(
                statePath,
                [
                    new StateThread("parent", parentPath, nowMs - 2_000, nowMs - 1_000, 0),
                    new StateThread("child", childPath, nowMs - 1_500, nowMs, 0)
                ]);

            var repository = new SqliteTelemetryRepository(telemetryPath);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(telemetryPath);
            await store.InitializeAsync(CancellationToken.None);
            var ingestion = new CompleteIngestionService();
            var service = new CodexObservatoryService(
                ingestion,
                store,
                new CodexStateCatalog(codexHome.FullName),
                new SqliteCodexStateIndexStore(telemetryPath),
                [sessions.FullName]);

            await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, ingestion.Calls);
            Assert.Empty(await store.GetAgentRelationshipsAsync(CancellationToken.None));

            await InsertEdgeAsync(statePath, "parent", "child", "open");
            await service.RefreshAsync(CancellationToken.None);

            Assert.Equal(2, ingestion.Calls);
            var relationship = Assert.Single(await store.GetAgentRelationshipsAsync(CancellationToken.None));
            Assert.Equal("parent", relationship.ParentAgentId);
            Assert.Equal("child", relationship.ChildAgentId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_SkipsEmptyNewerStateDatabaseAndUsesPopulatedOlderGeneration()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-rotation-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var rolloutPath = Path.Combine(sessions.FullName, "older.jsonl");
        await File.WriteAllTextAsync(rolloutPath, "{}\n");

        try
        {
            await CreateStateDatabaseAsync(Path.Combine(codexHome.FullName, "state_6.sqlite"), []);
            await CreateStateDatabaseAsync(
                Path.Combine(codexHome.FullName, "state_5.sqlite"),
                [new StateThread("older", rolloutPath, 1_000, 2_000, 10)]);

            var result = await new CodexStateCatalog(codexHome.FullName)
                .TryReadSinceAsync(0, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("older", Assert.Single(result!.Threads).ThreadId);
            Assert.EndsWith("state_5.sqlite", result.DatabasePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_SkipsMalformedNewerStateDatabaseAndUsesValidOlderGeneration()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-malformed-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var badPath = Path.Combine(sessions.FullName, "bad.jsonl");
        var goodPath = Path.Combine(sessions.FullName, "good.jsonl");
        await File.WriteAllTextAsync(badPath, "{}\n");
        await File.WriteAllTextAsync(goodPath, "{}\n");

        try
        {
            var newer = Path.Combine(codexHome.FullName, "state_6.sqlite");
            await CreateStateDatabaseAsync(
                newer,
                [new StateThread("bad", badPath, 1_000, 2_000, 10)]);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = newer }.ToString()))
            {
                await connection.OpenAsync();
                var malformed = connection.CreateCommand();
                malformed.CommandText = "UPDATE threads SET updated_at_ms = 'not-an-integer' WHERE id = 'bad';";
                await malformed.ExecuteNonQueryAsync();
            }

            await CreateStateDatabaseAsync(
                Path.Combine(codexHome.FullName, "state_5.sqlite"),
                [new StateThread("good", goodPath, 1_000, 2_000, 10)]);

            var result = await new CodexStateCatalog(codexHome.FullName)
                .TryReadSinceAsync(0, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("good", Assert.Single(result!.Threads).ThreadId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task CreateStateDatabaseAsync(string path, IReadOnlyCollection<StateThread> threads)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE threads (
                id TEXT PRIMARY KEY,
                rollout_path TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                tokens_used INTEGER NOT NULL DEFAULT 0,
                model TEXT,
                reasoning_effort TEXT,
                archived INTEGER NOT NULL DEFAULT 0,
                created_at_ms INTEGER,
                updated_at_ms INTEGER
            );
            CREATE INDEX idx_threads_updated_at_ms ON threads(updated_at_ms DESC, id DESC);
            CREATE TABLE thread_spawn_edges (
                parent_thread_id TEXT NOT NULL,
                child_thread_id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL
            );
            """;
        await schema.ExecuteNonQueryAsync();

        foreach (var thread in threads)
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO threads(
                    id, rollout_path, created_at, updated_at, tokens_used, model,
                    reasoning_effort, archived, created_at_ms, updated_at_ms)
                VALUES($id, $path, $createdSeconds, $updatedSeconds, $tokens,
                       'gpt-5.6-luna', 'xhigh', 0, $createdMs, $updatedMs);
                """;
            insert.Parameters.AddWithValue("$id", thread.Id);
            insert.Parameters.AddWithValue("$path", thread.RolloutPath);
            insert.Parameters.AddWithValue("$createdSeconds", thread.CreatedAtMs / 1000);
            insert.Parameters.AddWithValue("$updatedSeconds", thread.UpdatedAtMs / 1000);
            insert.Parameters.AddWithValue("$tokens", thread.TokensUsed);
            insert.Parameters.AddWithValue("$createdMs", thread.CreatedAtMs);
            insert.Parameters.AddWithValue("$updatedMs", thread.UpdatedAtMs);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertEdgeAsync(string statePath, string parent, string child, string status)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = statePath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO thread_spawn_edges(parent_thread_id, child_thread_id, status) VALUES($parent, $child, $status);";
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$child", child);
        command.Parameters.AddWithValue("$status", status);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record StateThread(
        string Id,
        string RolloutPath,
        long CreatedAtMs,
        long UpdatedAtMs,
        long TokensUsed);

    private sealed class CompleteIngestionService : ICodexSessionIngestionService
    {
        public int Calls { get; private set; }

        public Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var length = new FileInfo(filePath).Length;
            return Task.FromResult(new CodexIngestionResult(1, 1, Path.GetFileNameWithoutExtension(filePath))
            {
                LastCompleteRecordOffset = length,
                SourceLength = length
            });
        }
    }

    private sealed class PendingThenCompleteIngestionService(string sessionId) : ICodexSessionIngestionService
    {
        public int Calls { get; private set; }

        public Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var length = new FileInfo(filePath).Length;
            return Task.FromResult(new CodexIngestionResult(Calls == 1 ? 0 : 1, Calls == 1 ? 0 : 1, sessionId)
            {
                LastCompleteRecordOffset = Calls == 1 ? Math.Max(0, length - 1) : length,
                SourceLength = length
            });
        }
    }
}
