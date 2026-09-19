using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

internal static class CodexResponseObservationParser
{
    public static CodexResponseObservation Parse(JsonElement payload, RawSessionRecord raw,
        string sourceRecordId, string sourceIdentity, string owner, DateTimeOffset? observed)
    {
        var diagnostics = new List<string>();
        string? Id(string key)
        {
            if (!payload.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 512)
            { diagnostics.Add(key + ".unknown"); return null; }
            return value.GetString();
        }
        CodexResponseSnapshot? Snapshot(string key)
        {
            if (!payload.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object)
            { diagnostics.Add(key + ".unknown"); return null; }
            long? Counter(string field, bool defaultMissing = false)
            {
                if (!value.TryGetProperty(field, out var number))
                {
                    if (defaultMissing) return 0;
                    diagnostics.Add(key + "." + field + ".unknown"); return null;
                }
                if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out var count) || count < 0)
                { diagnostics.Add(key + "." + field + ".invalid"); return null; }
                return count;
            }
            var counters = new CodexTokenUsageSnapshot(Counter("input_tokens"), Counter("cached_input_tokens"),
                Counter("cache_write_input_tokens", true), Counter("output_tokens"),
                Counter("reasoning_output_tokens"), Counter("total_tokens"));
            if (counters.IsComplete && ((decimal)counters.InputTokens!.Value + counters.OutputTokens!.Value != counters.TotalTokens ||
                (decimal)counters.CachedInputTokens!.Value + counters.CacheWriteInputTokens!.Value > counters.InputTokens ||
                counters.ReasoningOutputTokens > counters.OutputTokens)) diagnostics.Add(key + ".category-mismatch");
            return new(counters, !value.TryGetProperty("cache_write_input_tokens", out _));
        }
        var thread = Id("thread_id");
        var turn = Id("turn_id"); var root = Id("root_turn_id");
        var session = Id("session_id"); var response = Id("response_id");
        if (thread is not null && thread != owner) diagnostics.Add("thread_id.owner-conflict");
        if (observed is null) diagnostics.Add("timestamp.unknown");
        var usage = Snapshot("usage"); var turnUsage = Snapshot("turn_token_usage"); var threadUsage = Snapshot("thread_token_usage");
        return new(sourceRecordId, sourceIdentity, raw.FilePath, raw.StartByteOffset, raw.EndByteOffset,
            owner, observed, DateTimeOffset.UtcNow, thread, turn, root, session, response,
            usage, turnUsage, threadUsage, string.Join(';', diagnostics));
    }
}
