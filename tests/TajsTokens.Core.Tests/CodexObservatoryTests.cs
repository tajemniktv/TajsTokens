using System.Text;
using TajsTokens.Core.Enums;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class CodexObservatoryTests
{
    private const string RootId = "11111111-1111-4111-8111-111111111111";
    private const string ChildId = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public async Task SanitizedFixtures_ReconstructCounterEpochsAndExcludeInheritedChildHistory()
    {
        var directory = CreateTempDirectory();
        try
        {
            var database = Path.Combine(directory, "telemetry.db");
            var rootPath = CopyFixture("root-counter-reset.jsonl", directory, RootId);
            var childPath = CopyFixture("child-inherited-prefix.jsonl", directory, ChildId);
            var baseRepository = new SqliteTelemetryRepository(database);
            await baseRepository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(
                new FileSystemCodexSessionEventProvider(),
                baseRepository,
                observatory);

            Assert.True(await ingestion.IngestAsync(rootPath, CancellationToken.None) > 0);
            Assert.True(await ingestion.IngestAsync(childPath, CancellationToken.None) > 0);

            var sessions = await observatory.GetSessionOverviewsAsync(20, CancellationToken.None);
            var root = Assert.Single(sessions, item => item.SessionId == RootId);
            var child = Assert.Single(sessions, item => item.SessionId == ChildId);

            Assert.Equal(1_870, root.NativeTokens.ReportedTotal);
            Assert.Equal(root.NativeTokens.ReportedTotal, root.NativeTokens.DisjointTotal);
            Assert.Equal(350, root.NativeTokens.UncachedInput);
            Assert.Equal(1_350, root.NativeTokens.CacheRead);
            Assert.Equal(105, root.NativeTokens.NonReasoningOutput);
            Assert.Equal(65, root.NativeTokens.ReasoningOutput);
            Assert.Equal(1, root.CompactionCount);
            Assert.InRange(root.PeakContextPercent!.Value, 96.7, 96.8);

            // The copied parent prefix in the child fixture contains >1M bogus inherited tokens.
            // Ownership is established from the UUID in the rollout filename, so only child work counts.
            Assert.Equal(550, child.NativeTokens.ReportedTotal);
            Assert.Equal(RootId, child.ParentSessionId);
            Assert.Equal("Bernoulli", child.DisplayName);

            var relationships = await observatory.GetAgentRelationshipsAsync(CancellationToken.None);
            Assert.Contains(relationships, item => item.ParentAgentId == RootId && item.ChildAgentId == ChildId);

            var quota = await baseRepository.GetRecentQuotaSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 20, CancellationToken.None);
            Assert.Contains(quota, item => item.Source.StartsWith("codex-rollout", StringComparison.Ordinal) && item.UsedPercent == 25);

            // Replay after the checkpoint must not duplicate native/accounting/storage telemetry.
            Assert.Equal(0, await ingestion.IngestAsync(rootPath, CancellationToken.None));
            var replayed = Assert.Single(await observatory.GetSessionOverviewsAsync(20, CancellationToken.None), item => item.SessionId == RootId);
            Assert.Equal(1_870, replayed.NativeTokens.ReportedTotal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedAndLargeRecords_DoNotPersistPayloadContentOrBlockFutureRecords()
    {
        var directory = CreateTempDirectory();
        try
        {
            var database = Path.Combine(directory, "telemetry.db");
            var rollout = CopyFixture("root-counter-reset.jsonl", directory, RootId);
            var baseRepository = new SqliteTelemetryRepository(database);
            await baseRepository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, observatory);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            await File.AppendAllTextAsync(rollout, "{ definitely-not-json }\n");
            Assert.Equal(0, await ingestion.IngestAsync(rollout, CancellationToken.None));

            var secretMarker = "SHOULD_NEVER_REACH_SQLITE_" + Guid.NewGuid().ToString("N");
            var filler = new string('x', 2 * 1024 * 1024);
            var safeRecord = "{\"timestamp\":\"2026-08-31T00:02:00Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"output\":\"" +
                             secretMarker + filler + "\"}}\n";
            await File.AppendAllTextAsync(rollout, safeRecord);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            var storage = await observatory.GetRolloutStorageAsync(10, CancellationToken.None);
            var item = Assert.Single(storage, value => value.SessionId == RootId);
            Assert.True(item.LargestRecordBytes > 2 * 1024 * 1024);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var databaseBytes = await File.ReadAllBytesAsync(database);
            var databaseText = Encoding.UTF8.GetString(databaseBytes);
            Assert.DoesNotContain(secretMarker, databaseText, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnterminatedRecord_IsNotCheckpointedUntilCompleted()
    {
        var directory = CreateTempDirectory();
        try
        {
            var database = Path.Combine(directory, "telemetry.db");
            var rollout = CopyFixture("root-counter-reset.jsonl", directory, RootId);
            var baseRepository = new SqliteTelemetryRepository(database);
            await baseRepository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), baseRepository, observatory);
            await ingestion.IngestAsync(rollout, CancellationToken.None);

            var before = await baseRepository.GetCheckpointAsync(rollout, CancellationToken.None);
            Assert.NotNull(before);
            await File.AppendAllTextAsync(rollout, "{\"timestamp\":\"2026-08-31T00:03:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}");
            Assert.Equal(0, await ingestion.IngestAsync(rollout, CancellationToken.None));
            var incomplete = await baseRepository.GetCheckpointAsync(rollout, CancellationToken.None);
            Assert.Equal(before!.LastByteOffset, incomplete!.LastByteOffset);

            await File.AppendAllTextAsync(rollout, "}\n");
            Assert.True(await ingestion.IngestAsync(rollout, CancellationToken.None) > 0);
            var completed = await baseRepository.GetCheckpointAsync(rollout, CancellationToken.None);
            Assert.True(completed!.LastByteOffset > before.LastByteOffset);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CopyFixture(string fixtureName, string directory, string sessionId)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodexRollouts", fixtureName);
        var destination = Path.Combine(directory, $"rollout-2026-08-31T00-00-00-{sessionId}.jsonl");
        File.Copy(source, destination);
        return destination;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Observatory.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
