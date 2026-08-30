using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class CodexSessionIngestionService(
    ICodexSessionEventProvider sessionEventProvider,
    ISessionIngestionCheckpointStore checkpointStore,
    ITelemetryRepository telemetryRepository) : ICodexSessionIngestionService
{
    public async Task<int> IngestAsync(string filePath, CancellationToken cancellationToken)
    {
        var checkpoint = await checkpointStore.GetCheckpointAsync(filePath, cancellationToken)
            ?? new FileIngestionCheckpoint(filePath, 0, DateTimeOffset.UtcNow, null);

        var newLines = await sessionEventProvider.ReadNewJsonLinesAsync(filePath, checkpoint.LastByteOffset, cancellationToken);

        var eventsIngested = 0;
        foreach (var line in newLines)
        {
            eventsIngested++;
            var eventId = $"{Path.GetFileName(filePath)}-{checkpoint.LastByteOffset + eventsIngested}";
            await telemetryRepository.AddUsageEventAsync(
                new UsageEvent(eventId, "unknown-session", DateTimeOffset.UtcNow, "jsonl.raw", line, null),
                cancellationToken);
        }

        long newOffset = checkpoint.LastByteOffset;
        if (File.Exists(filePath))
        {
            newOffset = new FileInfo(filePath).Length;
        }

        await checkpointStore.SaveCheckpointAsync(
            checkpoint with { LastByteOffset = newOffset, UpdatedAtUtc = DateTimeOffset.UtcNow },
            cancellationToken);

        return eventsIngested;
    }
}
