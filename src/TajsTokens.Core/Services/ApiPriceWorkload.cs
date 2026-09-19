using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Fixed counterfactual workload weighting, never a bill, credit amount or inferred service tier.</summary>
public static class ApiPriceWorkload
{
    public const string Version = "openai-api-standard-short-2026-09-19/v1";
    public const string Source = "https://developers.openai.com/api/docs/pricing";
    public const string Assumptions = "Retrospective fixed standard/short-context API-price weights, not actual charges or provider credits. " +
        "No inference of execution tier, request context length, regional uplift, tools or historical prices. " +
        "Exact supported model names only; unknown models and unsupported cache-write rates stay unpriced.";
    private sealed record Rate(decimal Input, decimal Cached, decimal? CacheWrite, decimal Output);
    // Official rate-card/model pages inspected 2026-09-19; provenance and caveats in the repo review.
    private static readonly IReadOnlyDictionary<string, Rate> Rates = new Dictionary<string, Rate>(StringComparer.Ordinal)
    {
        ["gpt-6-astra"] = new(10, 1, 12.5m, 50),
        ["gpt-5.6-sol"] = new(4, .4m, 5, 20),
        ["gpt-5.6-terra"] = new(2, .2m, 2.5m, 12),
        ["gpt-5.6-luna"] = new(.2m, .02m, .25m, 1.2m),
        ["gpt-5.5"] = new(5, .5m, null, 30),
        ["gpt-5.4"] = new(2.5m, .25m, null, 15),
        ["gpt-5.3-codex"] = new(1.75m, .175m, null, 14),
        ["gpt-5.2-codex"] = new(1.75m, .175m, null, 14)
    };

    public static ApiPriceWeight Calculate(IEnumerable<CodexPredictiveTokenEvent> events)
    {
        decimal amount = 0, priced = 0, unpriced = 0;
        var pricedEvents = 0;
        var unpricedEvents = 0;
        foreach (var row in events)
        {
            var counters = new[] { row.UncachedInputTokens, row.CacheReadTokens, row.CacheWriteTokens,
                row.NonReasoningOutputTokens, row.ReasoningOutputTokens, row.ReportedTotalTokens };
            if (counters.All(x => x == 0)) { pricedEvents++; continue; }
            if (counters.Any(x => x < 0) || row.Model is null || !Rates.TryGetValue(row.Model, out var rate) ||
                row.CacheWriteTokens > 0 && rate.CacheWrite is null)
            {
                unpriced += Math.Max(0, row.ReportedTotalTokens);
                unpricedEvents++;
                continue;
            }
            // Canonical categories are disjoint: reasoning output is added exactly once.
            amount += (row.UncachedInputTokens * rate.Input + row.CacheReadTokens * rate.Cached +
                row.CacheWriteTokens * (rate.CacheWrite ?? 0) +
                ((decimal)row.NonReasoningOutputTokens + row.ReasoningOutputTokens) * rate.Output) / 1_000_000m;
            priced += row.ReportedTotalTokens;
            pricedEvents++;
        }
        return new(Version, amount, priced, unpriced, pricedEvents, unpricedEvents);
    }
}
