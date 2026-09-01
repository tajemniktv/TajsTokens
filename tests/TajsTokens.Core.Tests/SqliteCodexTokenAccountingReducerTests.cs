using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexTokenAccountingReducerTests
{
    [Fact]
    public async Task Store_PersistsReducerDeltasAndStateAtomicallyAcrossRegressionResetAndReplay()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-counter-reducer-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var observed = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(database);

            await store.ApplyCumulativeTokenObservationAsync(
                Token("first", observed, 100, 80, 10, 4, 110, Last(100, 80, 0, 10, 4, 110)),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("second", observed.AddSeconds(1), 130, 105, 20, 8, 150, Last(30, 25, 0, 10, 4, 40)),
                CancellationToken.None);

            // A one-token stale cumulative snapshot is recorded for provenance,
            // but cannot advance the reducer state.
            await store.ApplyCumulativeTokenObservationAsync(
                Token("stale", observed.AddSeconds(2), 129, 104, 19, 7, 149, Last(1, 1, 0, 1, 0, 2)),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("recovered", observed.AddSeconds(3), 140, 110, 22, 9, 162, Last(10, 5, 0, 2, 1, 12)),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("reset", observed.AddSeconds(4), 10, 8, 1, 0, 11, Last(10, 8, 0, 1, 0, 11)),
                CancellationToken.None);

            // Replaying an older event remains idempotent even after a later epoch.
            await store.ApplyCumulativeTokenObservationAsync(
                Token("second", observed.AddSeconds(1), 130, 105, 20, 8, 150, Last(30, 25, 0, 10, 4, 40)),
                CancellationToken.None);

            var summary = await store.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(173, summary.NativeTokens.ReportedTotal);
            Assert.Equal(summary.NativeTokens.ReportedTotal, summary.NativeTokens.DisjointTotal);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();
            var state = connection.CreateCommand();
            state.CommandText = "SELECT counter_epoch, total_tokens FROM codex_counter_state WHERE session_id = 'session';";
            await using var reader = await state.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(11, reader.GetInt64(1));

            var events = connection.CreateCommand();
            events.CommandText = "SELECT COUNT(*) FROM codex_native_token_events WHERE session_id = 'session';";
            Assert.Equal(5L, Convert.ToInt64(await events.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static CodexCumulativeTokenObservation Token(
        string id,
        DateTimeOffset observed,
        long input,
        long cached,
        long output,
        long reasoning,
        long total,
        CodexTokenUsageSnapshot? last) =>
        new(id, "session.jsonl", "session", "session", observed, "model", "high", input, cached, 0, output, reasoning, total, last);

    private static CodexTokenUsageSnapshot Last(
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        long total) =>
        new(input, cached, cacheWrite, output, reasoning, total);
}
