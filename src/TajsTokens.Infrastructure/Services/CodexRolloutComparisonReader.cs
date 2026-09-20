// Taj's Tokens | CodexRolloutComparisonReader.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;

#endregion

namespace TajsTokens.Infrastructure.Services;

/// <summary>
///     Bounded ephemeral byte inspection, never a semantic importer or deduplicator. File metadata
///     and identity checks detect ordinary concurrent changes; native files are not a transaction.
/// </summary>
internal sealed class CodexRolloutComparisonReader(Action? afterRead = null)
{
    internal const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumRecords = 20_000;

    public async Task<CodexRolloutComparison> CompareAsync(
        string alternatePath,
        string indexedPath,
        string threadId,
        CancellationToken cancellationToken)
    {
        CodexRolloutComparison Unknown(string reason, bool hasPrefix = false)
        {
            return new CodexRolloutComparison(
                alternatePath,
                indexedPath,
                CodexRolloutComparisonKind.Unresolved,
                false,
                hasPrefix,
                null,
                reason);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long Length, DateTime Modified, DateTime Created, string Identity) alternateStamp = Stamp(alternatePath);
            (long Length, DateTime Modified, DateTime Created, string Identity) indexedStamp = Stamp(indexedPath);
            if (alternateStamp.Length > MaximumFileBytes || indexedStamp.Length > MaximumFileBytes)
                return Unknown("Not compared: a file exceeds the 2 MiB inspection limit. Size equality is not byte equality.");

            byte[] alternate = await ReadAsync(alternatePath, alternateStamp.Length, cancellationToken);
            byte[] indexed = await ReadAsync(indexedPath, indexedStamp.Length, cancellationToken);
            afterRead?.Invoke();
            if (alternateStamp != Stamp(alternatePath) || indexedStamp != Stamp(indexedPath))
                return Unknown("A file changed or was replaced during inspection. Retry after it becomes stable.");

            RecordScan left = InspectRecords(alternate, alternatePath, threadId, cancellationToken);
            RecordScan right = InspectRecords(indexed, indexedPath, threadId, cancellationToken);
            if (left.Error is not null || right.Error is not null)
                return Unknown(
                    $"Not comparable: alternate {left.Error ?? "complete"}; indexed {right.Error ?? "complete"}.",
                    left.HasPrefix || right.HasPrefix);

            bool hasPrefix = left.HasPrefix || right.HasPrefix;
            // Even exact bytes do not establish a logical owner. Keep that independent result visible.
            bool owned = left.OwnedStart is not null && right.OwnedStart is not null;
            if (alternate.AsSpan().SequenceEqual(indexed))
                return new CodexRolloutComparison(
                    alternatePath,
                    indexedPath,
                    CodexRolloutComparisonKind.IdenticalBytes,
                    owned,
                    hasPrefix,
                    owned ? left.Records.Count : null,
                    "Complete captured bytes match. " +
                    (owned ? "Both files corroborate the indexed thread owner. " : "Logical ownership remains unresolved. ") +
                    "This does not authorize import or prove durable collection completeness.");
            if (!owned)
                return Unknown(
                    "Logical ownership is unresolved: both filenames and session_meta must corroborate the indexed thread, with no later conflicting owner.",
                    hasPrefix);

            int common = 0;
            while (common < Math.Min(left.Records.Count, right.Records.Count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                (int Start, int Length) l = left.Records[common];
                (int Start, int Length) r = right.Records[common];
                if (!alternate.AsSpan(l.Start, l.Length).SequenceEqual(indexed.AsSpan(r.Start, r.Length))) break;
                common++;
            }
            CodexRolloutComparisonKind kind = common == left.Records.Count && common == right.Records.Count
                ? CodexRolloutComparisonKind.IdenticalOwnedRecords
                : common == Math.Min(left.Records.Count, right.Records.Count)
                    ? CodexRolloutComparisonKind.PrefixOverlap
                    : CodexRolloutComparisonKind.DifferentRecords;
            string detail = kind switch
            {
                CodexRolloutComparisonKind.IdenticalOwnedRecords =>
                    "Owned record bytes match; whole files differ outside that owned stream. Whole files are not identical.",
                CodexRolloutComparisonKind.PrefixOverlap =>
                    "One complete owned record stream is an exact prefix of the other. Extra records are not automatically missing collected work.",
                _ =>
                    "Owned record streams differ after the common prefix. This is byte-level divergence, not proof of a semantic conflict or additional billable work.",
            };
            return new CodexRolloutComparison(
                alternatePath,
                indexedPath,
                kind,
                true,
                hasPrefix,
                common,
                detail + " No records were imported or merged.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            // Do not echo paths/content from exceptions into diagnostic strings.
            return Unknown("A file is unavailable, unreadable, or changed during inspection. No comparison was accepted.");
        }
    }

    private static (long Length, DateTime Modified, DateTime Created, string Identity) Stamp(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, CodexSessionIngestionService.GetSourceIdentity(path));
    }

    private static async Task<byte[]> ReadAsync(string path, long length, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[checked((int)length)];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static RecordScan InspectRecords(byte[] bytes, string path, string threadId, CancellationToken cancellationToken)
    {
        var result = new List<(int Start, int Length)>();
        int? ownedStart = null;
        bool hasPrefix = false;
        string? filenameOwner = CodexRolloutParser.ExtractSessionIdFromFileName(path);
        bool corroboratedFilename = string.Equals(filenameOwner, threadId, StringComparison.OrdinalIgnoreCase);
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n') return new RecordScan(null, false, result, "empty or incomplete final record");
        int start = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        int recordCount = 0;
        try
        {
            while (start < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = Array.IndexOf(bytes, (byte)'\n', start);
                if (bytes.AsSpan(start, end - start).IndexOfAnyExcept((byte)' ', (byte)'\t', (byte)'\r') < 0)
                {
                    start = end + 1;
                    continue;
                }
                if (++recordCount > MaximumRecords) return new RecordScan(null, hasPrefix, result, "record limit exceeded");
                using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(start, end - start));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new RecordScan(null, hasPrefix, result, "non-object record");
                if (root.EnumerateObject().Count(property => property.NameEquals("type")) > 1)
                    return new RecordScan(null, hasPrefix, result, "ambiguous record type");
                if (root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && string.Equals(
                        type.GetString(),
                        "session_meta",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (root.EnumerateObject().Count(property => property.NameEquals("payload")) > 1)
                        return new RecordScan(null, hasPrefix, result, "ambiguous session metadata");
                    JsonElement payload = root.TryGetProperty("payload", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                        ? nested
                        : root;
                    if (payload.TryGetProperty("id", out _) && payload.TryGetProperty("session_id", out _))
                        return new RecordScan(null, hasPrefix, result, "ambiguous session owner aliases");
                    string ownerField = payload.TryGetProperty("id", out JsonElement primary) && primary.ValueKind == JsonValueKind.String
                        ? "id"
                        : "session_id";
                    if (!payload.TryGetProperty(ownerField, out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(id.GetString()))
                        return new RecordScan(null, hasPrefix, result, "missing session owner");
                    if (payload.EnumerateObject().Count(property => property.NameEquals(ownerField)) != 1)
                        return new RecordScan(null, hasPrefix, result, "ambiguous session owner");
                    if (corroboratedFilename && string.Equals(id.GetString(), threadId, StringComparison.OrdinalIgnoreCase))
                        ownedStart ??= start;
                    else if (ownedStart is not null)
                        return new RecordScan(null, hasPrefix, result, "conflicting session owner after ownership began");
                    else
                        hasPrefix = true;
                }
                if (ownedStart is not null) result.Add((start, end + 1 - start));
                else hasPrefix = true;
                start = end + 1;
            }
        }
        catch (JsonException)
        {
            return new RecordScan(null, hasPrefix, result, "malformed or unsupported JSON");
        }
        return new RecordScan(ownedStart, hasPrefix, result, null);
    }

    private sealed record RecordScan(int? OwnedStart, bool HasPrefix, List<(int Start, int Length)> Records, string? Error);
}