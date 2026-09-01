using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexSemanticBatchWriterTests
{
    [Fact]
    public async Task WriteBatchAsync_PersistsProjectionBatchAndKeepsTokenReplayIdempotent()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-semantic-batch-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexSemanticBatchWriter(database, observatory);
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

            await writer.WriteBatchAsync([first, second], CancellationToken.None);
            await writer.WriteBatchAsync([first, second], CancellationToken.None);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();

            Assert.Equal(1L, await CountAsync(connection, "sessions"));
            Assert.Equal(1L, await CountAsync(connection, "agents"));
            Assert.Equal(2L, await CountAsync(connection, "usage_events"));
            Assert.Equal(2L, await CountAsync(connection, "context_observations"));
            Assert.Equal(2L, await CountAsync(connection, "quota_snapshots"));
            Assert.Equal(2L, await CountAsync(connection, "codex_native_token_events"));
            Assert.Equal(1L, await CountAsync(connection, "codex_counter_state"));

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
