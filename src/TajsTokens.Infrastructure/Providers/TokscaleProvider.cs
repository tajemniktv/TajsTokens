using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
/// Bootstrap token-accounting provider backed by Tokscale's documented machine-readable CLI.
/// TajsTokens intentionally keeps this adapter at the process/JSON boundary so native accounting
/// can later run beside it for reconciliation without coupling the domain model to Tokscale.
/// </summary>
public sealed class TokscaleProvider : ITokscaleProvider
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    public async Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunToCompletionAsync(
            "tokscale",
            ["models", "--json", "--group-by", "client,model", "--client", "codex"],
            CommandTimeout,
            cancellationToken);

        EnsureSuccess(result, "Tokscale model usage");
        return ParseModelUsageJson(result.StandardOutput, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunToCompletionAsync(
            "tokscale",
            ["hourly", "--json", "--client", "codex"],
            CommandTimeout,
            cancellationToken);

        EnsureSuccess(result, "Tokscale hourly usage");
        return ParseHourlyJson(result.StandardOutput);
    }

    internal static IReadOnlyList<TokenUsage> ParseModelUsageJson(string json, DateTimeOffset observedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var entries = FindEntries(document.RootElement);
        var results = new List<TokenUsage>();

        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var client = ReadString(entry, "client") ?? "codex";
            if (!string.Equals(client, "codex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var breakdown = ReadBreakdown(entry);
            results.Add(new TokenUsage(
                "tokscale",
                client,
                ReadString(entry, "model") ?? "unknown",
                observedAtUtc,
                breakdown,
                Profile: ReadString(entry, "profile") ?? "default",
                SessionId: ReadString(entry, "sessionId")));
        }

        return results;
    }

    internal static IReadOnlyList<TokenTimeBucket> ParseHourlyJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = FindEntries(document.RootElement);
        var results = new List<TokenTimeBucket>();

        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var label = ReadBucketLabel(entry);
            var startUtc = ReadBucketTimestamp(entry);
            results.Add(new TokenTimeBucket(label, startUtc, ReadBreakdown(entry)));
        }

        return results;
    }

    private static IReadOnlyList<JsonElement> FindEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var propertyName in new[] { "entries", "hours", "data" })
        {
            if (root.TryGetProperty(propertyName, out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                return entries.EnumerateArray().ToArray();
            }
        }

        return [];
    }

    private static TokenBreakdown ReadBreakdown(JsonElement entry)
    {
        var input = ReadLong(entry, "input");
        var cacheRead = ReadLong(entry, "cacheRead");
        var cacheWrite = ReadLong(entry, "cacheWrite");
        var output = ReadLong(entry, "output");
        var reasoning = ReadLong(entry, "reasoning");
        var reportedTotal = ReadNullableLong(entry, "total");

        // Current Tokscale JSON exposes disjoint output/reasoning buckets. Older builds briefly
        // exposed output inclusive of reasoning; normalize that shape when the reported total makes
        // the relationship unambiguous instead of double counting it.
        var nonReasoningOutput = output;
        if (reportedTotal is long total &&
            checked(input + cacheRead + cacheWrite + output + reasoning) > total &&
            checked(input + cacheRead + cacheWrite + output) == total)
        {
            nonReasoningOutput = Math.Max(0, output - reasoning);
        }

        return new TokenBreakdown(input, cacheRead, cacheWrite, nonReasoningOutput, reasoning, reportedTotal);
    }

    private static string ReadBucketLabel(JsonElement entry)
    {
        var date = ReadString(entry, "date");
        var hour = ReadString(entry, "hour");
        if (hour is null && entry.TryGetProperty("hour", out var hourElement) && hourElement.ValueKind == JsonValueKind.Number)
        {
            hour = $"{hourElement.GetInt32():00}:00";
        }

        return (date, hour) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{date} {hour}",
            (_, { Length: > 0 }) => hour,
            _ => ReadString(entry, "label") ?? ReadString(entry, "timestamp") ?? "hour"
        };
    }

    private static DateTimeOffset? ReadBucketTimestamp(JsonElement entry)
    {
        foreach (var propertyName in new[] { "timestamp", "startUtc", "start" })
        {
            var value = ReadString(entry, propertyName);
            if (value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static long ReadLong(JsonElement element, string propertyName) => ReadNullableLong(element, propertyName) ?? 0;

    private static long? ReadNullableLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static void EnsureSuccess(ExternalCommandResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        detail = detail.Trim();
        if (detail.Length > 500)
        {
            detail = detail[..500];
        }

        throw new InvalidOperationException($"{operation} failed with exit code {result.ExitCode}: {detail}");
    }
}
