// Taj's Tokens | CodexObservatoryTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexObservatoryTests
{
    private const string RootId = "11111111-1111-4111-8111-111111111111";
    private const string ChildId = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public async Task SanitizedFixtures_ReconstructCounterEpochsAndExcludeInheritedChildHistory()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            string rootPath = CopyFixture("root-counter-reset.jsonl", directory, RootId);
            string childPath = CopyFixture("child-inherited-prefix.jsonl", directory, ChildId);
            var baseRepository = new SqliteTelemetryRepository(database);
            await baseRepository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(
                new FileSystemCodexSessionEventProvider(),
                baseRepository,
                observatory);

            Assert.True((await ingestion.IngestAsync(rootPath, CancellationToken.None)).RecordsNormalized > 0);
            Assert.True((await ingestion.IngestAsync(childPath, CancellationToken.None)).RecordsNormalized > 0);

            IReadOnlyList<CodexSessionOverview> sessions = await observatory.GetSessionOverviewsAsync(20, CancellationToken.None);
            CodexSessionOverview root = Assert.Single(sessions, item => item.SessionId == RootId);
            CodexSessionOverview child = Assert.Single(sessions, item => item.SessionId == ChildId);

            Assert.Equal(1_650, root.NativeTokens.ReportedTotal);
            Assert.Equal(root.NativeTokens.ReportedTotal, root.NativeTokens.DisjointTotal);
            Assert.Equal(300, root.NativeTokens.UncachedInput);
            Assert.Equal(1_200, root.NativeTokens.CacheRead);
            Assert.Equal(90, root.NativeTokens.NonReasoningOutput);
            Assert.Equal(60, root.NativeTokens.ReasoningOutput);
            Assert.Equal(1, root.CompactionCount);
            Assert.InRange(root.PeakContextPercent!.Value, 96.7, 96.8);

            Assert.Equal(550, child.NativeTokens.ReportedTotal);
            Assert.Equal(RootId, child.ParentSessionId);
            Assert.Equal("Bernoulli", child.DisplayName);

            IReadOnlyList<AgentRelationship> relationships = await observatory.GetAgentRelationshipsAsync(CancellationToken.None);
            Assert.Contains(relationships, item => item.ParentAgentId == RootId && item.ChildAgentId == ChildId);

            IReadOnlyList<QuotaSnapshot> quota = await baseRepository.GetRecentQuotaSnapshotsAsync(
                QuotaWindowKind.FiveHour,
                "codex",
                "default",
                20,
                CancellationToken.None);
            Assert.Contains(quota, item => item.Source.StartsWith("codex-rollout", StringComparison.Ordinal) && item.UsedPercent == 25);

            CodexIngestionResult replay = await ingestion.IngestAsync(rootPath, CancellationToken.None);
            Assert.Equal(0, replay.RecordsScanned);
            CodexSessionOverview replayed = Assert.Single(
                await observatory.GetSessionOverviewsAsync(20, CancellationToken.None),
                item => item.SessionId == RootId);
            Assert.Equal(1_650, replayed.NativeTokens.ReportedTotal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MalformedAndLargeRecords_DoNotPersistPayloadContentOrBlockFutureRecords()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            string rollout = CopyFixture("root-counter-reset.jsonl", directory, RootId);
            var baseRepository = new SqliteTelemetryRepository(database);
            await baseRepository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, observatory);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            await File.AppendAllTextAsync(rollout, "{ definitely-not-json }\n");
            CodexIngestionResult malformed = await ingestion.IngestAsync(rollout, CancellationToken.None);
            Assert.Equal(1, malformed.RecordsScanned);
            Assert.Equal(0, malformed.RecordsNormalized);

            string secretMarker = "SHOULD_NEVER_REACH_SQLITE_" + Guid.NewGuid().ToString("N");
            string filler = new('x', 2 * 1024 * 1024);
            string safeRecord =
                "{\"timestamp\":\"2026-08-31T00:02:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"output\":\"" +
                secretMarker + filler + "\"}}\n";
            await File.AppendAllTextAsync(rollout, safeRecord);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            IReadOnlyList<CodexRolloutStorageSummary> storage = await observatory.GetRolloutStorageAsync(10, CancellationToken.None);
            CodexRolloutStorageSummary item = Assert.Single(storage, value => value.SessionId == RootId);
            Assert.True(item.LargestRecordBytes > 2 * 1024 * 1024);

            SqliteConnection.ClearAllPools();
            byte[] databaseBytes = await File.ReadAllBytesAsync(database);
            string databaseText = Encoding.UTF8.GetString(databaseBytes);
            Assert.DoesNotContain(secretMarker, databaseText, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnterminatedRecord_IsNotCheckpointedUntilCompleted()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            string rollout = CopyFixture("root-counter-reset.jsonl", directory, RootId);
            var baseRepository = new SqliteTelemetryRepository(database);
            await baseRepository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, observatory);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            FileIngestionCheckpoint? before = await baseRepository.GetCheckpointAsync(rollout, CancellationToken.None);
            Assert.NotNull(before);
            await File.AppendAllTextAsync(
                rollout,
                "{\"timestamp\":\"2026-08-31T00:03:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}");
            Assert.Equal(0, (await ingestion.IngestAsync(rollout, CancellationToken.None)).RecordsScanned);
            FileIngestionCheckpoint? incomplete = await baseRepository.GetCheckpointAsync(rollout, CancellationToken.None);
            Assert.Equal(before!.LastByteOffset, incomplete!.LastByteOffset);

            await File.AppendAllTextAsync(rollout, "}\n");
            Assert.True((await ingestion.IngestAsync(rollout, CancellationToken.None)).RecordsNormalized > 0);
            FileIngestionCheckpoint? completed = await baseRepository.GetCheckpointAsync(rollout, CancellationToken.None);
            Assert.True(completed!.LastByteOffset > before.LastByteOffset);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ReplayedCommittedEvent_DoesNotRegressCounterState()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            DateTimeOffset start = DateTimeOffset.Parse("2026-08-31T00:00:00Z");

            await store.ApplyCumulativeTokenObservationAsync(Token("A", "one.jsonl", RootId, start, 10), CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("B", "one.jsonl", RootId, start.AddSeconds(1), 20),
                CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(Token("A", "one.jsonl", RootId, start, 10), CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("C", "one.jsonl", RootId, start.AddSeconds(2), 21),
                CancellationToken.None);

            CodexObservatorySummary summary = await store.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(21, summary.NativeTokens.ReportedTotal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SameSessionAcrossFiles_ContinuesCumulativeCounter()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            DateTimeOffset start = DateTimeOffset.Parse("2026-08-31T00:00:00Z");

            await store.ApplyCumulativeTokenObservationAsync(Token("A", "active.jsonl", RootId, start, 100), CancellationToken.None);
            await store.ApplyCumulativeTokenObservationAsync(
                Token("B", "archive.jsonl", RootId, start.AddMinutes(1), 120),
                CancellationToken.None);

            CodexObservatorySummary summary = await store.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(120, summary.NativeTokens.ReportedTotal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Resume_RestoresAgentModelReasoningAndContextMetadata()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            string rollout = Path.Combine(directory, $"rollout-2026-08-31T00-00-00-{RootId}.jsonl");
            var lines = new List<string>
            {
                $"{{\"timestamp\":\"2026-08-31T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{RootId}\",\"agent_nickname\":\"Bernoulli\",\"cwd\":\"C:/repo\"}}}}",
                "{\"timestamp\":\"2026-08-31T00:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.6-luna\",\"reasoning_effort\":\"xhigh\",\"model_context_window\":258400}}",
            };
            for (int i = 0; i < 126; i++)
            {
                lines.Add(
                    $"{{\"timestamp\":\"2026-08-31T00:00:{Math.Min(59, i + 2):00}Z\",\"type\":\"world_state\",\"payload\":{{\"n\":{i}}}}}");
            }
            await File.WriteAllTextAsync(rollout, string.Join("\n", lines) + "\n");

            SqliteTelemetryRepository baseRepository = await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, store);
            Assert.Equal(128, (await ingestion.IngestAsync(rollout, CancellationToken.None)).RecordsScanned);

            await File.AppendAllTextAsync(
                rollout,
                "{\"timestamp\":\"2026-08-31T00:03:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":80,\"cache_write_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":4,\"total_tokens\":110},\"last_token_usage\":{\"input_tokens\":90000}}}}\n");
            CodexIngestionResult resumed = await ingestion.IngestAsync(rollout, CancellationToken.None);
            Assert.Equal(1, resumed.RecordsScanned);

            CodexSessionOverview session = Assert.Single(await store.GetSessionOverviewsAsync(10, CancellationToken.None));
            Assert.Equal("Bernoulli", session.DisplayName);
            Assert.Equal("gpt-5.6-luna", session.Model);
            CodexContextObservation context = Assert.Single(await store.GetContextObservationsAsync(RootId, 10, CancellationToken.None));
            Assert.Equal(258_400, context.ContextWindowTokens);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();
            SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT reasoning_effort FROM codex_native_token_events WHERE session_id = $session LIMIT 1;";
            command.Parameters.AddWithValue("$session", RootId);
            Assert.Equal("xhigh", (string?)await command.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RolloutFileIdentity_FollowsMoveWithoutDoubleCountingStorage()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            await store.UpsertRolloutFileAsync(
                "stable-id",
                "C:/active/a.jsonl",
                RootId,
                100,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            await store.UpsertRolloutFileAsync(
                "stable-id",
                "C:/archive/a.jsonl",
                RootId,
                100,
                DateTimeOffset.UtcNow.AddSeconds(1),
                CancellationToken.None);

            IReadOnlyList<CodexRolloutStorageSummary> files = await store.GetRolloutStorageAsync(10, CancellationToken.None);
            CodexRolloutStorageSummary file = Assert.Single(files);
            Assert.StartsWith("a.jsonl [", file.FilePath);
            Assert.DoesNotContain("C:/archive", file.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(100, (await store.GetSummaryAsync(CancellationToken.None)).RolloutBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NoUuidRollout_DoesNotClaimFirstInheritedSessionMeta()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            string rollout = Path.Combine(directory, "rollout-without-owner-id.jsonl");
            await File.WriteAllTextAsync(
                rollout,
                $"{{\"timestamp\":\"2026-08-31T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{RootId}\"}}}}\n" +
                $"{{\"timestamp\":\"2026-08-31T00:00:01Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{ChildId}\",\"parent_thread_id\":\"{RootId}\"}}}}\n" +
                "{\"timestamp\":\"2026-08-31T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":80,\"cache_write_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":4,\"total_tokens\":110}}}}\n");

            SqliteTelemetryRepository baseRepository = await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, store);
            CodexIngestionResult result = await ingestion.IngestAsync(rollout, CancellationToken.None);

            Assert.Equal(3, result.RecordsScanned);
            Assert.Equal(0, result.RecordsNormalized);
            Assert.Equal(0, (await store.GetSummaryAsync(CancellationToken.None)).SessionCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task HeadlineSummary_IsIndependentOfDetailListLimit()
    {
        string directory = CreateTempDirectory();
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (string id in new[] { RootId, ChildId })
            {
                await store.UpsertSessionAsync(new CodexSession(id, id, "C:/repo", now, now, "active"), CancellationToken.None);
                await store.UpsertRolloutFileAsync("file-" + id, "C:/" + id + ".jsonl", id, 100, now, CancellationToken.None);
            }

            Assert.Single(await store.GetSessionOverviewsAsync(1, CancellationToken.None));
            CodexObservatorySummary summary = await store.GetSummaryAsync(CancellationToken.None);
            Assert.Equal(2, summary.SessionCount);
            Assert.Equal(200, summary.RolloutBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ObservatoryTables_DoNotPersistAbsoluteRolloutOrRepositoryPaths()
    {
        string directory = CreateTempDirectory();
        try
        {
            string marker = "PRIVATE_USER_PATH_" + Guid.NewGuid().ToString("N");
            string privateDirectory = Path.Combine(directory, marker);
            Directory.CreateDirectory(privateDirectory);
            string database = Path.Combine(directory, "telemetry.db");
            string rollout = Path.Combine(privateDirectory, $"rollout-2026-08-31T00-00-00-{RootId}.jsonl");
            string privateRepository = $"C:/Users/{marker}/source/TajsTokens";
            await File.WriteAllTextAsync(
                rollout,
                $"{{\"timestamp\":\"2026-08-31T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{RootId}\",\"agent_nickname\":\"Root\",\"cwd\":\"{privateRepository}\"}}}}\n" +
                "{\"timestamp\":\"2026-08-31T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":80,\"cache_write_input_tokens\":0,\"output_tokens\":10,\"reasoning_output_tokens\":4,\"total_tokens\":110}}}}\n");

            SqliteTelemetryRepository baseRepository = await InitializeBaseAsync(database);
            var store = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, store);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
            await connection.OpenAsync();
            SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                                  SELECT repository FROM sessions
                                  UNION ALL SELECT repository FROM codex_parser_state
                                  UNION ALL SELECT file_path FROM rollout_files
                                  UNION ALL SELECT file_path FROM rollout_records
                                  UNION ALL SELECT source_file FROM codex_native_token_events
                                  UNION ALL SELECT source_file FROM codex_counter_state;
                                  """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Assert.DoesNotContain(marker, reader.GetString(0), StringComparison.OrdinalIgnoreCase);
            }

            CodexSessionOverview session = Assert.Single(await store.GetSessionOverviewsAsync(10, CancellationToken.None));
            Assert.Equal("TajsTokens", session.Repository);
            CodexRolloutStorageSummary storage = Assert.Single(await store.GetRolloutStorageAsync(10, CancellationToken.None));
            Assert.StartsWith(Path.GetFileName(rollout) + " [", storage.FilePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static CodexCumulativeTokenObservation Token(
        string eventId,
        string file,
        string sessionId,
        DateTimeOffset observedAt,
        long total)
    {
        return new CodexCumulativeTokenObservation(
            eventId,
            file,
            sessionId,
            sessionId,
            observedAt,
            "gpt-5.6-luna",
            "xhigh",
            total,
            0,
            0,
            0,
            0,
            total);
    }

    private static async Task<SqliteTelemetryRepository> InitializeBaseAsync(string database)
    {
        var repository = new SqliteTelemetryRepository(database);
        await repository.InitializeAsync(CancellationToken.None);
        return repository;
    }

    private static string CopyFixture(string fixtureName, string directory, string sessionId)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodexRollouts", fixtureName);
        string destination = Path.Combine(directory, $"rollout-2026-08-31T00-00-00-{sessionId}.jsonl");
        File.Copy(source, destination);
        return destination;
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Observatory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}