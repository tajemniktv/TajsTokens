using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexStateIndexedIngestionTests
{
    [Fact]
    public async Task RefreshAsync_StateCatalogSkipsUnchangedRolloutAndReopensChangedThread()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-index-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var telemetryPath = Path.Combine(directory.FullName, "telemetry.db");
        var statePath = Path.Combine(codexHome.FullName, "state_5.sqlite");
        var threadId = "01a05c52-16e8-7152-a439-18bf1c3b1b7a";
        var rolloutPath = Path.Combine(sessions.FullName, $"rollout-2026-09-01T11-00-00-{threadId}.jsonl");
        await File.WriteAllTextAsync(rolloutPath, "{}\n");

        try
        {
            await CreateStateDatabaseAsync(
                statePath,
                [new StateThread(threadId, rolloutPath, 1_700_000_000_000, 1_700_000_001_000, 100)]);

            var repository = new SqliteTelemetryRepository(telemetryPath);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(telemetryPath);
            await store.InitializeAsync(CancellationToken.None);
            var ingestion = new RecordingIngestionService(threadId);
            var service = new CodexObservatoryService(
                ingestion,
                store,
                new CodexStateCatalog(codexHome.FullName),
                new SqliteCodexStateIndexStore(telemetryPath),
                [sessions.FullName]);

            var first = await service.RefreshAsync(CancellationToken.None);
            Assert.Single(ingestion.Paths);
            Assert.Equal(1, first.FilesDiscovered);
            Assert.Equal(1, first.FilesScanned);

            var second = await service.RefreshAsync(CancellationToken.None);
            Assert.Single(ingestion.Paths);
            Assert.Equal(1, second.FilesDiscovered);
            Assert.Equal(0, second.FilesScanned);
            Assert.Equal(0, second.RecordsScanned);

            await UpdateThreadAsync(statePath, threadId, 1_700_000_002_000, 250);

            var third = await service.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, ingestion.Paths.Count);
            Assert.Equal(1, third.FilesScanned);
            Assert.Equal(1, third.RecordsScanned);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = telemetryPath }.ToString());
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT rollout_path_hash FROM codex_state_thread_fingerprints WHERE thread_id = $id;";
            command.Parameters.AddWithValue("$id", threadId);
            var stored = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.NotEmpty(stored);
            Assert.DoesNotContain(directory.FullName, stored, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rollout-", stored, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_NewProcessReconcilesPathChangeWithoutTimestampAdvance()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-reconcile-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var archive = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "archived_sessions"));
        var telemetryPath = Path.Combine(directory.FullName, "telemetry.db");
        var statePath = Path.Combine(codexHome.FullName, "state_5.sqlite");
        var threadId = "thread-reconcile";
        var activePath = Path.Combine(sessions.FullName, "active.jsonl");
        var archivedPath = Path.Combine(archive.FullName, "archived.jsonl");
        await File.WriteAllTextAsync(activePath, "{}\n");
        await File.WriteAllTextAsync(archivedPath, "{}\n");

        try
        {
            await CreateStateDatabaseAsync(
                statePath,
                [new StateThread(threadId, activePath, 10_000, 20_000, 100)]);

            var repository = new SqliteTelemetryRepository(telemetryPath);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(telemetryPath);
            await store.InitializeAsync(CancellationToken.None);
            var indexStore = new SqliteCodexStateIndexStore(telemetryPath);

            var firstIngestion = new RecordingIngestionService(threadId);
            var firstProcess = new CodexObservatoryService(
                firstIngestion,
                store,
                new CodexStateCatalog(codexHome.FullName),
                indexStore,
                [sessions.FullName, archive.FullName]);
            await firstProcess.RefreshAsync(CancellationToken.None);
            Assert.Single(firstIngestion.Paths);

            // Simulate provider metadata changing while TajsTokens is not running. The deliberately
            // unchanged updated_at_ms proves process-start full reconciliation is doing real work.
            await UpdateRolloutPathOnlyAsync(statePath, threadId, archivedPath);

            var secondIngestion = new RecordingIngestionService(threadId);
            var secondProcess = new CodexObservatoryService(
                secondIngestion,
                store,
                new CodexStateCatalog(codexHome.FullName),
                indexStore,
                [sessions.FullName, archive.FullName]);
            var result = await secondProcess.RefreshAsync(CancellationToken.None);

            var reopened = Assert.Single(secondIngestion.Paths);
            Assert.Equal(Path.GetFullPath(archivedPath), reopened);
            Assert.Equal(1, result.FilesScanned);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_UnknownStateSchemaFallsBackToFilesystemDiscovery()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-fallback-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var telemetryPath = Path.Combine(directory.FullName, "telemetry.db");
        var statePath = Path.Combine(codexHome.FullName, "state_99.sqlite");
        var rolloutPath = Path.Combine(sessions.FullName, "rollout-fallback.jsonl");
        await File.WriteAllTextAsync(rolloutPath, "{}\n");

        try
        {
            await using (var state = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = statePath }.ToString()))
            {
                await state.OpenAsync();
                var incompatible = state.CreateCommand();
                incompatible.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL);";
                await incompatible.ExecuteNonQueryAsync();
            }

            var repository = new SqliteTelemetryRepository(telemetryPath);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(telemetryPath);
            await store.InitializeAsync(CancellationToken.None);
            var ingestion = new RecordingIngestionService("fallback-session");
            var service = new CodexObservatoryService(
                ingestion,
                store,
                new CodexStateCatalog(codexHome.FullName),
                new SqliteCodexStateIndexStore(telemetryPath),
                [sessions.FullName]);

            var result = await service.RefreshAsync(CancellationToken.None);

            Assert.Single(ingestion.Paths);
            Assert.Equal(Path.GetFullPath(rolloutPath), ingestion.Paths[0]);
            Assert.Equal(1, result.FilesDiscovered);
            Assert.Equal(1, result.FilesScanned);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Catalog_ReadsValidatedSpawnEdgesAndRespectsUpdatedWatermark()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-state-catalog-");
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
        var sessions = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sessions"));
        var statePath = Path.Combine(codexHome.FullName, "state_5.sqlite");
        var parentId = "parent";
        var childId = "child";
        var parentPath = Path.Combine(sessions.FullName, "parent.jsonl");
        var childPath = Path.Combine(sessions.FullName, "child.jsonl");
        await File.WriteAllTextAsync(parentPath, "{}\n");
        await File.WriteAllTextAsync(childPath, "{}\n");

        try
        {
            await CreateStateDatabaseAsync(
                statePath,
                [
                    new StateThread(parentId, parentPath, 1_000, 2_000, 100),
                    new StateThread(childId, childPath, 1_500, 3_000, 200)
                ],
                [(parentId, childId, "open")]);

            var catalog = new CodexStateCatalog(codexHome.FullName);
            var result = await catalog.TryReadSinceAsync(2_500, CancellationToken.None);
            Assert.NotNull(result);

            Assert.Equal(2, result!.TotalThreadCount);
            var changed = Assert.Single(result.Threads);
            Assert.Equal(childId, changed.ThreadId);
            var edge = Assert.Single(result.Edges);
            Assert.Equal(parentId, edge.ParentThreadId);
            Assert.Equal(childId, edge.ChildThreadId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task CreateStateDatabaseAsync(
        string path,
        IReadOnlyCollection<StateThread> threads,
        IReadOnlyCollection<(string Parent, string Child, string Status)>? edges = null)
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
                VALUES($id, $path, $createdSeconds, $updatedSeconds, $tokens, $model,
                       $reasoning, 0, $createdMs, $updatedMs);
                """;
            insert.Parameters.AddWithValue("$id", thread.Id);
            insert.Parameters.AddWithValue("$path", thread.RolloutPath);
            insert.Parameters.AddWithValue("$createdSeconds", thread.CreatedAtMs / 1000);
            insert.Parameters.AddWithValue("$updatedSeconds", thread.UpdatedAtMs / 1000);
            insert.Parameters.AddWithValue("$tokens", thread.TokensUsed);
            insert.Parameters.AddWithValue("$model", "gpt-5.6-luna");
            insert.Parameters.AddWithValue("$reasoning", "xhigh");
            insert.Parameters.AddWithValue("$createdMs", thread.CreatedAtMs);
            insert.Parameters.AddWithValue("$updatedMs", thread.UpdatedAtMs);
            await insert.ExecuteNonQueryAsync();
        }

        foreach (var edge in edges ?? [])
        {
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO thread_spawn_edges(parent_thread_id, child_thread_id, status) VALUES($parent, $child, $status);";
            insert.Parameters.AddWithValue("$parent", edge.Parent);
            insert.Parameters.AddWithValue("$child", edge.Child);
            insert.Parameters.AddWithValue("$status", edge.Status);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task UpdateThreadAsync(string statePath, string threadId, long updatedAtMs, long tokensUsed)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = statePath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE threads
            SET updated_at = $seconds,
                updated_at_ms = $milliseconds,
                tokens_used = $tokens
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$seconds", updatedAtMs / 1000);
        command.Parameters.AddWithValue("$milliseconds", updatedAtMs);
        command.Parameters.AddWithValue("$tokens", tokensUsed);
        command.Parameters.AddWithValue("$id", threadId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateRolloutPathOnlyAsync(string statePath, string threadId, string rolloutPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = statePath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE threads SET rollout_path = $path WHERE id = $id;";
        command.Parameters.AddWithValue("$path", rolloutPath);
        command.Parameters.AddWithValue("$id", threadId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record StateThread(
        string Id,
        string RolloutPath,
        long CreatedAtMs,
        long UpdatedAtMs,
        long TokensUsed);

    private sealed class RecordingIngestionService(string sessionId) : ICodexSessionIngestionService
    {
        public List<string> Paths { get; } = [];

        public Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(Path.GetFullPath(filePath));
            return Task.FromResult(new CodexIngestionResult(1, 1, sessionId));
        }
    }
}
