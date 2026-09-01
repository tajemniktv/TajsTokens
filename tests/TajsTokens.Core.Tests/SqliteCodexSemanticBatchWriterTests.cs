using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexIngestionBatchWriterTests
{
    [Fact]
    public async Task WriteBatchAsync_PersistsSemanticAndStorageBatchAndKeepsTokenReplayIdempotent()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-batch-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexIngestionBatchWriter(database, observatory);
            var start = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

            var first = BuildRecord(
                "event-1",
                sourcePath,
                start,
                input: 100,
                cached: 40,
                output: 20,
                reasoning: 5,
                usedPercent: 10);
            var second = BuildRecord(
                "event-2",
                sourcePath,
                start.AddMinutes(1),
                input: 180,
                cached: 70,
                output: 35,
                reasoning: 9,
                usedPercent: 12);

            await writer.WriteBatchAsync("source-1", sourcePath, 1_234, [first, second], CancellationToken.None);
            await writer.WriteBatchAsync("source-1", sourcePath, 1_234, [first, second], CancellationToken.None);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();

            Assert.Equal(1L, await CountAsync(connection, "sessions"));
            Assert.Equal(1L, await CountAsync(connection, "agents"));
            Assert.Equal(2L, await CountAsync(connection, "usage_events"));
            Assert.Equal(2L, await CountAsync(connection, "context_observations"));
            Assert.Equal(2L, await CountAsync(connection, "quota_snapshots"));
            Assert.Equal(2L, await CountAsync(connection, "rollout_records"));
            Assert.Equal(1L, await CountAsync(connection, "rollout_files"));
            Assert.Equal(2L, await CountAsync(connection, "codex_native_token_events"));
            Assert.Equal(1L, await CountAsync(connection, "codex_counter_state"));

            var storage = Assert.Single(await observatory.GetRolloutStorageAsync(10, CancellationToken.None));
            Assert.Equal("session-a", storage.SessionId);
            Assert.Equal(1_234, storage.SizeBytes);
            Assert.Equal(2, storage.RecordsSeen);
            Assert.DoesNotContain(directory.FullName, storage.FilePath, StringComparison.OrdinalIgnoreCase);

            var totals = await observatory.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(110, totals.NativeTokens.UncachedInput);
            Assert.Equal(70, totals.NativeTokens.CacheRead);
            Assert.Equal(26, totals.NativeTokens.NonReasoningOutput);
            Assert.Equal(9, totals.NativeTokens.ReasoningOutput);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_ReplacedPathRetiresOldTokenGenerationAndCounterState()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-replace-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexIngestionBatchWriter(database, observatory);
            var start = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

            await writer.WriteBatchAsync(
                "source-old",
                sourcePath,
                2_000,
                [
                    BuildRecord("old-1", sourcePath, start, 100, 40, 20, 5, 10),
                    BuildRecord("old-2", sourcePath, start.AddMinutes(1), 180, 70, 35, 9, 12)
                ],
                CancellationToken.None);

            await writer.WriteBatchAsync(
                "source-new",
                sourcePath,
                900,
                [BuildRecord("new-1", sourcePath, start.AddMinutes(2), 50, 10, 5, 1, 14)],
                CancellationToken.None);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();

            Assert.Equal(1L, await CountAsync(connection, "rollout_files"));
            Assert.Equal(1L, await CountAsync(connection, "rollout_records"));
            Assert.Equal(1L, await CountAsync(connection, "codex_native_token_events"));
            Assert.Equal(1L, await CountAsync(connection, "codex_counter_state"));

            var sourceCommand = connection.CreateCommand();
            sourceCommand.CommandText = "SELECT source_identity FROM rollout_files LIMIT 1;";
            Assert.Equal("source-new", (string?)await sourceCommand.ExecuteScalarAsync());

            var accounting = await new SqliteNativeCodexAccountingProvider(database)
                .GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(55, Assert.Single(accounting.Usage).Breakdown.Total);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static ParsedRolloutRecord BuildRecord(
        string id,
        string sourcePath,
        DateTimeOffset timestamp,
        long input,
        long cached,
        long output,
        long reasoning,
        double usedPercent)
    {
        const string sessionId = "session-a";
        var token = new CodexCumulativeTokenObservation(
            id,
            sourcePath,
            sessionId,
            sessionId,
            timestamp,
            "gpt-5.6-luna",
            "xhigh",
            input,
            cached,
            0,
            output,
            reasoning,
            input + output);

        return new ParsedRolloutRecord(
            id,
            "token_count",
            128,
            timestamp,
            sessionId,
            new CodexSession(sessionId, sessionId, "private-repository", timestamp, timestamp, "active"),
            new Agent(sessionId, sessionId, "Root agent", AgentRuntimeState.Running, timestamp, "gpt-5.6-luna"),
            null,
            new UsageEvent(id + ":usage", sessionId, timestamp, "token_count", "Codex token/quota observation", null),
            token,
            [new QuotaSnapshot(
                QuotaWindowKind.FiveHour,
                timestamp,
                usedPercent,
                300,
                timestamp.AddHours(5),
                "codex",
                "codex",
                "codex-rollout:primary")],
            new CodexContextObservation(
                id + ":ctx",
                sessionId,
                sessionId,
                timestamp,
                "gpt-5.6-luna",
                input,
                258_400,
                false,
                128));
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
