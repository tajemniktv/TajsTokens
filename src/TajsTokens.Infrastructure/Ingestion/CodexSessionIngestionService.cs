using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class CodexSessionIngestionService(
    ICodexSessionEventProvider sessionEventProvider,
    ISessionIngestionCheckpointStore checkpointStore) : ICodexSessionIngestionService
{
    private const string ParserVersion = "boundary-v2";

    public async Task<int> IngestAsync(string filePath, CancellationToken cancellationToken)
    {
        var existing = await checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        var fromOffset = existing is not null && string.Equals(existing.ParserVersion, ParserVersion, StringComparison.Ordinal)
            ? existing.LastByteOffset
            : 0;

        var recordsScanned = 0;
        var lastCompleteRecordOffset = fromOffset;

        await foreach (var record in sessionEventProvider.ReadNewJsonLinesAsync(filePath, fromOffset, cancellationToken))
        {
            // This bootstrap pass deliberately validates complete record boundaries only. It does not
            // persist raw Codex transcript payloads. A future typed parser will use a new parser version,
            // causing a safe re-scan from byte zero and only checkpointing normalized committed telemetry.
            recordsScanned++;
            lastCompleteRecordOffset = record.EndByteOffset;
        }

        await checkpointStore.SaveCheckpointAsync(
            new FileIngestionCheckpoint(
                filePath,
                lastCompleteRecordOffset,
                DateTimeOffset.UtcNow,
                existing?.LastSessionId,
                ParserVersion),
            cancellationToken);

        return recordsScanned;
    }
}
