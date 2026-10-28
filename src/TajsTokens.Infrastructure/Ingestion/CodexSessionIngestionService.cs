// Taj's Tokens | CodexSessionIngestionService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class CodexSessionIngestionService : ICodexSessionIngestionService
{
    private const string BoundaryParserVersion = "boundary-v2";
    private const string TypedParserVersion = "typed-v8-source-timestamp-provenance";
    private const int DurableBatchSize = 128;
    private readonly ISessionIngestionCheckpointStore _checkpointStore;
    private readonly ICodexIngestionBatchWriter? _ingestionBatchWriter;
    private readonly ICodexObservatoryStore? _observatoryStore;
    private readonly CodexRolloutParser _parser = new();
    private readonly ICodexSessionEventProvider _sessionEventProvider;

    private readonly Dictionary<string, (string Identity, string PhysicalIdentity, long Offset, string Hash, long Length, DateTime Written)> _verifiedPrefixes =
        new(StringComparer.OrdinalIgnoreCase);

    public CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore)
        : this(sessionEventProvider, checkpointStore, null, null)
    {
    }

    public CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore,
        ICodexObservatoryStore? observatoryStore)
        : this(sessionEventProvider, checkpointStore, observatoryStore, null)
    {
    }

    internal CodexSessionIngestionService(
        ICodexSessionEventProvider sessionEventProvider,
        ISessionIngestionCheckpointStore checkpointStore,
        ICodexObservatoryStore? observatoryStore,
        ICodexIngestionBatchWriter? ingestionBatchWriter)
    {
        _sessionEventProvider = sessionEventProvider;
        _checkpointStore = checkpointStore;
        _observatoryStore = observatoryStore;
        _ingestionBatchWriter = ingestionBatchWriter;
    }

    public async Task<bool> IsUnchangedAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_verifiedPrefixes.TryGetValue(filePath, out var verified) || verified.Offset != verified.Length ||
            verified.PhysicalIdentity.StartsWith("fallback", StringComparison.Ordinal)) return false;
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length != verified.Length || info.LastWriteTimeUtc != verified.Written ||
            GetSourceIdentity(filePath) != verified.PhysicalIdentity) return false;
        FileIngestionCheckpoint? checkpoint = await _checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        return checkpoint is not null && checkpoint.SourceIdentity == verified.Identity &&
               checkpoint.LastByteOffset == verified.Offset && checkpoint.ConsumedPrefixSha256 == verified.Hash &&
               checkpoint.ParserVersion == ParserVersion(filePath);
    }

    private string ParserVersion(string filePath)
    {
        string version = _observatoryStore is null ? BoundaryParserVersion : TypedParserVersion;
        return _observatoryStore is not null && CodexRolloutParser.HasDesktopFilenameSuffix(filePath)
            ? version + "/desktop-owner-v1" : version;
    }

    public async Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken)
    {
        using var work = WorkDiagnostics.Measure(WorkPhase.RolloutIngestion);
        if (!File.Exists(filePath))
        {
            work?.Complete(rows: 0, logicalBytes: 0, committedTransactions: 0);
            return CodexIngestionResult.Empty;
        }

        if (_observatoryStore is not null)
        {
            await _observatoryStore.InitializeAsync(cancellationToken);
        }

        string parserVersion = ParserVersion(filePath);
        FileIngestionCheckpoint? existing = await _checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        string sourceIdentity = GetSourceIdentity(filePath);
        string physicalIdentity = sourceIdentity;
        var initialInfo = new FileInfo(filePath);
        long initialLength = initialInfo.Length;
        DateTime initialWritten = initialInfo.LastWriteTimeUtc;
        long prefixBytesHashed = 0;
        long completeRecordBytesDecoded = 0;
        int checkpointWrites = 0;
        using var prefixHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var prefixStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hashBuffer = new byte[65536];

        async Task<string> HashThroughAsync(long offset)
        {
            using var verification = WorkDiagnostics.Measure(WorkPhase.PrefixVerification);
            long before = prefixBytesHashed;
            if (prefixStream.Position > offset)
            {
                prefixStream.Position = 0;
                prefixHash.GetHashAndReset();
            }
            while (prefixStream.Position < offset)
            {
                int read = await prefixStream.ReadAsync(
                    hashBuffer.AsMemory(
                        0,
                        (int)Math.Min(hashBuffer.Length, offset - prefixStream.Position)),
                    cancellationToken);
                if (read == 0) throw new IOException("Rollout changed while verifying its byte checkpoint; retry required.");
                prefixHash.AppendData(hashBuffer, 0, read);
                prefixBytesHashed += read;
            }
            verification?.Complete(logicalBytes: prefixBytesHashed - before);
            return Convert.ToHexString(prefixHash.GetCurrentHash());
        }

        bool sameFile = existing?.SourceIdentity is { } previousIdentity &&
                        (previousIdentity == sourceIdentity || previousIdentity.StartsWith(
                            sourceIdentity + ":generation:",
                            StringComparison.Ordinal));
        // A verified legacy checkpoint may retain its occurrence ID after gaining a stable
        // physical file ID. Cache against that physical ID, never against a path-only fallback.
        bool cachedPrefix = !sourceIdentity.StartsWith("fallback-path:", StringComparison.Ordinal) &&
                            existing?.ConsumedPrefixSha256 is { } knownHash &&
                            _verifiedPrefixes.TryGetValue(
                                filePath,
                                out (string Identity, string PhysicalIdentity, long Offset, string Hash, long Length, DateTime Written) verified) &&
                            verified == (existing.SourceIdentity, sourceIdentity, existing.LastByteOffset, knownHash, initialLength, initialWritten);
        string? existingPrefix = existing is null ? null :
            cachedPrefix ? existing.ConsumedPrefixSha256 :
            await HashThroughAsync(Math.Min(existing.LastByteOffset, prefixStream.Length));
        // A prefix-verified legacy fallback ID denotes the already captured occurrence, even
        // though it never proved physical identity. Retain its IDs/capture times during the
        // transition to stable OS identity; changed bytes still start a new generation.
        bool legacyIdentity = existing?.SourceIdentity?.StartsWith("fallback:", StringComparison.Ordinal) == true ||
                              existing?.SourceIdentity?.StartsWith("fallback-path:", StringComparison.Ordinal) == true;
        bool verifiedContent = existing is not null && (sameFile || legacyIdentity) && existing.LastByteOffset <= prefixStream.Length &&
                               existing.ConsumedPrefixSha256 is not null &&
                               existing.ConsumedPrefixSha256 == existingPrefix;
        bool canResume = verifiedContent && string.Equals(existing!.ParserVersion, parserVersion, StringComparison.Ordinal);
        // These upgrades enrich immutable occurrences only. Keep verified IDs, token amounts and
        // prior capture times; ownership/reducer upgrades still require their generation replay.
        bool metadataOnlyReplay = verifiedContent && _observatoryStore is not null &&
                                  (existing!.ParserVersion == parserVersion.Replace(TypedParserVersion, "typed-v6-service-tier-evidence", StringComparison.Ordinal) ||
                                   existing.ParserVersion == parserVersion.Replace(TypedParserVersion, "typed-v7-response-evidence", StringComparison.Ordinal));

        if (canResume || metadataOnlyReplay) sourceIdentity = existing!.SourceIdentity!;
        else if (sameFile)
            // New generation retires the old projections before replay, even when the OS file ID
            // survived an in-place rewrite. Legacy unverified checkpoints take this path once.
            sourceIdentity += ":generation:" + existingPrefix;

        long fromOffset = canResume ? existing!.LastByteOffset : 0;
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
                existing?.LastSessionId is not null &&
                !string.Equals(existing.LastSessionId, resumeState.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                // A byte checkpoint without parser metadata cannot safely resume semantic parsing. A
                // one-time replay from zero is cheaper than overwriting established agent/model state.
                fromOffset = 0;
                resumeState = null;
            }
        }

        var state = new RolloutParseState(filePath, sourceIdentity, resumeState);
        int recordsScanned = 0;
        int normalizedRecords = 0;
        long lastCompleteRecordOffset = fromOffset;
        List<ParsedRolloutRecord>? pendingRecords = _ingestionBatchWriter is null
            ? null
            : new List<ParsedRolloutRecord>(DurableBatchSize);

        await foreach (RawSessionRecord record in _sessionEventProvider.ReadNewJsonLinesAsync(filePath, fromOffset, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordsScanned++;
            completeRecordBytesDecoded += record.EndByteOffset - record.StartByteOffset;

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
                        state.OwnSessionId, CodexSourceTimestampQuality.Malformed);
                }

                if (pendingRecords is not null)
                {
                    pendingRecords.Add(parsed);
                }
                else
                {
                    await PersistParsedTelemetryAsync(parsed, sourceIdentity, filePath, cancellationToken);
                }

                if (parsed.HasNormalizedTelemetry)
                {
                    normalizedRecords++;
                }
            }

            // This offset becomes durable only after the corresponding ingestion batch has committed.
            // If persistence fails, no checkpoint is advanced and replay remains safe/idempotent.
            lastCompleteRecordOffset = record.EndByteOffset;
            if (recordsScanned % DurableBatchSize == 0)
            {
                await FlushIngestionBatchAsync(
                    pendingRecords,
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
                    await HashThroughAsync(lastCompleteRecordOffset),
                    cancellationToken);
                checkpointWrites++;
            }
        }

        await FlushIngestionBatchAsync(
            pendingRecords,
            sourceIdentity,
            filePath,
            cancellationToken);
        string finalPrefix = canResume && recordsScanned == 0 ? existingPrefix! : await HashThroughAsync(lastCompleteRecordOffset);
        if ((!canResume && recordsScanned == 0) || recordsScanned % DurableBatchSize != 0)
        {
            await SaveCheckpointAsync(
                filePath,
                lastCompleteRecordOffset,
                state,
                existing?.LastSessionId,
                parserVersion,
                sourceIdentity,
                finalPrefix,
                cancellationToken);
            checkpointWrites++;
        }

        var finalInfo = new FileInfo(filePath);
        if (finalInfo.Length == initialLength && finalInfo.LastWriteTimeUtc == initialWritten &&
            GetSourceIdentity(filePath) == physicalIdentity)
            _verifiedPrefixes[filePath] = (sourceIdentity, physicalIdentity, lastCompleteRecordOffset,
                finalPrefix, initialLength, initialWritten);
        else _verifiedPrefixes.Remove(filePath);

        int normalized = _observatoryStore is null ? recordsScanned : normalizedRecords;
        work?.Complete(rows: recordsScanned, logicalBytes: completeRecordBytesDecoded);
        return new CodexIngestionResult(recordsScanned, normalized, state.OwnSessionId ?? existing?.LastSessionId)
        {
            SourceIdentity = sourceIdentity,
            LastCompleteRecordOffset = lastCompleteRecordOffset,
            SourceLength = TryGetFileSize(filePath),
            PrefixBytesHashed = prefixBytesHashed,
            CompleteRecordBytesDecoded = completeRecordBytesDecoded,
            CheckpointWrites = checkpointWrites,
        };
    }

    private async Task FlushIngestionBatchAsync(
        List<ParsedRolloutRecord>? pendingRecords,
        string sourceIdentity,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (_ingestionBatchWriter is null || pendingRecords is null || pendingRecords.Count == 0)
        {
            return;
        }

        await _ingestionBatchWriter.WriteBatchAsync(
            sourceIdentity,
            filePath,
            TryGetFileSize(filePath),
            pendingRecords,
            cancellationToken);
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

        // Retire a replaced source before counter reduction, matching the production batch path.
        await _observatoryStore.RecordRolloutRecordAsync(
            parsed.SourceRecordId,
            sourceIdentity,
            filePath,
            parsed.SessionId,
            parsed.EventClass,
            parsed.RecordBytes,
            TryGetFileSize(filePath),
            parsed.TimestampUtc,
            cancellationToken, parsed.TimestampQuality);

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
        foreach (QuotaSnapshot quota in parsed.QuotaSnapshots)
        {
            await _observatoryStore.UpsertQuotaSnapshotAsync(quota, cancellationToken);
        }
        if (parsed.ContextObservation is not null)
        {
            await _observatoryStore.UpsertContextObservationAsync(parsed.ContextObservation, cancellationToken);
        }
        if (parsed.WorkloadObservation is not null)
        {
            await _observatoryStore.UpsertWorkloadObservationAsync(parsed.WorkloadObservation, cancellationToken);
        }
        if (parsed.ResponseObservation is not null)
            await _observatoryStore.UpsertResponseObservationAsync(parsed.ResponseObservation, cancellationToken);
    }

    private async Task SaveCheckpointAsync(
        string filePath,
        long offset,
        RolloutParseState state,
        string? previousSessionId,
        string parserVersion,
        string sourceIdentity,
        string consumedPrefixSha256,
        CancellationToken cancellationToken)
    {
        if (_observatoryStore is not null)
        {
            CodexParserResumeState? resumeState = state.BuildResumeState(offset);
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
                sourceIdentity) { ConsumedPrefixSha256 = consumedPrefixSha256 },
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

            if (GetFileInformationByHandle(handle, out ByHandleFileInformation info))
            {
                return $"win:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            }
        }

        if (OperatingSystem.IsLinux() && TryGetLinuxFileIdentity(filePath) is { } linuxIdentity)
        {
            return linuxIdentity;
        }

        // CreationTimeUtc can map to ctime on filesystems without birth-time support, so it
        // changes on append and cannot identify a stable source generation. A path hash is stable
        // across appends and keeps the local path out of durable telemetry. Comparisons that need
        // physical replacement detection fail closed when this fallback is in use.
        string normalizedPath = Path.GetFullPath(filePath).Replace(Path.DirectorySeparatorChar, '/');
        string pathHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return $"fallback-path:{pathHash}";
    }

    private static string? TryGetLinuxFileIdentity(string filePath)
    {
        const int atCurrentWorkingDirectory = -100;
        const uint statxInode = 0x100;
        const int statxBufferBytes = 256;
        IntPtr buffer = Marshal.AllocHGlobal(statxBufferBytes);
        try
        {
            for (int offset = 0; offset < statxBufferBytes; offset += sizeof(long))
            {
                Marshal.WriteInt64(buffer, offset, 0);
            }

            if (LinuxStatx(atCurrentWorkingDirectory, filePath, 0, statxInode, buffer) != 0 ||
                (unchecked((uint)Marshal.ReadInt32(buffer, 0)) & statxInode) == 0)
            {
                return null;
            }

            // Linux struct statx has fixed ABI offsets for stx_ino and the device major/minor.
            uint deviceMajor = unchecked((uint)Marshal.ReadInt32(buffer, 136));
            uint deviceMinor = unchecked((uint)Marshal.ReadInt32(buffer, 140));
            ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            return $"linux:{deviceMajor:X8}:{deviceMinor:X8}:{inode:X16}";
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        IntPtr buffer);

    private static string BuildSourceRecordId(string sourceIdentity, long start, long end)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{sourceIdentity}:{start}:{end}");
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
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
