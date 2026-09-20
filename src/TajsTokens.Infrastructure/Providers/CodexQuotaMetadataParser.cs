// Taj's Tokens | CodexQuotaMetadataParser.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Allowlisted schema evidence. A metadata parse failure must not hide usable quota windows.</summary>
internal static class CodexQuotaMetadataParser
{
    internal static CodexServerObservation Parse(JsonElement result, string? account, DateTimeOffset captured)
    {
        var observation = new CodexServerObservation(
            Guid.NewGuid().ToString("N"),
            CodexServerSurface.QuotaMetadata,
            null,
            captured,
            captured,
            CodexServerEvidenceParser.Contract,
            null,
            ServerEvidenceState.Available,
            "App-server named-limit metadata; balances and spend controls are not workload credits.")
        {
            CorrelatedAccountKey = account,
            AccountEvidence = account is null ? AccountEvidenceClass.Unattributed : AccountEvidenceClass.ProviderVerified,
        };
        try
        {
            CodexDailyReportParser.Validate(result);
            var limits = new List<CodexQuotaLimitMetadata>();
            if (Object(result, "rateLimitsByLimitId") is { } map)
                foreach (JsonProperty property in map.EnumerateObject())
                {
                    if (limits.Count >= 64 || property.Name.Length > 256 || property.Name.Any(char.IsControl)) throw new JsonException();
                    if (property.Value.ValueKind == JsonValueKind.Null) continue;
                    limits.Add(Read("named:" + property.Name, property.Value));
                }
            if (Object(result, "rateLimits") is { } legacy) limits.Add(Read("legacy", legacy));
            return observation with { QuotaMetadata = new CodexQuotaMetadataReport("codex-app-server-quota-metadata/v1", limits) };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or OverflowException)
        {
            return observation with
            {
                State = ServerEvidenceState.Invalid,
                Detail = "Quota metadata failed bounded schema validation; current-window parsing is independent.",
            };
        }
    }

    private static CodexQuotaLimitMetadata Read(string key, JsonElement root)
    {
        JsonElement? credits = Object(root, "credits");
        JsonElement? individual = Object(root, "individualLimit");
        return new CodexQuotaLimitMetadata(
            key,
            Text(root, "limitId"),
            Text(root, "limitName"),
            Text(root, "planType"),
            Text(root, "rateLimitReachedType"),
            Boolean(root, "spendControlReached"),
            Text(root, "normalModelSlug"),
            credits is { } c ? new CodexQuotaCredits(Boolean(c, "hasCredits"), Boolean(c, "unlimited"), Text(c, "balance")) : null,
            individual is { } i
                ? new CodexSpendControlLimit(Text(i, "limit"), Text(i, "used"), Integer(i, "remainingPercent"), Integer(i, "resetsAt"))
                : null,
            Window(root, "primary"),
            Window(root, "secondary"));
    }

    private static CodexQuotaReportedWindow? Window(JsonElement root, string name)
    {
        if (Object(root, name) is not { } w) return null;
        double? used = null;
        if (w.TryGetProperty("usedPercent", out JsonElement v) && v.ValueKind != JsonValueKind.Null)
        {
            if (!v.TryGetDouble(out double n) || !double.IsFinite(n)) throw new JsonException();
            used = n;
        }
        return new CodexQuotaReportedWindow(used, Integer(w, "windowDurationMins") ?? Integer(w, "windowMinutes"), Integer(w, "resetsAt"));
    }

    private static string? Text(JsonElement root, string key)
    {
        return CodexDailyReportParser.Text(root, key);
    }

    private static JsonElement? Object(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        return value;
    }

    private static long? Integer(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt64(out long n)) throw new JsonException();
        return n;
    }

    private static bool? Boolean(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new JsonException() };
    }
}