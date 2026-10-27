// Taj's Tokens | FileSystemCodexSessionEventProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Runtime.CompilerServices;
using System.Text;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

#endregion

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
        byte[] buffer = new byte[BufferSize];
        using var recordBuffer = new MemoryStream();
        long recordStart = fromOffset;
        long absoluteOffset = fromOffset;

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            int index = 0;
            while (index < read)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int newline = buffer.AsSpan(index, read - index).IndexOf((byte)'\n');
                int count = newline < 0 ? read - index : newline;
                if (recordBuffer.Length + count > MaxRecordBytes)
                    throw new InvalidDataException($"JSONL record exceeds {MaxRecordBytes:N0} bytes: {filePath}");
                recordBuffer.Write(buffer, index, count);
                index += count;
                absoluteOffset += count;

                if (newline >= 0)
                {
                    index++;
                    absoluteOffset++;
                    int length = (int)recordBuffer.Length;
                    if (length > 0 && recordBuffer.GetBuffer()[length - 1] == (byte)'\r')
                    {
                        length--;
                    }

                    string payload = Encoding.UTF8.GetString(recordBuffer.GetBuffer(), 0, length);
                    var completedRecord = new RawSessionRecord(filePath, recordStart, absoluteOffset, payload);
                    recordBuffer.SetLength(0);
                    // A rare multi-megabyte content record must not pin its capacity while the
                    // iterator waits for the consumer to persist later, much smaller records.
                    if (recordBuffer.Capacity > BufferSize) recordBuffer.Capacity = BufferSize;
                    recordStart = absoluteOffset;

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        yield return completedRecord;
                    }

                }
            }
        }

        // Intentionally do not yield/checkpoint an unterminated final record. Codex may still be
        // writing it; the next pass will resume from recordStart and read the completed record.
    }
}
