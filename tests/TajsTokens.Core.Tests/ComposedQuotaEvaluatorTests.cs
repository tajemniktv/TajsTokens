using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ComposedQuotaEvaluatorTests
{
    [Fact]
    public void ForecastAndCostOnlyErrorsAreSeparateAndFutureWorkCannotChangePrediction()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var quota = Enumerable.Range(0, 55).Select(i => new QuotaSnapshot(QuotaWindowKind.Weekly,
            start.AddMinutes(i * 15), i, 10080, start.AddDays(7), "codex", "default", "codex-app-server:codex", "account")
            { HasSourceTimestamp = true, CollectedAtUtc = start.AddMinutes(i * 15) }).ToArray();
        var tokens = Enumerable.Range(0, 55).Select(i => new CodexPredictiveTokenEvent("s", start.AddMinutes(i * 15),
            start.AddMinutes(i * 15), "m", "high", 10000, 0, 0, 0, 0, 10000)).ToArray();
        var data = new CodexForecastDataset(quota, [], tokens, [], start.AddDays(1), "fixture");
        var before = ComposedQuotaEvaluator.Evaluate(data).Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total");
        Assert.NotEmpty(before.Trials);
        var origin = before.Trials[0].OriginUtc;
        var extra = tokens[0] with { ObservedAtUtc = origin.AddMinutes(1), UncachedInputTokens = 1_000_000, ReportedTotalTokens = 1_000_000, Model = "future" };
        var after = ComposedQuotaEvaluator.Evaluate(data with { Tokens = tokens.Append(extra).ToArray() })
            .Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total");
        Assert.Equal(before.Trials[0].PredictedDelta, after.Trials[0].PredictedDelta);
        Assert.NotEqual(before.Trials[0].ActualWorkloadCost, after.Trials[0].ActualWorkloadCost);
        Assert.DoesNotContain("future", after.Trials[0].Workload.ModelShares.Keys);
        Assert.All(after.Trials, x => Assert.InRange(x.PredictedRemaining, 0, 100));
    }
}
