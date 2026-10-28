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

    private readonly Dictionary<string, (string Identity, string PhysicalIdentity, long Offset, string Hash, long Length, DateTime Written, long DecodedLength)> _verifiedPrefixes =
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
        filePath = CodexRolloutSource.Canonicalize(filePath);
        if (!_verifiedPrefixes.TryGetValue(filePath, out var verified) || verified.Offset != verified.DecodedLength ||
            verified.PhysicalIdentity.StartsWith("fallback", StringComparison.Ordinal)) return false;
        string physicalPath = CodexRolloutSource.Resolve(filePath);
        var info = new FileInfo(physicalPath);
        if (!info.Exists || info.Length != verified.Length || info.LastWriteTimeUtc != verified.Written ||
            GetSourceIdentity(physicalPath) != verified.PhysicalIdentity) return false;
        FileIngestionCheckpoint? checkpoint = await _checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        return checkpoint is not null && checkpoint.SourceIdentity == verified.Identity &&
               checkpoint.LastByteOffset == verified.Offset && checkpoint.ConsumedPrefixSha256 == verified.Hash &&
               checkpoint.ParserVersion == ParserVersion(checkpoint.LogicalSourcePath ?? filePath) &&
               checkpoint.CoordinateKind == "decoded-utf8-bytes-v1" &&
               checkpoint.PhysicalSourceIdentity == verified.PhysicalIdentity &&
               checkpoint.PhysicalEncoding == (physicalPath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) ? "zstandard" : "plain");
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
        await using CodexRolloutSource? source = await CodexRolloutSource.OpenAsync(filePath, cancellationToken);
        if (source is null)
        {
            work?.Complete(rows: 0, logicalBytes: 0, committedTransactions: 0);
            return CodexIngestionResult.MissingSource;
        }

        if (_observatoryStore is not null)
        {
            await _observatoryStore.InitializeAsync(cancellationToken);
        }

        filePath = source.CanonicalPath;
        FileIngestionCheckpoint? existing = await _checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        if (_sessionEventProvider is FileSystemCodexSessionEventProvider)
        {
            IReadOnlyList<FileIngestionCheckpoint> candidates = await _checkpointStore.FindCheckpointCandidatesAsync(
                Path.GetFileName(filePath), cancellationToken);
            if (candidates.Count >= 65) throw new CodexRolloutReadException(CodexRolloutReadFailure.ConflictingRepresentations);
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            existing = candidates.Append(existing).Where(x => x is not null &&
                    string.Equals(Path.GetFileName(x.FilePath), Path.GetFileName(filePath), comparison) &&
                    string.Equals(CodexRolloutSource.Namespace(x.FilePath), CodexRolloutSource.Namespace(filePath), comparison))
                .OrderByDescending(x => x!.UpdatedAtUtc).ThenByDescending(x => x!.LastByteOffset).FirstOrDefault();
        }
        string logicalSourcePath = existing?.LogicalSourcePath ?? existing?.FilePath ?? filePath;
        string parserVersion = ParserVersion(logicalSourcePath);
        string sourceNamespace = Environment.MachineName + "\0" + CodexRolloutSource.Namespace(filePath);
        if (OperatingSystem.IsWindows()) sourceNamespace = sourceNamespace.ToUpperInvariant();
        string sourceIdentity = "rollout/v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceNamespace))) + ":" + source.PhysicalIdentity;
        string physicalIdentity = source.PhysicalIdentity;
        long initialLength = source.PhysicalLength;
        DateTime initialWritten = source.WrittenUtc;
        long prefixBytesHashed = 0;
        long completeRecordBytesDecoded = 0;
        int checkpointWrites = 0;
        using var prefixHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Stream prefixStream = source.OpenDecodedStream();
        await using var prefixLifetime = new AsyncStreamLifetime(() => prefixStream);
        long prefixPosition = 0;
        byte[] hashBuffer = new byte[65536];

        async Task<string> HashThroughAsync(long offset)
        {
            using var verification = WorkDiagnostics.Measure(WorkPhase.PrefixVerification);
            long before = prefixBytesHashed;
            if (prefixPosition > offset)
            {
                await prefixStream.DisposeAsync();
                prefixStream = source.OpenDecodedStream();
                prefixPosition = 0;
                prefixHash.GetHashAndReset();
            }
            while (prefixPosition < offset)
            {
                int read = await prefixStream.ReadAsync(
                    hashBuffer.AsMemory(
                        0,
                        (int)Math.Min(hashBuffer.Length, offset - prefixPosition)),
                    cancellationToken);
                if (read == 0) throw new IOException("Rollout changed while verifying its byte checkpoint; retry required.");
                prefixHash.AppendData(hashBuffer, 0, read);
                prefixPosition += read;
                prefixBytesHashed += read;
            }
            verification?.Complete(logicalBytes: prefixBytesHashed - before);
            return Convert.ToHexString(prefixHash.GetCurrentHash());
        }

        bool sameFile = existing?.PhysicalSourceIdentity == physicalIdentity || existing?.SourceIdentity is { } previousIdentity &&
                        (previousIdentity == physicalIdentity || previousIdentity.StartsWith(
                            physicalIdentity + ":generation:",
                            StringComparison.Ordinal));
        // A verified legacy checkpoint may retain its occurrence ID after gaining a stable
        // physical file ID. Cache against that physical ID, never against a path-only fallback.
        bool cachedPrefix = !physicalIdentity.StartsWith("fallback-path:", StringComparison.Ordinal) &&
                            existing?.ConsumedPrefixSha256 is { } knownHash &&
                            _verifiedPrefixes.TryGetValue(
                                filePath,
                                out var verified) &&
                            verified == (existing.SourceIdentity, physicalIdentity, existing.LastByteOffset, knownHash, initialLength, initialWritten, source.DecodedLength);
        string? existingPrefix = existing is null ? null :
            cachedPrefix ? existing.ConsumedPrefixSha256 :
            await HashThroughAsync(Math.Min(existing.LastByteOffset, source.DecodedLength));
        // A prefix-verified legacy fallback ID denotes the already captured occurrence, even
        // though it never proved physical identity. Retain its IDs/capture times during the
        // transition to stable OS identity; changed bytes still start a new generation.
        bool legacyIdentity = existing?.SourceIdentity?.StartsWith("fallback:", StringComparison.Ordinal) == true ||
                              existing?.SourceIdentity?.StartsWith("fallback-path:", StringComparison.Ordinal) == true;
        bool knownCoordinates = existing?.CoordinateKind is null or "decoded-utf8-bytes-v1";
        if (!knownCoordinates) throw new CodexRolloutReadException(CodexRolloutReadFailure.UnsupportedCheckpoint);
        bool verifiedContent = existing is not null && (sameFile || legacyIdentity || existing.LastByteOffset > 0) &&
                               existing.LastByteOffset <= source.DecodedLength &&
                               existing.ConsumedPrefixSha256 is not null &&
                               existing.ConsumedPrefixSha256 == existingPrefix;
        if (!verifiedContent && existing is not null && existing.FilePath != filePath &&
            File.Exists(CodexRolloutSource.Resolve(existing.FilePath)))
            throw new CodexRolloutReadException(CodexRolloutReadFailure.ConflictingRepresentations);
        bool canResume = verifiedContent && string.Equals(existing!.ParserVersion, parserVersion, StringComparison.Ordinal);
        // These upgrades enrich immutable occurrences only. Keep verified IDs, token amounts and
        // prior capture times; ownership/reducer upgrades still require their generation replay.
        bool metadataOnlyReplay = verifiedContent && _observatoryStore is not null &&
                                  (existing!.ParserVersion == parserVersion.Replace(TypedParserVersion, "typed-v6-service-tier-evidence", StringComparison.Ordinal) ||
                                   existing.ParserVersion == parserVersion.Replace(TypedParserVersion, "typed-v7-response-evidence", StringComparison.Ordinal));

        if (canResume || metadataOnlyReplay) sourceIdentity = existing!.SourceIdentity!;
        else if (existing is not null)
            // New generation retires the old projections before replay, even when the OS file ID
            // survived an in-place rewrite. Legacy unverified checkpoints take this path once.
            sourceIdentity += ":generation:" + existingPrefix;

        long fromOffset = canResume ? existing!.LastByteOffset : 0;
        if (source.DecodedLength < fromOffset)
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

        var state = new RolloutParseState(logicalSourcePath, sourceIdentity, resumeState);
        int recordsScanned = 0;
        int normalizedRecords = 0;
        long lastCompleteRecordOffset = fromOffset;
        long checkpointedOffset = canResume ? existing!.LastByteOffset : -1;
        List<ParsedRolloutRecord>? pendingRecords = _ingestionBatchWriter is null
            ? null
            : new List<ParsedRolloutRecord>(DurableBatchSize);

        await using Stream recordStream = source.OpenDecodedStream();
        IAsyncEnumerable<RawSessionRecord> records = _sessionEventProvider is FileSystemCodexSessionEventProvider
            ? FileSystemCodexSessionEventProvider.ReadAsync(recordStream, logicalSourcePath, fromOffset, source.DecodedLength, cancellationToken, includeWhitespace: true)
            : _sessionEventProvider.ReadNewJsonLinesAsync(filePath, fromOffset, cancellationToken);
        await foreach (RawSessionRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completeRecordBytesDecoded += record.EndByteOffset - record.StartByteOffset;
            lastCompleteRecordOffset = record.EndByteOffset;
            if (string.IsNullOrWhiteSpace(record.Payload)) continue;
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
                        state.OwnSessionId, CodexSourceTimestampQuality.Malformed);
                }

                if (pendingRecords is not null)
                {
                    pendingRecords.Add(parsed);
                }
                else
                {
                    source.EnsureSelected();
                    await PersistParsedTelemetryAsync(parsed, sourceIdentity, logicalSourcePath, source.CurrentPhysicalLength, cancellationToken);
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
                source.EnsureSelected();
                await FlushIngestionBatchAsync(
                    pendingRecords,
                    sourceIdentity,
                    logicalSourcePath,
                    source.CurrentPhysicalLength,
                    cancellationToken);
                await SaveCheckpointAsync(
                    filePath,
                    lastCompleteRecordOffset,
                    state,
                    existing?.LastSessionId,
                    parserVersion,
                    sourceIdentity,
                    logicalSourcePath, source.Encoding, physicalIdentity,
                    await HashThroughAsync(lastCompleteRecordOffset),
                    cancellationToken);
                checkpointWrites++;
                checkpointedOffset = lastCompleteRecordOffset;
            }
        }

        source.EnsureSelected();
        if (recordsScanned == 0 && _observatoryStore is not null &&
            (!canResume || lastCompleteRecordOffset != existing?.LastByteOffset || existing.PhysicalEncoding != source.Encoding ||
             existing.PhysicalSourceIdentity != physicalIdentity || existing.FilePath != filePath))
            await _observatoryStore.UpsertRolloutFileAsync(sourceIdentity, logicalSourcePath,
                state.OwnSessionId ?? existing?.LastSessionId, source.PhysicalLength, DateTimeOffset.UtcNow, cancellationToken);
        await FlushIngestionBatchAsync(
            pendingRecords,
            sourceIdentity,
            logicalSourcePath,
            source.CurrentPhysicalLength,
            cancellationToken);
        string finalPrefix = canResume && lastCompleteRecordOffset == existing!.LastByteOffset
            ? existingPrefix! : await HashThroughAsync(lastCompleteRecordOffset);
        if (checkpointedOffset != lastCompleteRecordOffset ||
            existing?.CoordinateKind != "decoded-utf8-bytes-v1" || existing.PhysicalSourceIdentity != physicalIdentity ||
            existing.PhysicalEncoding != source.Encoding || existing.FilePath != filePath)
        {
            await SaveCheckpointAsync(
                filePath,
                lastCompleteRecordOffset,
                state,
                existing?.LastSessionId,
                parserVersion,
                sourceIdentity,
                logicalSourcePath, source.Encoding, physicalIdentity,
                finalPrefix,
                cancellationToken);
            checkpointWrites++;
        }

        var finalInfo = new FileInfo(source.PhysicalPath);
        if (finalInfo.Length == initialLength && finalInfo.LastWriteTimeUtc == initialWritten &&
            GetSourceIdentity(source.PhysicalPath) == physicalIdentity)
            _verifiedPrefixes[filePath] = (sourceIdentity, physicalIdentity, lastCompleteRecordOffset,
                finalPrefix, initialLength, initialWritten, source.CurrentDecodedLength);
        else _verifiedPrefixes.Remove(filePath);

        int normalized = _observatoryStore is null ? recordsScanned : normalizedRecords;
        work?.Complete(rows: recordsScanned, logicalBytes: completeRecordBytesDecoded);
        return new CodexIngestionResult(recordsScanned, normalized, state.OwnSessionId ?? existing?.LastSessionId)
        {
            SourceIdentity = sourceIdentity,
            LastCompleteRecordOffset = lastCompleteRecordOffset,
            SourceLength = source.CurrentDecodedLength,
            LogicalSourcePath = logicalSourcePath,
            PhysicalSourcePath = source.PhysicalPath,
            PhysicalSourceLength = source.CurrentPhysicalLength,
            CompressedValidationBytes = source.Compressed ? source.DecodedLength : 0,
            PrefixBytesHashed = prefixBytesHashed,
            CompleteRecordBytesDecoded = completeRecordBytesDecoded,
            CheckpointWrites = checkpointWrites,
        };
    }

    private async Task FlushIngestionBatchAsync(
        List<ParsedRolloutRecord>? pendingRecords,
        string sourceIdentity,
        string filePath,
        long fileSizeBytes,
        CancellationToken cancellationToken)
    {
        if (_ingestionBatchWriter is null || pendingRecords is null || pendingRecords.Count == 0)
        {
            return;
        }

        await _ingestionBatchWriter.WriteBatchAsync(
            sourceIdentity,
            filePath,
            fileSizeBytes,
            pendingRecords,
            cancellationToken);
        pendingRecords.Clear();
    }

    private async Task PersistParsedTelemetryAsync(
        ParsedRolloutRecord parsed,
        string sourceIdentity,
        string filePath,
        long fileSizeBytes,
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
            fileSizeBytes,
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
        string logicalSourcePath, string physicalEncoding, string physicalIdentity,
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
                sourceIdentity)
            {
                ConsumedPrefixSha256 = consumedPrefixSha256,
                CoordinateKind = "decoded-utf8-bytes-v1", LogicalSourcePath = logicalSourcePath,
                PhysicalEncoding = physicalEncoding, PhysicalSourceIdentity = physicalIdentity,
            },
            cancellationToken);
    }

    internal static string GetSourceIdentity(string filePath, SafeFileHandle? existingHandle = null)
    {
        if (OperatingSystem.IsWindows())
        {
            using SafeFileHandle? openedHandle = existingHandle is null ? File.OpenHandle(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete) : null;

            if (GetFileInformationByHandle(existingHandle ?? openedHandle!, out ByHandleFileInformation info))
            {
                return $"win:{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            }
        }

        if (OperatingSystem.IsLinux() && TryGetLinuxFileIdentity(filePath, existingHandle) is { } linuxIdentity)
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

    private static string? TryGetLinuxFileIdentity(string filePath, SafeFileHandle? handle = null)
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

            if (LinuxStatx(handle is null ? atCurrentWorkingDirectory : handle.DangerousGetHandle().ToInt32(),
                    handle is null ? filePath : "", handle is null ? 0 : 0x1000, statxInode, buffer) != 0 ||
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

    private sealed class AsyncStreamLifetime(Func<Stream> current) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => current().DisposeAsync();
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
