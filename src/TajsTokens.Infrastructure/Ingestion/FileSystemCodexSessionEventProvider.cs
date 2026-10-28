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
    internal const int MaxRecordBytes = 64 * 1024 * 1024;

    public async IAsyncEnumerable<RawSessionRecord> ReadNewJsonLinesAsync(
        string filePath,
        long fromOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using CodexRolloutSource? source = await CodexRolloutSource.OpenAsync(filePath, cancellationToken);
        if (source is null) yield break;
        await using Stream stream = source.OpenDecodedStream();
        await foreach (RawSessionRecord record in ReadAsync(stream, CodexRolloutSource.Canonicalize(filePath), fromOffset,
                           source.DecodedLength, cancellationToken)) yield return record;
    }

    internal static async IAsyncEnumerable<RawSessionRecord> ReadAsync(Stream stream, string filePath,
        long fromOffset, long decodedLength, [EnumeratorCancellation] CancellationToken cancellationToken,
        bool includeWhitespace = false)
    {
        fromOffset = Math.Max(0, fromOffset);
        if (fromOffset > decodedLength)
        {
            // File was truncated/replaced. Re-scan from the beginning; parser-version/checkpoint logic
            // keeps this explicit instead of silently skipping content.
            fromOffset = 0;
        }

        byte[] buffer = new byte[BufferSize];
        if (stream.CanSeek) stream.Seek(fromOffset, SeekOrigin.Begin);
        else
        {
            long skipped = 0;
            while (skipped < fromOffset)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, fromOffset - skipped)), cancellationToken);
                if (read == 0) throw new IOException("Decoded rollout changed before its checkpoint.");
                skipped += read;
            }
        }
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
                    throw new CodexRolloutReadException(CodexRolloutReadFailure.RecordTooLarge);
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

                    // The optional UTF-8 BOM is not JSON text. Its original bytes still belong
                    // to the record coordinates and checkpoint hash.
                    int textStart = recordStart == 0 && length >= 3 && recordBuffer.GetBuffer().AsSpan(0, 3)
                        .SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
                    string payload = Encoding.UTF8.GetString(recordBuffer.GetBuffer(), textStart, length - textStart);
                    var completedRecord = new RawSessionRecord(filePath, recordStart, absoluteOffset, payload);
                    recordBuffer.SetLength(0);
                    // A rare multi-megabyte content record must not pin its capacity while the
                    // iterator waits for the consumer to persist later, much smaller records.
                    if (recordBuffer.Capacity > BufferSize) recordBuffer.Capacity = BufferSize;
                    recordStart = absoluteOffset;

                    if (includeWhitespace || !string.IsNullOrWhiteSpace(payload))
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
