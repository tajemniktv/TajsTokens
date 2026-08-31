using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
        if (!File.Exists(filePath))
        {
            return 0;
        }

        var existing = await checkpointStore.GetCheckpointAsync(filePath, cancellationToken);
        var sourceIdentity = GetSourceIdentity(filePath);
        var canResume = existing is not null &&
                        string.Equals(existing.ParserVersion, ParserVersion, StringComparison.Ordinal) &&
                        existing.SourceIdentity is not null &&
                        string.Equals(existing.SourceIdentity, sourceIdentity, StringComparison.Ordinal);

        var fromOffset = canResume ? existing!.LastByteOffset : 0;
        if (new FileInfo(filePath).Length < fromOffset)
        {
            fromOffset = 0;
        }

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
                ParserVersion,
                sourceIdentity),
            cancellationToken);

        return recordsScanned;
    }

    private static string GetSourceIdentity(string filePath)
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

        // The desktop app is Windows-native, but keeping a deterministic fallback lets Core/Infrastructure
        // validation still run elsewhere. Creation time remains stable across appends and ordinarily changes
        // when the path is replaced; if it cannot distinguish a replacement, the length guard still handles
        // truncation safely.
        var creationTicks = File.GetCreationTimeUtc(filePath).Ticks;
        return $"fallback:{creationTicks.ToString("X16", CultureInfo.InvariantCulture)}";
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
