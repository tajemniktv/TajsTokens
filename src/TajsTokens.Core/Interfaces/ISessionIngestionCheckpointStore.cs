using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ISessionIngestionCheckpointStore
{
    Task<FileIngestionCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken);
    Task SaveCheckpointAsync(FileIngestionCheckpoint checkpoint, CancellationToken cancellationToken);
}
