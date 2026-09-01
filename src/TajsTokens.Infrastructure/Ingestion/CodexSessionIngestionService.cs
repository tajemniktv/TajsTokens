using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class CodexSessionIngestionService : ICodexSessionIngestionService
{
    private const string BoundaryParserVersion = "boundary-v2";
    private const string TypedParserVersion = "typed-v3";
    private const int DurableBatchSize = 128;
    private readonly ICodexSessionEventProvider _sessionEventProvider;
    private readonly ISessionIngestionCheckpointStore _checkpointStore;
    private readonly ICodexObservatoryStore? _observatoryStore;
    private readonly ICodexRolloutRecordBatchWriter? _rolloutRecordBatchWriter;
    private readonly ICodexSemanticBatchWriter? _semanticBatchWriter;
    private readonly CodexRolloutParser _parser = new();

    public CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore)
        : this(sessionEventProvider, checkpointStore, null, null, null)
    {
    }

    public CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore,
        ICodexObservatoryStore? observatoryStore)
        : this(sessionEventProvider, checkpointStore, observatoryStore, null, null)
    {
    }

    internal CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore,
        ICodexObservatoryStore? observatoryStore,
        ICodexRolloutRecordBatchWriter? rolloutRecordBatchWriter,
        ICodexSemanticBatchWriter? semanticBatchWriter = null)
    {
        _sessionEventProvider = sessionEventProvider;
        _checkpointStore = checkpointStore;
        _observatoryStore = observatoryStore;
        _rolloutRecordBatchWriter = rolloutRecordBatchWriter;
        _semanticBatchWriter = semanticBatchWriter;
    }

    public async Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return CodexIngestionResult.Empty;
        }

        if (_observatoryStore is not null)
        {
            await _observatoryStore.InitializeAsync(cancellationToken);
        }

        var parserVersion = _observatoryStore is null ? BoundaryParserVersion : TypedParserVersion;
        var existing = await _checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        var sourceIdentity = GetSourceIdentity(filePath);
        var canResume = existing is not null &&
                        string.Equals(existing.ParserVersion, parserVersion, StringComparison.Ordinal) &&
                        existing.SourceIdentity is not null &&
                        string.Equals(existing.SourceIdentity, sourceIdentity, StringComparison.Ordinal);

        var fromOffset = canResume ? existing!.LastByteOffset : 0;
        if (new FileInfo(filePath).Length < fromOffset)
        {
            fromOffset = 0;
            canResume = false;
        }

        CodexParserResumeState? resumeState = null;
        if (canResume && _observatoryStore is not null && fromOffset > 0)
        {
            resumeState = await _observatoryStore.GetParserResumeStateAsync(sourceIdentity, cancellationToken);
            if (resumeState is null ||
                resumeState.ByteOffset != fromOffset ||
                (existing?.LastSessionId is not null &&
                 !string.Equals(existing.LastSessionId, resumeState.SessionId, StringComparison.OrdinalIgnoreCase)))
            {
                // A byte checkpoint without parser metadata cannot safely resume semantic parsing. A
                // one-time replay from zero is cheaper than overwriting established agent/model state.
                fromOffset = 0;
                canResume = false;
                resumeState = null;
            }
        }

        var state = new RolloutParseState(filePath, sourceIdentity, resumeState);
        var recordsScanned = 0;
        var normalizedRecords = 0;
        var lastCompleteRecordOffset = fromOffset;
        var pendingStorageRecords = _rolloutRecordBatchWriter is null
            ? null
            : new List<CodexRolloutRecordMetadata>(DurableBatchSize);
        var pendingSemanticRecords = _semanticBatchWriter is null
            ? null
            : new List<ParsedRolloutRecord>(DurableBatchSize);

        await foreach (var record in _sessionEventProvider.ReadNewJsonLinesAsync(filePath, fromOffset, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordsScanned++;

            if (_observatoryStore is not null)
            {
                ParsedRolloutRecord parsed;
                try
                {
                    parsed = _parser.Parse(record, state);
                }
                catch (JsonException)
                {
                    parsed = ParsedRolloutRecord.StorageOnly(
                        BuildSourceRecordId(sourceIdentity, record.StartByteOffset, record.EndByteOffset),
                        "malformed_json",
                        Math.Max(0, record.EndByteOffset - record.StartByteOffset),
                        DateTimeOffset.UtcNow,
                        state.OwnSessionId);
                }

                if (pendingSemanticRecords is not null)
                {
                    pendingSemanticRecords.Add(parsed);
                }
                else
                {
                    await PersistParsedTelemetryAsync(parsed, sourceIdentity, filePath, cancellationToken);
                }

                pendingStorageRecords?.Add(new CodexRolloutRecordMetadata(
                    parsed.SourceRecordId,
                    parsed.SessionId,
                    parsed.EventClass,
                    parsed.RecordBytes,
                    parsed.TimestampUtc));

                if (parsed.HasNormalizedTelemetry)
                {
                    normalizedRecords++;
                }
            }

            // This offset becomes durable only after the corresponding semantic + storage metadata
            // batches have committed. If either fails, no checkpoint is advanced and replay remains
            // safe/idempotent on the next run.
            lastCompleteRecordOffset = record.EndByteOffset;
            if (recordsScanned % DurableBatchSize == 0)
            {
                await FlushSemanticBatchAsync(
                    pendingSemanticRecords,
                    sourceIdentity,
                    filePath,
                    cancellationToken);
                await FlushStorageBatchAsync(
                    pendingStorageRecords,
                    sourceIdentity,
                    filePath,
                    cancellationToken);
                await SaveCheckpointAsync(
                    filePath,
                    lastCompleteRecordOffset,
                    state,
                    existing?.LastSessionId,
                    parserVersion,
                    sourceIdentity,
                    cancellationToken);
            }
        }

        await FlushSemanticBatchAsync(
            pendingSemanticRecords,
            sourceIdentity,
            filePath,
            cancellationToken);
        await FlushStorageBatchAsync(
            pendingStorageRecords,
            sourceIdentity,
            filePath,
            cancellationToken);
        await SaveCheckpointAsync(
            filePath,
            lastCompleteRecordOffset,
            state,
            existing?.LastSessionId,
            parserVersion,
            sourceIdentity,
            cancellationToken);

        var normalized = _observatoryStore is null ? recordsScanned : normalizedRecords;
        return new CodexIngestionResult(recordsScanned, normalized, state.OwnSessionId ?? existing?.LastSessionId);
    }

    private async Task FlushSemanticBatchAsync(
        List<ParsedRolloutRecord>? pendingRecords,
        string sourceIdentity,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (_semanticBatchWriter is null || pendingRecords is null || pendingRecords.Count == 0)
        {
            return;
        }

        await _semanticBatchWriter.WriteBatchAsync(pendingRecords, cancellationToken);

        if (_rolloutRecordBatchWriter is null && _observatoryStore is not null)
        {
            // Alternate/test compositions can opt into semantic batching without the production
            // rollout metadata batch writer. Retain storage metadata correctness in that shape.
            var fileSize = TryGetFileSize(filePath);
            foreach (var parsed in pendingRecords)
            {
                await _observatoryStore.RecordRolloutRecordAsync(
                    parsed.SourceRecordId,
                    sourceIdentity,
                    filePath,
                    parsed.SessionId,
                    parsed.EventClass,
                    parsed.RecordBytes,
                    fileSize,
                    parsed.TimestampUtc,
                    cancellationToken);
            }
        }

        pendingRecords.Clear();
    }

    private async Task PersistParsedTelemetryAsync(
        ParsedRolloutRecord parsed,
        string sourceIdentity,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (_observatoryStore is null)
        {
            return;
        }

        if (parsed.Session is not null)
        {
            await _observatoryStore.UpsertSessionAsync(parsed.Session, cancellationToken);
        }
        if (parsed.Agent is not null)
        {
            await _observatoryStore.UpsertAgentAsync(parsed.Agent, cancellationToken);
        }
        if (parsed.Relationship is not null)
        {
            await _observatoryStore.UpsertAgentRelationshipAsync(parsed.Relationship, cancellationToken);
        }
        if (parsed.UsageEvent is not null)
        {
            await _observatoryStore.UpsertUsageEventAsync(parsed.UsageEvent, cancellationToken);
        }
        if (parsed.TokenObservation is not null)
        {
            await _observatoryStore.ApplyCumulativeTokenObservationAsync(parsed.TokenObservation, cancellationToken);
        }
        foreach (var quota in parsed.QuotaSnapshots)
        {
            await _observatoryStore.UpsertQuotaSnapshotAsync(quota, cancellationToken);
        }
        if (parsed.ContextObservation is not null)
        {
            await _observatoryStore.UpsertContextObservationAsync(parsed.ContextObservation, cancellationToken);
        }

        if (_rolloutRecordBatchWriter is null)
        {
            // Compatibility path used by focused tests/alternate composition. Production composition
            // supplies the batch writer and avoids one file-stat + transaction per JSONL record.
            await _observatoryStore.RecordRolloutRecordAsync(
                parsed.SourceRecordId,
                sourceIdentity,
                filePath,
                parsed.SessionId,
                parsed.EventClass,
                parsed.RecordBytes,
                TryGetFileSize(filePath),
                parsed.TimestampUtc,
                cancellationToken);
        }
    }

    private async Task FlushStorageBatchAsync(
        List<CodexRolloutRecordMetadata>? pendingRecords,
        string sourceIdentity,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (_rolloutRecordBatchWriter is null || pendingRecords is null || pendingRecords.Count == 0)
        {
            return;
        }

        await _rolloutRecordBatchWriter.WriteBatchAsync(
            sourceIdentity,
            filePath,
            TryGetFileSize(filePath),
            pendingRecords,
            cancellationToken);
        pendingRecords.Clear();
    }

    private async Task SaveCheckpointAsync(
        string filePath,
        long offset,
        RolloutParseState state,
        string? previousSessionId,
        string parserVersion,
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        if (_observatoryStore is not null)
        {
            var resumeState = state.BuildResumeState(offset);
            if (resumeState is not null)
            {
                // Save semantic state first. If the following byte-checkpoint write fails, the offsets
                // differ and the next run safely replays from zero rather than using mismatched state.
                await _observatoryStore.UpsertParserResumeStateAsync(resumeState, cancellationToken);
            }
        }

        await _checkpointStore.SaveCheckpointAsync(
            new FileIngestionCheckpoint(
                filePath,
                offset,
                DateTimeOffset.UtcNow,
                state.OwnSessionId ?? previousSessionId,
                parserVersion,
                sourceIdentity),
            cancellationToken);
    }

    private static long TryGetFileSize(string filePath)
    {
        try
        {
            return Math.Max(0, new FileInfo(filePath).Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string GetSourceIdentity(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle handle = File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (GetFileInformationByHandle(handle, out var info))
            {
                return $"win:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            }
        }

        var creationTicks = File.GetCreationTimeUtc(filePath).Ticks;
        return $"fallback:{creationTicks.ToString("X16", CultureInfo.InvariantCulture)}";
    }

    private static string BuildSourceRecordId(string sourceIdentity, long start, long end)
    {
        var bytes = Encoding.UTF8.GetBytes($"{sourceIdentity}:{start}:{end}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
