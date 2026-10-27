// Taj's Tokens | SqliteCodexTokenAccountingReducerTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexTokenAccountingReducerTests
{
    [Fact]
    public async Task UnknownSourceTimeKeepsCounterEvidenceButIsExcludedFromHourlyAndReplayWindowsAfterRestart()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-unknown-token-time-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset captured = DateTimeOffset.Parse("2026-09-03T10:00:00Z");
        DateTimeOffset eventAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            CodexTokenCountObservation unknown = Token(
                "unknown-time", captured, 100, 80, 10, 4, 110, Last(100, 80, 0, 10, 4, 110)) with
            {
                SourceEventAtUtc = null,
                CapturedAtUtc = captured,
                SourceTimestampQuality = CodexSourceTimestampQuality.Missing,
            };
            CodexTokenCountObservation known = Token(
                "known-time", eventAt, 200, 150, 20, 8, 220, Last(100, 70, 0, 10, 4, 110)) with
            {
                CapturedAtUtc = captured.AddSeconds(1),
            };

            using (var firstStore = new SqliteCodexObservatoryStore(database))
            {
                await firstStore.ApplyCumulativeTokenObservationAsync(unknown, CancellationToken.None);
                await firstStore.ApplyCumulativeTokenObservationAsync(known, CancellationToken.None);
            }

            using (var restartedStore = new SqliteCodexObservatoryStore(database))
            {
                await restartedStore.ApplyCumulativeTokenObservationAsync(unknown, CancellationToken.None);
                await restartedStore.ApplyCumulativeTokenObservationAsync(known, CancellationToken.None);
                CodexObservatorySummary summary = await restartedStore.GetSummaryAsync(CancellationToken.None);
                Assert.Equal(220, summary.NativeTokens.ReportedTotal);
            }

            CodexTokenAccountingSnapshot accounting = await new SqliteNativeCodexAccountingProvider(database)
                .GetSnapshotAsync(CancellationToken.None);
            TokenUsage model = Assert.Single(accounting.Usage);
            Assert.Equal(220, model.Breakdown.Total);
            Assert.Null(model.SourceEventAtUtc);
            Assert.Equal(CodexSourceTimestampQuality.Mixed, model.SourceTimestampQuality);
            TokenTimeBucket hour = Assert.Single(accounting.Hourly);
            Assert.Equal(110, hour.Breakdown.Total);

            CodexForecastDataset replay = await new SqliteForecastDatasetReader(database).ReadAsync(
                "codex",
                "default",
                eventAt.AddDays(-1),
                eventAt.AddDays(1),
                CancellationToken.None,
                includeQuota: false);
            CodexPredictiveTokenEvent replayed = Assert.Single(replay.Tokens);
            Assert.Equal("session", replayed.SessionId);
            Assert.Equal(eventAt, replayed.ObservedAtUtc);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();
            SqliteCommand provenance = connection.CreateCommand();
            provenance.CommandText = "SELECT source_event_at_utc, captured_at_utc, source_timestamp_quality FROM codex_native_token_events WHERE source_event_id='unknown-time';";
            await using SqliteDataReader row = await provenance.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.IsDBNull(0));
            Assert.Equal(captured.ToString("O"), row.GetString(1));
            Assert.Equal("Missing", row.GetString(2));
            SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM codex_native_token_events;";
            Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Version8MigrationMarksOldTokenTimesUnverifiedWithoutChangingStoredTimestamps()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-v8-migration-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset storedObserved = DateTimeOffset.Parse("2026-09-01T10:00:00Z");
        DateTimeOffset storedCapture = DateTimeOffset.Parse("2026-09-03T10:00:00Z");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            using (var store = new SqliteCodexObservatoryStore(database))
            {
                await store.ApplyCumulativeTokenObservationAsync(
                    Token("legacy", storedObserved, 100, 80, 10, 4, 110, Last(100, 80, 0, 10, 4, 110)) with
                    {
                        SourceEventAtUtc = null,
                        CapturedAtUtc = storedCapture,
                        SourceTimestampQuality = CodexSourceTimestampQuality.Missing,
                    },
                    CancellationToken.None);
                await store.UpsertContextObservationAsync(new CodexContextObservation("legacy-context", "session", null,
                    storedObserved, "model", 100, 1000, false, 10, storedCapture), CancellationToken.None);
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand downgrade = connection.CreateCommand();
                downgrade.CommandText = LegacyObservatoryFixture.RemoveProjectionCache + """
                                        DROP INDEX idx_native_token_source_time;
                                        DROP INDEX idx_native_token_session_source_time;
                                        DROP INDEX idx_context_source_time;
                                        ALTER TABLE codex_native_token_events DROP COLUMN source_event_at_utc;
                                        ALTER TABLE codex_native_token_events DROP COLUMN source_timestamp_quality;
                                        ALTER TABLE codex_counter_state DROP COLUMN last_source_event_at_utc;
                                        ALTER TABLE context_observations DROP COLUMN source_event_at_utc;
                                        ALTER TABLE context_observations DROP COLUMN source_timestamp_quality;
                                        UPDATE observatory_schema SET version = 8 WHERE component = 'codex-observatory';
                                        """;
                await downgrade.ExecuteNonQueryAsync();
            }

            using (var migrated = new SqliteCodexObservatoryStore(database))
            {
                await migrated.InitializeAsync(CancellationToken.None);
            }
            using (var reopened = new SqliteCodexObservatoryStore(database))
            {
                await reopened.InitializeAsync(CancellationToken.None);
                CodexContextObservation context = Assert.Single(await reopened.GetContextObservationsAsync("session", 10, CancellationToken.None));
                Assert.Equal(storedObserved, context.ObservedAtUtc);
                Assert.Equal(storedCapture, context.CapturedAtUtc);
                Assert.Null(context.SourceEventAtUtc);
                Assert.Equal(CodexSourceTimestampQuality.LegacyUnverified, context.SourceTimestampQuality);
            }

            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await verify.OpenAsync();
            SqliteCommand check = verify.CreateCommand();
            check.CommandText = "SELECT observed_at_utc,captured_at_utc,source_event_at_utc,source_timestamp_quality FROM codex_native_token_events WHERE source_event_id='legacy';";
            await using SqliteDataReader row = await check.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(storedObserved.ToString("O"), row.GetString(0));
            Assert.Equal(storedCapture.ToString("O"), row.GetString(1));
            Assert.True(row.IsDBNull(2));
            Assert.Equal("LegacyUnverified", row.GetString(3));
            await row.DisposeAsync();
            check.CommandText = "SELECT last_source_event_at_utc,total_tokens FROM codex_counter_state;";
            await using SqliteDataReader counter = await check.ExecuteReaderAsync();
            Assert.True(await counter.ReadAsync());
            Assert.True(counter.IsDBNull(0));
            Assert.Equal(110, counter.GetInt64(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Store_PersistsReducerDeltasAndStateAtomicallyAcrossRegressionResetAndReplay()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-counter-reducer-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset observed = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

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

            CodexObservatorySummary summary = await store.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(173, summary.NativeTokens.ReportedTotal);
            Assert.Equal(summary.NativeTokens.ReportedTotal, summary.NativeTokens.DisjointTotal);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();
            SqliteCommand state = connection.CreateCommand();
            state.CommandText = "SELECT counter_epoch, total_tokens FROM codex_counter_state WHERE session_id = 'session';";
            await using SqliteDataReader reader = await state.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(11, reader.GetInt64(1));

            SqliteCommand events = connection.CreateCommand();
            events.CommandText = "SELECT COUNT(*) FROM codex_native_token_events WHERE session_id = 'session';";
            Assert.Equal(5L, Convert.ToInt64(await events.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Store_CountsLastOnlyRowsAndPersistsSyntheticBaselineMarker()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-last-only-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        DateTimeOffset observed = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(database);

            await store.ApplyCumulativeTokenObservationAsync(
                new CodexTokenCountObservation(
                    "last-only",
                    "session.jsonl",
                    "session",
                    "session",
                    observed,
                    "model",
                    "high",
                    null,
                    Last(10, 6, 0, 4, 1, 14)),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                new CodexTokenCountObservation(
                    "cumulative",
                    "session.jsonl",
                    "session",
                    "session",
                    observed.AddSeconds(1),
                    "model",
                    "high",
                    new CodexTokenUsageSnapshot(25, 16, 0, 8, 2, 33)),
                CancellationToken.None);

            CodexObservatorySummary summary = await store.GetSummaryAsync(CancellationToken.None);
            await using (var debugConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                await debugConnection.OpenAsync();
                SqliteCommand debug = debugConnection.CreateCommand();
                debug.CommandText = "SELECT reported_total_tokens FROM codex_native_token_events ORDER BY observed_at_utc;";
                await using SqliteDataReader debugReader = await debug.ExecuteReaderAsync();
                var values = new List<long>();
                while (await debugReader.ReadAsync()) values.Add(debugReader.GetInt64(0));
                Assert.Equal("14,19", string.Join(',', values));
            }
            Assert.Equal(33, summary.NativeTokens.ReportedTotal);
            Assert.Equal(summary.NativeTokens.ReportedTotal, summary.NativeTokens.DisjointTotal);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();
            SqliteCommand state = connection.CreateCommand();
            state.CommandText = "SELECT has_cumulative_baseline FROM codex_counter_state WHERE session_id = 'session';";
            Assert.Equal(1L, Convert.ToInt64(await state.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
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
        CodexTokenUsageSnapshot? last)
    {
        return new CodexCumulativeTokenObservation(
            id,
            "session.jsonl",
            "session",
            "session",
            observed,
            "model",
            "high",
            input,
            cached,
            0,
            output,
            reasoning,
            total,
            last);
    }

    private static CodexTokenUsageSnapshot Last(
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        long total)
    {
        return new CodexTokenUsageSnapshot(input, cached, cacheWrite, output, reasoning, total);
    }
}
