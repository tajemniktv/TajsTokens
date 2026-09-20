// Taj's Tokens | CodexHistoricalAnalyticsParser.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Providers;

public static class CodexHistoricalAnalyticsParser
{
    public const string Contract = "codex-historical-analytics/v1";

    public static CodexPlanHistoryReport ParsePlan(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = doc.RootElement;
        CodexPlanPeriod[] periods = Rows(root, "periods", 1000).Select(p =>
        {
            DateTimeOffset start = Time(p, "starts_at") ?? throw new InvalidDataException();
            DateTimeOffset end = Time(p, "ends_at") ?? throw new InvalidDataException();
            int window = p.GetProperty("window_minutes").GetInt32();
            if (end <= start || window <= 0) throw new InvalidDataException();
            return new CodexPlanPeriod(
                Required(p, "id"),
                window,
                Required(p, "plan_type"),
                start,
                end,
                Bool(p, "accounting_complete"),
                Number(p, "used_basis_points"),
                Rows(p, "breakdowns", 32, true).Select(b => new CodexPlanBreakdown(
                    Required(b, "dimension"),
                    Rows(b, "rows", 2000).Select(v => new CodexPlanValue(
                        Required(v, "key"),
                        Number(v, "basis_points") ?? throw new InvalidDataException())).ToArray())).ToArray());
        }).ToArray();
        if (periods.Select(x => x.Id).Distinct().Count() != periods.Length) throw new InvalidDataException();
        int? tolerance = root.TryGetProperty("boundary_tolerance_seconds", out JsonElement t) && t.ValueKind != JsonValueKind.Null
            ? t.GetInt32()
            : null;
        if (tolerance < 0) throw new InvalidDataException();
        return new CodexPlanHistoryReport(
            Time(root, "data_as_of"),
            Time(root, "coverage_start"),
            Bool(root, "coverage_complete"),
            Bool(root, "approximate") ?? true,
            tolerance,
            periods);
    }

    public static CodexTaskUsageReport ParseTasks(string json, IReadOnlyList<string> requested)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        JsonElement root = doc.RootElement;
        CodexTaskUsage[] rows = Rows(root, "threads", 100).Select(x => new CodexTaskUsage(
            Required(x, "thread_id"),
            Required(x, "data_status"),
            Required(x, "usage_source"),
            Amounts(x),
            Rows(x, "groups", 2000).Select(g => new CodexTaskGroup(
                Text(g, "product_experience"),
                Text(g, "model"),
                Text(g, "reasoning_effort"),
                Text(g, "speed"),
                Amounts(g))).ToArray())).ToArray();
        if (rows.Select(x => x.ThreadId).Distinct().Count() != rows.Length || rows.Any(x => !requested.Contains(x.ThreadId)))
            throw new InvalidDataException();
        return new CodexTaskUsageReport(Time(root, "data_as_of"), requested.ToArray(), rows);
    }

    private static CodexTaskAmounts Amounts(JsonElement x)
    {
        return new CodexTaskAmounts(
            Number(x, "five_hour_limit_percent"),
            Number(x, "weekly_limit_percent"),
            Number(x, "balance_usage_credits"));
    }

    private static JsonElement[] Rows(JsonElement x, string key, int maximum, bool optional = false)
    {
        if (!x.TryGetProperty(key, out JsonElement a) || a.ValueKind == JsonValueKind.Null)
            return optional ? [] : throw new InvalidDataException();
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() > maximum) throw new InvalidDataException();
        return a.EnumerateArray().ToArray();
    }

    private static string Required(JsonElement x, string key)
    {
        return Text(x, key) is { Length: > 0 } s ? s : throw new InvalidDataException();
    }

    private static string? Text(JsonElement x, string key)
    {
        if (!x.TryGetProperty(key, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        string? s = v.GetString();
        return s?.Length <= 512 ? s : throw new InvalidDataException();
    }

    private static bool? Bool(JsonElement x, string key)
    {
        return !x.TryGetProperty(key, out JsonElement v) || v.ValueKind == JsonValueKind.Null ? null : v.GetBoolean();
    }

    private static DateTimeOffset? Time(JsonElement x, string key)
    {
        return Text(x, key) is { } s
            ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime()
            : null;
    }

    private static decimal? Number(JsonElement x, string key)
    {
        if (!x.TryGetProperty(key, out JsonElement v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal value)) return value;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(
                v.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)) return value;
        throw new InvalidDataException();
    }
}