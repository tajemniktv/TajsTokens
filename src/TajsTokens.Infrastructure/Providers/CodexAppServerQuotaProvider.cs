using System.Diagnostics;
using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
/// Reads provider-authoritative Codex subscription windows from the local Codex app-server. This is
/// a read-only integration: no model turn is created and no undocumented web endpoint is called.
/// </summary>
public sealed class CodexAppServerQuotaProvider : ICodexQuotaProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        var token = timeoutSource.Token;

        using var process = new Process
        {
            StartInfo = ExternalProcess.CreateStartInfo("codex", ["app-server", "--listen", "stdio://"])
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start Codex app-server.");
            }

            // Keep stderr drained so a noisy app-server cannot block on a full pipe. Provider errors
            // are surfaced through the JSON-RPC response/timeout rather than copied into telemetry.
            _ = process.StandardError.ReadToEndAsync(token);

            await WriteJsonLineAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new { name = "tajs-tokens", title = "TajsTokens", version = "0.1" }
                }
            });
            var initializeResponse = await ReadResponseAsync(process, 0, token);
            EnsureSuccessfulJsonRpcResponse(initializeResponse, "initialize");

            // Match the stable app-server protocol exactly: neither notification nor rate-limit read
            // takes params. In particular, do not send an empty object for account/rateLimits/read.
            await WriteJsonLineAsync(process, new { method = "initialized" });
            await WriteJsonLineAsync(process, new { method = "account/rateLimits/read", id = 1 });
            var response = await ReadResponseAsync(process, 1, token);
            return ParseRateLimitsResponse(response, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Codex app-server did not return rate limits within 20 seconds.");
        }
        finally
        {
            ExternalProcess.TryKill(process);
        }
    }

    internal static void EnsureSuccessfulJsonRpcResponse(string json, string operation)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Codex app-server {operation} response was not a JSON object.");
        }

        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"Codex app-server {operation} failed: {error.GetRawText()}");
        }

        if (!root.TryGetProperty("result", out _))
        {
            throw new InvalidOperationException($"Codex app-server {operation} response did not contain a result.");
        }
    }

    internal static IReadOnlyList<QuotaSnapshot> ParseRateLimitsResponse(string json, DateTimeOffset capturedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"Codex app-server returned an error: {error.GetRawText()}");
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex app-server response did not contain a result object.");
        }

        JsonElement limits = default;
        var source = "codex-app-server";

        // Newer app-server versions may expose several independent metered products. Only an
        // explicitly named Codex bucket is safe to treat as Codex quota. Never guess from object
        // enumeration order; when that bucket is absent, fall back only to the legacy Codex view.
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimit) && byLimit.ValueKind == JsonValueKind.Object)
        {
            if (byLimit.TryGetProperty("codex", out var codexLimits))
            {
                if (codexLimits.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Codex app-server returned a malformed 'codex' rate-limit bucket.");
                }

                limits = codexLimits;
                source += ":codex";
            }
            else if (result.TryGetProperty("rateLimits", out var legacyLimits) && legacyLimits.ValueKind == JsonValueKind.Object)
            {
                limits = legacyLimits;
                source += ":legacy";
            }
        }
        else if (result.TryGetProperty("rateLimits", out var legacyLimits) && legacyLimits.ValueKind == JsonValueKind.Object)
        {
            limits = legacyLimits;
            source += ":legacy";
        }

        if (limits.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Codex app-server response did not contain an identifiable Codex rate-limit view.");
        }

        var snapshots = new List<QuotaSnapshot>(2);
        AddWindow(limits, "primary", snapshots, capturedAtUtc, source);
        AddWindow(limits, "secondary", snapshots, capturedAtUtc, source);

        // Some app-server versions have moved weekly into primary and omitted secondary. Window
        // duration, not primary/secondary position, is the semantic identity we trust.
        return snapshots
            .GroupBy(snapshot => snapshot.Kind)
            .Select(group => group.First())
            .ToArray();
    }

    private static void AddWindow(
        JsonElement limits,
        string propertyName,
        ICollection<QuotaSnapshot> snapshots,
        DateTimeOffset capturedAtUtc,
        string source)
    {
        if (!limits.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var usedPercent = ReadDouble(window, "usedPercent");
        var minutes = ReadInt(window, "windowDurationMins") ?? ReadInt(window, "windowMinutes");
        var resetsAtSeconds = ReadLong(window, "resetsAt");
        DateTimeOffset? resetsAt = resetsAtSeconds is long seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

        var kind = minutes switch
        {
            300 => QuotaWindowKind.FiveHour,
            10_080 => QuotaWindowKind.Weekly,
            _ => QuotaWindowKind.Unknown
        };

        snapshots.Add(new QuotaSnapshot(
            kind,
            capturedAtUtc,
            usedPercent,
            minutes,
            resetsAt,
            "codex",
            "default",
            source));
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value) ? value : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : null;
    }

    private static async Task WriteJsonLineAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<string> ReadResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidOperationException("Codex app-server closed stdout before returning the requested response.");
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number &&
                    responseId.TryGetInt32(out var parsedId) &&
                    parsedId == id)
                {
                    return line;
                }
            }
            catch (JsonException)
            {
                // Ignore non-protocol diagnostic lines. Provider health will still report a failure if
                // the expected response never arrives before the timeout.
            }
        }
    }
}
