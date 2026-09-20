// Taj's Tokens | CodexResponseUsageComparison.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;

#endregion

namespace TajsTokens.Infrastructure.Ingestion;

/// <summary>Per-file, read-only comparison. Identity stays in memory; counts are not token accounting.</summary>
public sealed class CodexResponseUsageComparison
{
    private readonly Dictionary<(string Thread, string Response), string> _seen = [];
    private long _pending;
    private long[]? _usage;
    public Dictionary<string, long> Counts { get; } = [];

    public void Observe(string json, string owner)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        string? type = Text(root, "type");
        if (!root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object) return;
        string? nested = Text(payload, "type");
        if (type == "token_usage_record")
        {
            Count("response-records");
            _pending++;
            _usage = null;
            string? thread = Text(payload, "thread_id");
            string? response = Text(payload, "response_id");
            string? turn = Text(payload, "turn_id");
            string? session = Text(payload, "session_id");
            string? rootTurn = Text(payload, "root_turn_id");
            if (new[] { thread, response, turn, session, rootTurn }.Any(string.IsNullOrWhiteSpace))
            {
                Count("missing-identity");
                return;
            }
            if (!string.Equals(thread, owner, StringComparison.Ordinal))
            {
                Count("owner-conflict");
                return;
            }
            long[]? usage = Vector(payload, "usage");
            if (usage is null)
            {
                Count("invalid-response-vector");
                return;
            }
            // Include lineage dimensions in conflicts, but never publish these values or hashes.
            string signature = JsonSerializer.Serialize(new { turn, session, rootTurn, usage });
            if (_seen.TryGetValue((thread!, response!), out string? previous))
            {
                Count(previous == signature ? "repeated-response-key" : "conflicting-response-key");
                return;
            }
            _seen.Add((thread!, response!), signature);
            _usage = usage;
            return;
        }
        if (type == "compacted" || type == "session_meta" || nested is "task_started" or
                "task_complete" or "turn_aborted" or "thread_settings_applied" or "context_compaction" or "compacted")
        {
            Flush("response-records-unpaired-at-boundary");
            return;
        }
        if (nested != "token_count") return;
        Count("legacy-token-records");
        if (_pending == 0)
        {
            Count("legacy-without-pending-response");
        }
        else if (_pending > 1)
        {
            Count("ambiguous-multiple-response-pair");
        }
        else if (_usage is null)
        {
            Count("ineligible-response-pair");
        }
        else
        {
            long[]? last = payload.TryGetProperty("info", out JsonElement info) ? Vector(info, "last_token_usage") : null;
            Count(
                last is null
                    ? "invalid-legacy-vector-pair"
                    : _usage.SequenceEqual(last)
                        ? "exact-vector-pair"
                        : "different-vector-pair");
        }
        _pending = 0;
        _usage = null;
    }

    public void Complete()
    {
        Flush("response-records-unpaired-at-end");
    }

    private void Flush(string reason)
    {
        if (_pending > 0) Counts[reason] = Counts.GetValueOrDefault(reason) + _pending;
        _pending = 0;
        _usage = null;
    }

    private void Count(string key)
    {
        Counts[key] = Counts.GetValueOrDefault(key) + 1;
    }

    private static string? Text(JsonElement value, string key)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(key, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;
    }

    private static long[]? Vector(JsonElement parent, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(key, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object) return null;
        string[] fields =
        [
            "input_tokens", "cached_input_tokens", "cache_write_input_tokens",
            "output_tokens", "reasoning_output_tokens", "total_tokens",
        ];
        long[] result = new long[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            // Upstream explicitly defaults only absent cache-write to zero. Null is not absence.
            if (!value.TryGetProperty(fields[i], out JsonElement field))
            {
                if (i == 2) continue;
                return null;
            }
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out result[i]) || result[i] < 0) return null;
        }
        return result;
    }
}