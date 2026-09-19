using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ApiPriceWorkloadTests
{
    [Fact]
    public void WeightsDisjointCategoriesOnceAndKeepsUnknownModelsUnpriced()
    {
        var time = DateTimeOffset.UtcNow;
        var row = new CodexPredictiveTokenEvent("s", time, time, "gpt-5.6-sol", "high",
            1_000_000, 2_000_000, 3_000_000, 4_000_000, 5_000_000, 15_000_000);
        var priced = ApiPriceWorkload.Calculate([row]);
        Assert.Equal(199.8m, priced.WeightedAmount);
        Assert.True(priced.IsComplete);
        var mixed = ApiPriceWorkload.Calculate([row, row with { Model = "gpt-5.6-sol-unknown-suffix" }]);
        Assert.Equal(priced.WeightedAmount, mixed.WeightedAmount);
        Assert.False(mixed.IsComplete);
        Assert.Equal(15_000_000m, mixed.UnpricedReportedTokens);
        Assert.False(ApiPriceWorkload.Calculate([row with { Model = "gpt-5.5" }]).IsComplete); // No verified cache-write rate.
        Assert.False(ApiPriceWorkload.Calculate([row with { UncachedInputTokens = -1 }]).IsComplete);
    }

    [Fact]
    public void FrozenApiBaselineReportsCoverageAndUsesMatchedHeldOutComparisons()
    {
        var rows = QuotaCostEvaluationTests.Observations(60).Select(x => x with
        {
            ApiPriceWeight = new(ApiPriceWorkload.Version, (decimal)x.Features.Tokens / 1_000_000m * 4, x.Features.Tokens, 0, 1, 0)
        }).ToArray();
        var before = QuotaCostEvaluation.Evaluate(rows, "fixture");
        var baseline = before.Scores.Single(x => x.Candidate == "api-price");
        Assert.Equal(40, baseline.HeldOutSamples);
        Assert.False(baseline.MaterialWin);
        rows[30] = rows[30] with { ApiPriceWeight = new(ApiPriceWorkload.Version, 0, 0, rows[30].Features.Tokens, 0, 1) };
        var report = QuotaCostEvaluation.Evaluate(rows, "fixture");
        var score = report.Scores.Single(x => x.Candidate == "api-price");
        Assert.Equal(baseline.Coefficients, score.Coefficients);
        Assert.Equal(39, score.HeldOutSamples);
        Assert.Equal(1, score.UnpricedHeldOutIntervals);
        Assert.DoesNotContain(score.Trials, x => x.StartUtc == rows[30].StartUtc);
        Assert.Equal(report.Scores.Single(x => x.Candidate == "total").Trials.Where(x => x.StartUtc != rows[30].StartUtc).Average(x => x.IntervalLoss), score.PairedTotalIntervalLoss);
        rows[0] = rows[0] with { ApiPriceWeight = rows[30].ApiPriceWeight };
        var unavailable = QuotaCostEvaluation.Evaluate(rows, "fixture").Scores.Single(x => x.Candidate == "api-price");
        Assert.Empty(unavailable.Trials);
        Assert.Equal("unpriced-training-evidence", unavailable.Status);
    }
}
