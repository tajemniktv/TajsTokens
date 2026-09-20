// Taj's Tokens | CodexDailyReportParser.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>Allowlisted experimental reports; missing and zero remain distinct.</summary>
public static class CodexDailyReportParser
{
    public const string Contract = "codex-private-daily/v1";

    public static CodexDailyReport Parse(string json, bool relative, string endpoint, string start, string end)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Validate(root);
        JsonElement data = root.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 32) throw new JsonException();
        var days = new List<CodexDailyReportRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement day in data.EnumerateArray())
        {
            string date = Text(day, "date") ?? throw new JsonException();
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                string.CompareOrdinal(date, start) < 0 || string.CompareOrdinal(date, end) > 0 ||
                !seen.Add(date)) throw new JsonException();
            if (relative)
            {
                JsonElement values = day.GetProperty("product_surface_usage_values");
                if (values.ValueKind != JsonValueKind.Object) throw new JsonException();
                var surfaces = new Dictionary<string, decimal>(StringComparer.Ordinal);
                foreach (JsonProperty value in values.EnumerateObject())
                {
                    if (value.Name.Length > 128 || surfaces.Count >= 64) throw new JsonException();
                    surfaces.Add(value.Name, Decimal(values, value.Name) ?? throw new JsonException());
                }
                days.Add(new CodexDailyReportRow(date, null, null, null, null, null, null, surfaces));
            }
            else
            {
                JsonElement totals = day.GetProperty("totals");
                days.Add(
                    new CodexDailyReportRow(
                        date,
                        Decimal(totals, "credits"),
                        Decimal(totals, "on_demand_credits"),
                        Integer(totals, "uncached_text_input_tokens"),
                        Integer(totals, "cached_text_input_tokens"),
                        Integer(totals, "text_output_tokens"),
                        Integer(totals, "text_total_tokens"),
                        null));
            }
        }
        return new CodexDailyReport(
            Contract,
            endpoint,
            start,
            end,
            Text(root, relative ? "units" : "balance_unit"),
            Text(root, "group_by"),
            Text(root, "data_freshness_ts"),
            null,
            null,
            null,
            days);
    }

    internal static string? Text(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException();
        string text = value.GetString()!;
        if (text.Length > 256 || text.Any(char.IsControl)) throw new JsonException();
        return text;
    }

    private static decimal? Decimal(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetDecimal(out decimal number) || number < 0) throw new JsonException();
        return number;
    }

    private static long? Integer(JsonElement root, string key)
    {
        decimal? value = Decimal(root, key);
        if (value is null) return null;
        if (value > long.MaxValue || decimal.Truncate(value.Value) != value) throw new JsonException();
        return (long)value;
    }

    internal static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                Validate(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) Validate(item);
        }
    }
}