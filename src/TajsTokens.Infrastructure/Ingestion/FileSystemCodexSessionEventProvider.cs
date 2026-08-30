using System.Runtime.CompilerServices;
using System.Text;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

public sealed class FileSystemCodexSessionEventProvider : ICodexSessionEventProvider
{
    private const int BufferSize = 64 * 1024;
    private const int MaxRecordBytes = 64 * 1024 * 1024;

    public async IAsyncEnumerable<RawSessionRecord> ReadNewJsonLinesAsync(
        string filePath,
        long fromOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        fromOffset = Math.Max(0, fromOffset);
        if (fromOffset > stream.Length)
        {
            // File was truncated/replaced. Re-scan from the beginning; parser-version/checkpoint logic
            // keeps this explicit instead of silently skipping content.
            fromOffset = 0;
        }

        stream.Seek(fromOffset, SeekOrigin.Begin);
        var buffer = new byte[BufferSize];
        using var recordBuffer = new MemoryStream();
        var recordStart = fromOffset;
        var absoluteOffset = fromOffset;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = buffer[index];
                absoluteOffset++;

                if (value == (byte)'\n')
                {
                    var bytes = recordBuffer.ToArray();
                    var length = bytes.Length;
                    if (length > 0 && bytes[^1] == (byte)'\r')
                    {
                        length--;
                    }

                    var payload = Encoding.UTF8.GetString(bytes, 0, length);
                    var completedRecord = new RawSessionRecord(filePath, recordStart, absoluteOffset, payload);
                    recordBuffer.SetLength(0);
                    recordStart = absoluteOffset;

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        yield return completedRecord;
                    }

                    continue;
                }

                recordBuffer.WriteByte(value);
                if (recordBuffer.Length > MaxRecordBytes)
                {
                    throw new InvalidDataException($"JSONL record exceeds {MaxRecordBytes:N0} bytes: {filePath}");
                }
            }
        }

        // Intentionally do not yield/checkpoint an unterminated final record. Codex may still be
        // writing it; the next pass will resume from recordStart and read the completed record.
    }
}
