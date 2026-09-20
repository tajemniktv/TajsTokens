// Taj's Tokens | CodexResponseObservationParser.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Ingestion;

internal static class CodexResponseObservationParser
{
    public static CodexResponseObservation Parse(
        JsonElement payload,
        RawSessionRecord raw,
        string sourceRecordId,
        string sourceIdentity,
        string owner,
        DateTimeOffset? observed)
    {
        var diagnostics = new List<string>();

        string? Id(string key)
        {
            if (!payload.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 512)
            {
                diagnostics.Add(key + ".unknown");
                return null;
            }
            return value.GetString();
        }

        CodexResponseSnapshot? Snapshot(string key)
        {
            if (!payload.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(key + ".unknown");
                return null;
            }

            long? Counter(string field, bool defaultMissing = false)
            {
                if (!value.TryGetProperty(field, out JsonElement number))
                {
                    if (defaultMissing) return 0;
                    diagnostics.Add(key + "." + field + ".unknown");
                    return null;
                }
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out long count) || count < 0)
                {
                    diagnostics.Add(key + "." + field + ".invalid");
                    return null;
                }
                return count;
            }

            var counters = new CodexTokenUsageSnapshot(
                Counter("input_tokens"),
                Counter("cached_input_tokens"),
                Counter("cache_write_input_tokens", true),
                Counter("output_tokens"),
                Counter("reasoning_output_tokens"),
                Counter("total_tokens"));
            if (counters.IsComplete && ((decimal)counters.InputTokens!.Value + counters.OutputTokens!.Value != counters.TotalTokens ||
                                        (decimal)counters.CachedInputTokens!.Value + counters.CacheWriteInputTokens!.Value >
                                        counters.InputTokens ||
                                        counters.ReasoningOutputTokens > counters.OutputTokens))
                diagnostics.Add(key + ".category-mismatch");
            return new CodexResponseSnapshot(counters, !value.TryGetProperty("cache_write_input_tokens", out _));
        }

        string? thread = Id("thread_id");
        string? turn = Id("turn_id");
        string? root = Id("root_turn_id");
        string? session = Id("session_id");
        string? response = Id("response_id");
        if (thread is not null && thread != owner) diagnostics.Add("thread_id.owner-conflict");
        if (observed is null) diagnostics.Add("timestamp.unknown");
        CodexResponseSnapshot? usage = Snapshot("usage");
        CodexResponseSnapshot? turnUsage = Snapshot("turn_token_usage");
        CodexResponseSnapshot? threadUsage = Snapshot("thread_token_usage");
        return new CodexResponseObservation(
            sourceRecordId,
            sourceIdentity,
            raw.FilePath,
            raw.StartByteOffset,
            raw.EndByteOffset,
            owner,
            observed,
            DateTimeOffset.UtcNow,
            thread,
            turn,
            root,
            session,
            response,
            usage,
            turnUsage,
            threadUsage,
            string.Join(';', diagnostics));
    }
}