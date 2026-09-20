// Taj's Tokens | TokscaleProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
///     Bootstrap token-accounting provider backed by Tokscale's documented machine-readable CLI.
///     TajsTokens intentionally keeps this adapter at the process/JSON boundary so native accounting
///     can later run beside it for reconciliation without coupling the domain model to Tokscale.
/// </summary>
public sealed class TokscaleProvider : ITokscaleProvider
{
    private static readonly TimeSpan s_commandTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan s_npxCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly string[] s_tokenPropertyNames = ["input", "cacheRead", "cacheWrite", "output", "reasoning", "total"];

    public async Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
    {
        ExternalCommandResult result = await RunTokscaleAsync(
            ["models", "--json", "--group-by", "client,model", "--client", "codex"],
            cancellationToken);

        EnsureSuccess(result, "Tokscale model usage");
        return ParseModelUsageJson(result.StandardOutput, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
    {
        ExternalCommandResult result = await RunTokscaleAsync(
            ["hourly", "--json", "--client", "codex"],
            cancellationToken);

        EnsureSuccess(result, "Tokscale hourly usage");
        return ParseHourlyJson(result.StandardOutput);
    }

    internal static IReadOnlyList<TokenUsage> ParseModelUsageJson(string json, DateTimeOffset observedAtUtc)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        IReadOnlyList<JsonElement> entries = FindEntries(document.RootElement);
        var results = new List<TokenUsage>();

        foreach (JsonElement entry in entries)
        {
            EnsureObjectRow(entry, "model");
            ValidateTokenFields(entry, "model");

            string? model = ReadString(entry, "model");
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new JsonException("Tokscale model row did not contain a non-empty model identifier.");
            }

            string client = ReadString(entry, "client") ?? "codex";
            if (!string.Equals(client, "codex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TokenBreakdown breakdown = ReadBreakdown(entry);
            results.Add(
                new TokenUsage(
                    "tokscale",
                    client,
                    model,
                    observedAtUtc,
                    breakdown,
                    ReadString(entry, "profile") ?? "default",
                    ReadString(entry, "sessionId")));
        }

        return results;
    }

    internal static IReadOnlyList<TokenTimeBucket> ParseHourlyJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        IReadOnlyList<JsonElement> entries = FindEntries(document.RootElement);
        var results = new List<TokenTimeBucket>();

        foreach (JsonElement entry in entries)
        {
            EnsureObjectRow(entry, "hourly");
            ValidateTokenFields(entry, "hourly");

            string label = ReadBucketLabel(entry);
            DateTimeOffset? startUtc = ReadBucketTimestamp(entry);
            results.Add(new TokenTimeBucket("tokscale", label, startUtc, ReadBreakdown(entry)));
        }

        return results;
    }

    internal static bool LooksLikeCommandNotFound(ExternalCommandResult result, string command)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }

        // Exit codes such as 127/9009 are not enough by themselves: a real Tokscale invocation or
        // one of its dependencies can legitimately fail with the same code. Require diagnostics that
        // identify the command we attempted to launch before treating the result as discovery failure.
        string commandName = Path.GetFileName(command);
        string detail = $"{result.StandardError}\n{result.StandardOutput}";
        return detail.Contains($"'{commandName}' is not recognized", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains($"\"{commandName}\" is not recognized", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains($"{commandName}: command not found", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains($"{commandName}: not found", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains($"not found: {commandName}", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ExternalCommandResult> RunTokscaleAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            ExternalCommandResult direct = await ExternalProcess.RunToCompletionAsync(
                "tokscale",
                arguments,
                s_commandTimeout,
                cancellationToken);

            if (direct.ExitCode == 0 || !LooksLikeCommandNotFound(direct, "tokscale"))
            {
                return direct;
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            // No native/global Tokscale executable. The documented zero-install path is npx.
        }

        var npxArguments = new List<string>(arguments.Count + 2) { "--yes", "tokscale@latest" };
        npxArguments.AddRange(arguments);

        try
        {
            ExternalCommandResult fallback = await ExternalProcess.RunToCompletionAsync(
                "npx",
                npxArguments,
                s_npxCommandTimeout,
                cancellationToken);

            if (LooksLikeCommandNotFound(fallback, "npx"))
            {
                throw CreateNpxUnavailableException();
            }

            return fallback;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            throw CreateNpxUnavailableException(exception);
        }
    }

    private static InvalidOperationException CreateNpxUnavailableException(Exception? innerException = null)
    {
        return new InvalidOperationException(
            "Tokscale is not installed globally and the npx fallback is unavailable. Install Tokscale or Node.js/npm, or make either command available on PATH.",
            innerException);
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

        foreach (string propertyName in new[] { "entries", "hours", "data" })
        {
            if (root.TryGetProperty(propertyName, out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
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
        bool foundAny = false;
        foreach (string propertyName in s_tokenPropertyNames)
        {
            if (!entry.TryGetProperty(propertyName, out JsonElement property))
            {
                continue;
            }

            foundAny = true;
            if (!TryReadLongValue(property, out long value) || value < 0)
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
        long input = ReadLong(entry, "input");
        long cacheRead = ReadLong(entry, "cacheRead");
        long cacheWrite = ReadLong(entry, "cacheWrite");
        long output = ReadLong(entry, "output");
        long reasoning = ReadLong(entry, "reasoning");
        long? reportedTotal = ReadNullableLong(entry, "total");

        // Current Tokscale JSON exposes disjoint output/reasoning buckets. Older builds briefly
        // exposed output inclusive of reasoning; normalize that shape when the reported total makes
        // the relationship unambiguous instead of double counting it.
        long nonReasoningOutput = output;
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
        string? date = ReadString(entry, "date");
        string? hour;
        if (entry.TryGetProperty("hour", out JsonElement hourElement) && hourElement.ValueKind == JsonValueKind.Number &&
            hourElement.TryGetInt32(out int numericHour))
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

        string? label = (date, hour) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{date} {hour}",
            (_, { Length: > 0 }) => hour,
            _ => ReadString(entry, "label") ??
                 ReadString(entry, "timestamp") ?? ReadString(entry, "startUtc") ?? ReadString(entry, "start"),
        };

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new JsonException("Tokscale hourly row did not contain a supported time-bucket identifier.");
        }

        return label;
    }

    private static DateTimeOffset? ReadBucketTimestamp(JsonElement entry)
    {
        foreach (string propertyName in new[] { "timestamp", "startUtc", "start" })
        {
            string? value = ReadString(entry, propertyName);
            if (value is not null && DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        return ReadNullableLong(element, propertyName) ?? 0;
    }

    private static long? ReadNullableLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return TryReadLongValue(property, out long value) ? value : null;
    }

    private static bool TryReadLongValue(JsonElement property, out long value)
    {
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String && long.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value))
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

        string detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        detail = detail.Trim();
        if (detail.Length > 500)
        {
            detail = detail[..500];
        }

        throw new InvalidOperationException($"{operation} failed with exit code {result.ExitCode}: {detail}");
    }
}