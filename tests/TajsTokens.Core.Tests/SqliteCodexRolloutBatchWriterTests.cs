using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexRolloutBatchWriterTests
{
    [Fact]
    public async Task WriteBatchAsync_CommitsRecordsAndFileStateOncePerBatch()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-rollout-batch-");
        var database = Path.Combine(directory.FullName, "telemetry.db");
        var sourcePath = Path.Combine(directory.FullName, "private", "rollout.jsonl");

        try
        {
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            var observatory = new SqliteCodexObservatoryStore(database);
            await observatory.InitializeAsync(CancellationToken.None);
            var writer = new SqliteCodexRolloutRecordBatchWriter(database);
            var now = DateTimeOffset.UtcNow;

            await writer.WriteBatchAsync(
                "source-1",
                sourcePath,
                1_234,
                [
                    new CodexRolloutRecordMetadata("r1", "session-a", "token_count", 111, now),
                    new CodexRolloutRecordMetadata("r2", "session-a", "item_completed", 222, now.AddSeconds(1)),
                    new CodexRolloutRecordMetadata("r2", "session-a", "item_completed", 222, now.AddSeconds(1))
                ],
                CancellationToken.None);

            var storage = Assert.Single(await observatory.GetRolloutStorageAsync(10, CancellationToken.None));
            Assert.Equal("session-a", storage.SessionId);
            Assert.Equal(1_234, storage.SizeBytes);
            Assert.Equal(2, storage.RecordsSeen);
            Assert.Equal(222, storage.LargestRecordBytes);
            Assert.DoesNotContain(directory.FullName, storage.FilePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
