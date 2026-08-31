using System.ComponentModel;
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
    private static readonly TimeSpan NpxCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly string[] TokenPropertyNames = ["input", "cacheRead", "cacheWrite", "output", "reasoning", "total"];

    public async Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
    {
        var result = await RunTokscaleAsync(
            ["models", "--json", "--group-by", "client,model", "--client", "codex"],
            cancellationToken);

        EnsureSuccess(result, "Tokscale model usage");
        return ParseModelUsageJson(result.StandardOutput, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
    {
        var result = await RunTokscaleAsync(
            ["hourly", "--json", "--client", "codex"],
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
            EnsureObjectRow(entry, "model");
            ValidateTokenFields(entry, "model");

            var model = ReadString(entry, "model");
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new JsonException("Tokscale model row did not contain a non-empty model identifier.");
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
                model,
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
            EnsureObjectRow(entry, "hourly");
            ValidateTokenFields(entry, "hourly");

            var label = ReadBucketLabel(entry);
            var startUtc = ReadBucketTimestamp(entry);
            results.Add(new TokenTimeBucket("tokscale", label, startUtc, ReadBreakdown(entry)));
        }

        return results;
    }

    internal static bool LooksLikeCommandNotFound(ExternalCommandResult result, string command)
    {
        if (result.ExitCode == 127 || result.ExitCode == 9009)
        {
            return true;
        }

        var detail = $"{result.StandardError}\n{result.StandardOutput}";
        return detail.Contains($"'{command}' is not recognized", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains($"{command}: command not found", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains($"{command}: not found", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ExternalCommandResult> RunTokscaleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var direct = await ExternalProcess.RunToCompletionAsync(
                "tokscale",
                arguments,
                CommandTimeout,
                cancellationToken);

            if (direct.ExitCode == 0 || !LooksLikeCommandNotFound(direct, "tokscale"))
            {
                return direct;
            }
        }
        catch (Win32Exception)
        {
            // No native/global Tokscale executable. The documented zero-install path is npx.
        }

        var npxArguments = new List<string>(arguments.Count + 2) { "--yes", "tokscale@latest" };
        npxArguments.AddRange(arguments);

        try
        {
            return await ExternalProcess.RunToCompletionAsync(
                "npx",
                npxArguments,
                NpxCommandTimeout,
                cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Tokscale is not installed globally and the npx fallback is unavailable. Install Tokscale or Node.js/npm, or make either command available on PATH.",
                exception);
        }
    }

    private static IReadOnlyList<JsonElement> FindEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Tokscale payload root was neither an array nor a supported object.");
        }

        foreach (var propertyName in new[] { "entries", "hours", "data" })
        {
            if (root.TryGetProperty(propertyName, out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                return entries.EnumerateArray().ToArray();
            }
        }

        throw new JsonException("Tokscale payload did not contain a supported entry array.");
    }

    private static void EnsureObjectRow(JsonElement entry, string rowKind)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Tokscale {rowKind} entry was not an object.");
        }
    }

    private static void ValidateTokenFields(JsonElement entry, string rowKind)
    {
        var foundAny = false;
        foreach (var propertyName in TokenPropertyNames)
        {
            if (!entry.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            foundAny = true;
            if (!TryReadLongValue(property, out var value) || value < 0)
            {
                throw new JsonException($"Tokscale {rowKind} row contained an invalid '{propertyName}' token value.");
            }
        }

        if (!foundAny)
        {
            throw new JsonException($"Tokscale {rowKind} row did not contain any recognized token fields.");
        }
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
        string? hour;
        if (entry.TryGetProperty("hour", out var hourElement) && hourElement.ValueKind == JsonValueKind.Number && hourElement.TryGetInt32(out var numericHour))
        {
            if (numericHour is < 0 or > 23)
            {
                throw new JsonException("Tokscale hourly row contained an hour outside the 0-23 range.");
            }

            hour = $"{numericHour:00}:00";
        }
        else
        {
            hour = ReadString(entry, "hour");
        }

        var label = (date, hour) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{date} {hour}",
            (_, { Length: > 0 }) => hour,
            _ => ReadString(entry, "label") ?? ReadString(entry, "timestamp") ?? ReadString(entry, "startUtc") ?? ReadString(entry, "start")
        };

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new JsonException("Tokscale hourly row did not contain a supported time-bucket identifier.");
        }

        return label;
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

        return TryReadLongValue(property, out var value) ? value : null;
    }

    private static bool TryReadLongValue(JsonElement property, out long value)
    {
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return false;
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
