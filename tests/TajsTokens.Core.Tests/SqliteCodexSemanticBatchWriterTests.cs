// Taj's Tokens | SqliteCodexSemanticBatchWriterTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexIngestionBatchWriterTests
{
    [Fact]
    public async Task ReplacementRetiresDerivedResetsAndRefreshRestoresOnlySurvivingEvidence()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PROJECT.md"))) root = root.Parent;
        DirectoryInfo directory = Directory.CreateDirectory(
            Path.Combine(root!.FullName, ".codex", "temp", "reset-replacement-" + Guid.NewGuid().ToString("N")));
        try
        {
            string database = Path.Combine(directory.FullName, "telemetry.db");
            string file = Path.Combine(directory.FullName, "rollout.jsonl");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeIntelligenceAsync(default);
            var store = new SqliteCodexObservatoryStore(database);
            await store.InitializeAsync(default);
            var writer = new SqliteCodexIngestionBatchWriter(database, store);
            DateTimeOffset at = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

            ParsedRolloutRecord Row(string id, string generation, DateTimeOffset time, double used)
            {
                ParsedRolloutRecord record = BuildRecord(id, file, time, 100, 40, 20, 5, used);
                return record with
                {
                    QuotaSnapshots = record.QuotaSnapshots.Select(q => q with
                    {
                        ObservationId = id,
                        SourceIdentity = generation,
                        SessionId = "session-a",
                        HasSourceTimestamp = true,
                        ResetsAtUtc = at.AddHours(5),
                    }).ToArray(),
                };
            }

            var other = new QuotaSnapshot(
                QuotaWindowKind.FiveHour,
                at,
                90,
                300,
                at.AddHours(5),
                "codex",
                "default",
                "codex-app-server:codex",
                "unrelated") { HasSourceTimestamp = true };
            await repository.UpsertQuotaSnapshotAsync(other, default);
            await repository.UpsertQuotaSnapshotAsync(other with { CapturedAtUtc = at.AddMinutes(1), UsedPercent = 5 }, default);
            await writer.WriteBatchAsync("A", file, 256, [Row("a1", "A", at, 90), Row("a2", "A", at.AddMinutes(1), 5)], default);
            var intelligence = new SqliteIntelligenceService(database, repository);
            await intelligence.RefreshAsync(default);
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            Assert.Equal(2, await CountAsync(connection, "quota_reset_events"));
            await writer.WriteBatchAsync("B", file, 128, [Row("b1", "B", at.AddMinutes(2), 20)], default);
            // Invalidation commits with retirement, before any intelligence refresh can run.
            Assert.Equal(0, await CountAsync(connection, "quota_reset_events"));
            Assert.Equal(3, await CountAsync(connection, "quota_snapshots"));
            await intelligence.RefreshAsync(default);
            Assert.Equal(1, await CountAsync(connection, "quota_reset_events"));
            SqliteCommand owner = connection.CreateCommand();
            owner.CommandText = "SELECT account_key FROM quota_reset_events;";
            Assert.Equal("unrelated", await owner.ExecuteScalarAsync());
            Assert.Equal(0, (await intelligence.RefreshAsync(default)).ResetEventsDetected);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData("C:/x")]
    [InlineData("C:/same")]
    [InlineData("C:/a/much/longer/workspace")]
    public async Task InPlaceWorkspaceRewrite_ReplacesConsumedProjectionAndRemainsAppendable(string newPath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PROJECT.md"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root required.");
        DirectoryInfo directory =
            Directory.CreateDirectory(Path.Combine(root.FullName, ".codex", "temp", "rewrite-" + Guid.NewGuid().ToString("N")));
        try
        {
            const string id = "11111111-1111-4111-8111-111111111111";
            string file = Path.Combine(directory.FullName, $"rollout-2026-09-01T10-00-00-{id}.jsonl");

            // Keep the first record unchanged: the consumed prefix, not just its header, must be checked.
            static string Json(object value)
            {
                return JsonSerializer.Serialize(value) + "\n";
            }

            string header = Json(new { timestamp = "2026-09-01T10:00:00Z", type = "session_meta", payload = new { id } });

            string Body(string cwd)
            {
                return Json(
                    new { timestamp = "2026-09-01T10:00:01Z", type = "turn_context", payload = new { cwd, model = "gpt-5.6-luna" } });
            }

            string Tokens(int input, string time)
            {
                return Json(
                    new
                    {
                        timestamp = $"2026-09-01T{time}Z",
                        type = "event_msg",
                        payload = new
                        {
                            type = "token_count",
                            info = new
                            {
                                total_token_usage = new
                                {
                                    input_tokens = input,
                                    cached_input_tokens = 0,
                                    cache_write_input_tokens = 0,
                                    output_tokens = 10,
                                    reasoning_output_tokens = 0,
                                    total_tokens = input + 10,
                                },
                            },
                        },
                    });
            }

            string database = Path.Combine(directory.FullName, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var store = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(
                new FileSystemCodexSessionEventProvider(),
                repository,
                store,
                new SqliteCodexIngestionBatchWriter(database, store));
            await File.WriteAllTextAsync(file, header + Body("C:/repo") + Tokens(100, "10:00:02"));
            await ingestion.IngestAsync(file, CancellationToken.None);
            string? oldIdentity = (await repository.GetCheckpointAsync(file, CancellationToken.None))!.SourceIdentity;
            ingestion = new CodexSessionIngestionService(
                new FileSystemCodexSessionEventProvider(),
                repository,
                store,
                new SqliteCodexIngestionBatchWriter(database, store));
            await File.WriteAllTextAsync(file, header + Body(newPath) + Tokens(100, "10:00:02"));
            Assert.Equal(3, (await ingestion.IngestAsync(file, CancellationToken.None)).RecordsScanned);
            Assert.NotEqual(oldIdentity, (await repository.GetCheckpointAsync(file, CancellationToken.None))!.SourceIdentity);
            Assert.Equal(0, (await ingestion.IngestAsync(file, CancellationToken.None)).RecordsScanned);
            await File.AppendAllTextAsync(file, Tokens(150, "10:00:03"));
            Assert.Equal(1, (await ingestion.IngestAsync(file, CancellationToken.None)).RecordsScanned);
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            Assert.Equal(4, await CountAsync(connection, "rollout_records"));
            Assert.Equal(2, await CountAsync(connection, "codex_workload_observations"));
            Assert.Equal(2, await CountAsync(connection, "codex_native_token_events"));
            CodexTokenAccountingSnapshot accounting =
                await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(160, Assert.Single(accounting.Usage).Breakdown.Total);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_PersistsSemanticAndStorageBatchAndKeepsTokenReplayIdempotent()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-batch-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        string sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexIngestionBatchWriter(database, observatory);
            DateTimeOffset start = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

            ParsedRolloutRecord first = BuildRecord(
                "event-1",
                sourcePath,
                start,
                100,
                40,
                20,
                5,
                10);
            ParsedRolloutRecord second = BuildRecord(
                "event-2",
                sourcePath,
                start.AddMinutes(1),
                180,
                70,
                35,
                9,
                12);

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

            CodexRolloutStorageSummary storage = Assert.Single(await observatory.GetRolloutStorageAsync(10, CancellationToken.None));
            Assert.Equal("session-a", storage.SessionId);
            Assert.Equal(1_234, storage.SizeBytes);
            Assert.Equal(2, storage.RecordsSeen);
            Assert.DoesNotContain(directory.FullName, storage.FilePath, StringComparison.OrdinalIgnoreCase);

            CodexObservatorySummary totals = await observatory.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(110, totals.NativeTokens.UncachedInput);
            Assert.Equal(70, totals.NativeTokens.CacheRead);
            Assert.Equal(26, totals.NativeTokens.NonReasoningOutput);
            Assert.Equal(9, totals.NativeTokens.ReasoningOutput);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriteBatchAsync_PreservesProviderQuotaWhenSourcesShareTimestamp(bool providerFirst)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-batch-authority-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        string sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");
        DateTimeOffset captured = DateTimeOffset.Parse("2026-08-31T10:00:00Z");
        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexIngestionBatchWriter(database, observatory);
            var previous = new QuotaSnapshot(
                QuotaWindowKind.FiveHour,
                captured.AddHours(-1),
                10,
                300,
                captured.AddHours(5),
                "codex",
                "codex",
                "codex-app-server:codex",
                "fixture-account") { HasSourceTimestamp = true };
            var current = new QuotaSnapshot(
                QuotaWindowKind.FiveHour,
                captured,
                20,
                300,
                captured.AddHours(5),
                "codex",
                "codex",
                "codex-app-server:codex",
                "fixture-account") { HasSourceTimestamp = true };
            await repository.UpsertQuotaSnapshotAsync(previous, CancellationToken.None);
            if (providerFirst)
            {
                await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);
                await writer.WriteBatchAsync(
                    "source-1",
                    sourcePath,
                    128,
                    [BuildRecord("rollout-1", sourcePath, captured, 100, 40, 20, 5, 99)],
                    CancellationToken.None);
            }
            else
            {
                await writer.WriteBatchAsync(
                    "source-1",
                    sourcePath,
                    128,
                    [BuildRecord("rollout-1", sourcePath, captured, 100, 40, 20, 5, 99)],
                    CancellationToken.None);
                await repository.UpsertQuotaSnapshotAsync(current, CancellationToken.None);
            }

            IReadOnlyList<QuotaSnapshot> snapshots = await repository.GetRecentQuotaSnapshotsAsync(
                QuotaWindowKind.FiveHour,
                "codex",
                "codex",
                10,
                CancellationToken.None);
            Assert.Single(snapshots);
            Assert.Equal(
                2,
                (await repository.GetRecentQuotaSnapshotsAsync(
                    QuotaWindowKind.FiveHour,
                    "codex",
                    "codex",
                    10,
                    CancellationToken.None,
                    accountKey: "fixture-account")).Count);

            var intelligence = new SqliteIntelligenceService(database, repository);
            IReadOnlyList<CurrentQuotaForecast> results = await intelligence.BuildAndPersistCurrentForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "codex", current, TelemetryHealthState.Live, current.CapturedAtUtc)],
                captured.AddHours(1),
                CancellationToken.None);
            CurrentQuotaForecast generation = Assert.Single(results);
            Assert.NotNull(generation.Forecast);
            Assert.Equal(10d, generation.Forecast!.BurnRatePercentPerHour);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task WriteBatchAsync_ReplacedPathRetiresOldTokenGenerationAndCounterState()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-replace-");
        string database = Path.Combine(directory.FullName, "telemetry.db");
        string sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexIngestionBatchWriter(database, observatory);
            DateTimeOffset start = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

            await writer.WriteBatchAsync(
                "source-old",
                sourcePath,
                2_000,
                [
                    BuildRecord("old-1", sourcePath, start, 100, 40, 20, 5, 10),
                    BuildRecord("old-2", sourcePath, start.AddMinutes(1), 180, 70, 35, 9, 12),
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

            SqliteCommand sourceCommand = connection.CreateCommand();
            sourceCommand.CommandText = "SELECT source_identity FROM rollout_files LIMIT 1;";
            Assert.Equal("source-new", (string?)await sourceCommand.ExecuteScalarAsync());

            CodexTokenAccountingSnapshot accounting = await new SqliteNativeCodexAccountingProvider(database)
                .GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(55, Assert.Single(accounting.Usage).Breakdown.Total);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
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
            [
                new QuotaSnapshot(
                    QuotaWindowKind.FiveHour,
                    timestamp,
                    usedPercent,
                    300,
                    timestamp.AddHours(5),
                    "codex",
                    "codex",
                    "codex-rollout:primary"),
            ],
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
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}