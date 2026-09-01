using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class CodexSessionIngestionService : ICodexSessionIngestionService
{
    private const string BoundaryParserVersion = "boundary-v2";
    private const string TypedParserVersion = "typed-v1";
    private readonly ICodexSessionEventProvider _sessionEventProvider;
    private readonly ISessionIngestionCheckpointStore _checkpointStore;
    private readonly ICodexObservatoryStore? _observatoryStore;
    private readonly CodexRolloutParser _parser = new();

    public CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore)
        : this(sessionEventProvider, checkpointStore, null)
    {
    }

    public CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore,
        ICodexObservatoryStore? observatoryStore)
    {
        _sessionEventProvider = sessionEventProvider;
        _checkpointStore = checkpointStore;
        _observatoryStore = observatoryStore;
    }

    public async Task<int> IngestAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return 0;
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

        var state = new RolloutParseState(filePath, sourceIdentity, canResume ? existing?.LastSessionId : null);
        var recordsScanned = 0;
        var normalizedRecords = 0;
        var lastCompleteRecordOffset = fromOffset;

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

                await PersistParsedRecordAsync(parsed, filePath, cancellationToken);
                if (parsed.HasNormalizedTelemetry)
                {
                    normalizedRecords++;
                }
            }

            // Advance only after every normalized write for this complete record succeeded. If the
            // process dies between records, replay is safe because source/event ids are idempotent.
            lastCompleteRecordOffset = record.EndByteOffset;
            if (recordsScanned % 128 == 0)
            {
                await SaveCheckpointAsync(
                    filePath, lastCompleteRecordOffset, state.OwnSessionId ?? existing?.LastSessionId,
                    parserVersion, sourceIdentity, cancellationToken);
            }
        }

        await SaveCheckpointAsync(
            filePath, lastCompleteRecordOffset, state.OwnSessionId ?? existing?.LastSessionId,
            parserVersion, sourceIdentity, cancellationToken);

        return _observatoryStore is null ? recordsScanned : normalizedRecords;
    }

    private async Task PersistParsedRecordAsync(
        ParsedRolloutRecord parsed,
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

        var fileSize = 0L;
        try
        {
            fileSize = new FileInfo(filePath).Length;
        }
        catch (IOException)
        {
            // The record itself was already read successfully; storage size is diagnostic metadata.
        }

        await _observatoryStore.RecordRolloutRecordAsync(
            parsed.SourceRecordId,
            filePath,
            parsed.SessionId,
            parsed.EventClass,
            parsed.RecordBytes,
            fileSize,
            parsed.TimestampUtc,
            cancellationToken);
    }

    private Task SaveCheckpointAsync(
        string filePath,
        long offset,
        string? sessionId,
        string parserVersion,
        string sourceIdentity,
        CancellationToken cancellationToken) =>
        _checkpointStore.SaveCheckpointAsync(
            new FileIngestionCheckpoint(
                filePath,
                offset,
                DateTimeOffset.UtcNow,
                sessionId,
                parserVersion,
                sourceIdentity),
            cancellationToken);

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
