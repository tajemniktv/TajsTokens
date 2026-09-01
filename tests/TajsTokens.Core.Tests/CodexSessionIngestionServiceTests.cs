using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class CodexSessionIngestionServiceTests
{
    [Fact]
    public async Task IngestAsync_WhenSourceIsAppended_ResumesFromCheckpoint()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-");
        var filePath = Path.Combine(directory.FullName, "rollout.jsonl");

        try
        {
            await File.WriteAllTextAsync(filePath, "{\"n\":1}\n");
            var checkpoints = new InMemoryCheckpointStore();
            var service = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), checkpoints);

            Assert.Equal(1, (await service.IngestAsync(filePath, CancellationToken.None)).RecordsScanned);
            var firstIdentity = checkpoints.Checkpoint!.SourceIdentity;

            await File.AppendAllTextAsync(filePath, "{\"n\":2}\n");
            Assert.Equal(1, (await service.IngestAsync(filePath, CancellationToken.None)).RecordsScanned);
            Assert.Equal(firstIdentity, checkpoints.Checkpoint!.SourceIdentity);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task IngestAsync_WhenPathIsReplacedByLargerFile_ResetsCheckpoint()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-ingestion-");
        var filePath = Path.Combine(directory.FullName, "rollout.jsonl");
        var replacementPath = Path.Combine(directory.FullName, "replacement.jsonl");

        try
        {
            await File.WriteAllTextAsync(filePath, "{\"old\":1}\n");
            var checkpoints = new InMemoryCheckpointStore();
            var service = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), checkpoints);

            Assert.Equal(1, (await service.IngestAsync(filePath, CancellationToken.None)).RecordsScanned);
            var originalIdentity = checkpoints.Checkpoint!.SourceIdentity;

            await File.WriteAllTextAsync(replacementPath, "{\"replacement\":true,\"padding\":\"xxxxxxxxxxxxxxxx\"}\n");
            File.Move(replacementPath, filePath, overwrite: true);

            Assert.Equal(1, (await service.IngestAsync(filePath, CancellationToken.None)).RecordsScanned);
            Assert.NotEqual(originalIdentity, checkpoints.Checkpoint!.SourceIdentity);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class InMemoryCheckpointStore : ISessionIngestionCheckpointStore
    {
        public FileIngestionCheckpoint? Checkpoint { get; private set; }

        public Task<FileIngestionCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(Checkpoint is not null && string.Equals(Checkpoint.FilePath, filePath, StringComparison.Ordinal)
                ? Checkpoint
                : null);

        public Task SaveCheckpointAsync(FileIngestionCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            Checkpoint = checkpoint;
            return Task.CompletedTask;
        }
    }
}
