namespace TajsTokens.Infrastructure.Ingestion;

internal sealed record CodexRolloutRecordMetadata(
    string SourceRecordId,
    string? SessionId,
    string EventClass,
    long RecordBytes,
    DateTimeOffset ObservedAtUtc);

internal interface ICodexRolloutRecordBatchWriter
{
    Task WriteBatchAsync(
        string sourceIdentity,
        string filePath,
        long fileSizeBytes,
        IReadOnlyList<CodexRolloutRecordMetadata> records,
        CancellationToken cancellationToken);
}
