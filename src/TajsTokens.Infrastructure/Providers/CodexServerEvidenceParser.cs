// Taj's Tokens | CodexServerEvidenceParser.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Strict allowlist projection. Missing counters stay null; invalid or unbounded reports fail closed.</summary>
public static class CodexServerEvidenceParser
{
    public const string Contract = "codex-server-evidence/v1";

    public static CodexServerObservation ParseUsage(
        string json,
        string? threadId,
        DateTimeOffset started,
        DateTimeOffset collected,
        string? clientVersion = null)
    {
        CodexServerObservation observation = New(
            threadId is null ? CodexServerSurface.AccountActivity : CodexServerSurface.ThreadUsage,
            threadId,
            started,
            collected,
            clientVersion);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("error", out JsonElement error)) return Error(observation, error);
            JsonElement result = Required(root, "result", JsonValueKind.Object);
            if (threadId is not null)
            {
                if (!result.TryGetProperty("threadUsage", out JsonElement usage) || usage.ValueKind == JsonValueKind.Null)
                    return observation with
                    {
                        State = ServerEvidenceState.Unavailable,
                        Detail = "Thread estimate absent; billing route may be unavailable. Not zero.",
                    };
                usage = Required(result, "threadUsage", JsonValueKind.Object);
                string id = Text(usage, "threadId") ?? throw new JsonException();
                if (id != threadId)
                    return observation with
                    {
                        State = ServerEvidenceState.Conflict, Detail = "Returned thread identity does not match the request.",
                    };
                CodexThreadUsageGroup[] groups = Array(usage, "groups", 512).Select(g => new CodexThreadUsageGroup(
                    Text(g, "model"),
                    Text(g, "reasoningEffort"),
                    Text(g, "speed"),
                    Number(g, "estimatedUsageCreditsMicros") ?? throw new JsonException(),
                    Number(g, "netNewInputTokens"),
                    Number(g, "cachedInputTokens"),
                    Number(g, "inputTokens"),
                    Number(g, "outputTokens"),
                    Number(g, "totalTokens"))).ToArray();
                return observation with
                {
                    State = ServerEvidenceState.Available,
                    Detail = "Provider-estimated thread credits; not subscription quota percentage.",
                    ThreadUsage = new CodexThreadUsage(
                        id,
                        Number(usage, "estimatedUsageCreditsMicros") ?? throw new JsonException(),
                        Number(usage, "estimatedUsageUsdMicros"),
                        groups),
                };
            }
            JsonElement summary = Required(result, "summary", JsonValueKind.Object);
            CodexAccountDay[]? days = null;
            if (result.TryGetProperty("dailyUsageBuckets", out JsonElement daily) && daily.ValueKind != JsonValueKind.Null)
            {
                days = Array(result, "dailyUsageBuckets", 4000).Select(d =>
                {
                    string date = Text(d, "startDate") ?? throw new JsonException();
                    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        throw new JsonException();
                    return new CodexAccountDay(date, Number(d, "tokens") ?? throw new JsonException());
                }).ToArray();
                if (days.Select(x => x.StartDate).Distinct().Count() != days.Length) throw new JsonException();
            }
            var activity = new CodexAccountActivity(
                Number(summary, "lifetimeTokens"),
                Number(summary, "peakDailyTokens"),
                Number(summary, "longestRunningTurnSec"),
                Number(summary, "currentStreakDays"),
                Number(summary, "longestStreakDays"),
                days);
            bool hasCounters = activity.LifetimeTokens is not null || activity.PeakDailyTokens is not null || days is { Length: > 0 };
            return observation with
            {
                Activity = activity,
                State = hasCounters ? ServerEvidenceState.Available : ServerEvidenceState.Empty,
                Detail = "Backend account activity; coverage/time-zone and equality with local token units are not asserted.",
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or OverflowException or FormatException)
        {
            return observation with
            {
                State = ServerEvidenceState.Invalid, Detail = "Usage response failed the bounded content-free contract.",
            };
        }
    }

    internal static CodexServerObservation New(
        CodexServerSurface surface,
        string? thread,
        DateTimeOffset started,
        DateTimeOffset collected,
        string? version)
    {
        return new CodexServerObservation(
            Guid.NewGuid().ToString("N"),
            surface,
            thread,
            started,
            collected,
            Contract,
            version,
            ServerEvidenceState.Unavailable,
            "No report.");
    }

    private static CodexServerObservation Error(CodexServerObservation observation, JsonElement error)
    {
        int code = error.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int n) ? n : 0;
        string message = error.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? ""
            : "";
        ServerEvidenceState state = code == -32601 ? ServerEvidenceState.Unsupported :
            message.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ? ServerEvidenceState.AuthenticationRequired :
            ServerEvidenceState.Error;
        // Never persist provider error text: it can contain URLs, headers or account identifiers.
        return observation with
        {
            State = state, Detail = $"App-server read failed (JSON-RPC {code}); provider error text was not retained.",
        };
    }

    internal static JsonElement Required(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || parent.EnumerateObject().Count(p => p.NameEquals(name)) != 1 ||
            !parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != kind) throw new JsonException();
        return value;
    }

    internal static JsonElement[] Array(JsonElement parent, string name, int max)
    {
        JsonElement array = Required(parent, name, JsonValueKind.Array);
        if (array.GetArrayLength() > max) throw new JsonException();
        return array.EnumerateArray().ToArray();
    }

    internal static long? Number(JsonElement parent, string name)
    {
        if (parent.EnumerateObject().Count(p => p.NameEquals(name)) > 1) throw new JsonException();
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        value = Required(parent, name, JsonValueKind.Number);
        return value.TryGetInt64(out long n) && n >= 0 ? n : throw new JsonException();
    }

    internal static string? Text(JsonElement parent, string name)
    {
        if (parent.EnumerateObject().Count(p => p.NameEquals(name)) > 1) throw new JsonException();
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        string? text = Required(parent, name, JsonValueKind.String).GetString();
        return text is { Length: > 0 and <= 256 } && !text.Any(char.IsControl) ? text : throw new JsonException();
    }
}