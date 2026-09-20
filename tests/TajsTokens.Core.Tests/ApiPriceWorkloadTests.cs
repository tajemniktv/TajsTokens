// Taj's Tokens | ApiPriceWorkloadTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class ApiPriceWorkloadTests
{
    [Fact]
    public void WeightsDisjointCategoriesOnceAndKeepsUnknownModelsUnpriced()
    {
        DateTimeOffset time = DateTimeOffset.UtcNow;
        var row = new CodexPredictiveTokenEvent(
            "s",
            time,
            time,
            "gpt-5.6-sol",
            "high",
            1_000_000,
            2_000_000,
            3_000_000,
            4_000_000,
            5_000_000,
            15_000_000);
        ApiPriceWeight priced = ApiPriceWorkload.Calculate([row]);
        Assert.Equal(199.8m, priced.WeightedAmount);
        Assert.True(priced.IsComplete);
        ApiPriceWeight mixed = ApiPriceWorkload.Calculate([row, row with { Model = "gpt-5.6-sol-unknown-suffix" }]);
        Assert.Equal(priced.WeightedAmount, mixed.WeightedAmount);
        Assert.False(mixed.IsComplete);
        Assert.Equal(15_000_000m, mixed.UnpricedReportedTokens);
        Assert.False(ApiPriceWorkload.Calculate([row with { Model = "gpt-5.5" }]).IsComplete); // No verified cache-write rate.
        Assert.False(ApiPriceWorkload.Calculate([row with { UncachedInputTokens = -1 }]).IsComplete);
    }

    [Fact]
    public void FrozenApiBaselineReportsCoverageAndUsesMatchedHeldOutComparisons()
    {
        QuotaCostObservation[] rows = QuotaCostEvaluationTests.Observations(60).Select(x => x with
        {
            ApiPriceWeight = new ApiPriceWeight(
                ApiPriceWorkload.Version,
                x.Features.Tokens / 1_000_000m * 4,
                x.Features.Tokens,
                0,
                1,
                0),
        }).ToArray();
        QuotaCostReport before = QuotaCostEvaluation.Evaluate(rows, "fixture");
        QuotaCostScore baseline = before.Scores.Single(x => x.Candidate == "api-price");
        Assert.Equal(40, baseline.HeldOutSamples);
        Assert.False(baseline.MaterialWin);
        rows[30] = rows[30] with { ApiPriceWeight = new ApiPriceWeight(ApiPriceWorkload.Version, 0, 0, rows[30].Features.Tokens, 0, 1) };
        QuotaCostReport report = QuotaCostEvaluation.Evaluate(rows, "fixture");
        QuotaCostScore score = report.Scores.Single(x => x.Candidate == "api-price");
        Assert.Equal(baseline.Coefficients, score.Coefficients);
        Assert.Equal(39, score.HeldOutSamples);
        Assert.Equal(1, score.UnpricedHeldOutIntervals);
        Assert.DoesNotContain(score.Trials, x => x.StartUtc == rows[30].StartUtc);
        Assert.Equal(
            report.Scores.Single(x => x.Candidate == "total").Trials.Where(x => x.StartUtc != rows[30].StartUtc)
                .Average(x => x.IntervalLoss),
            score.PairedTotalIntervalLoss);
        rows[0] = rows[0] with { ApiPriceWeight = rows[30].ApiPriceWeight };
        QuotaCostScore unavailable = QuotaCostEvaluation.Evaluate(rows, "fixture").Scores.Single(x => x.Candidate == "api-price");
        Assert.Empty(unavailable.Trials);
        Assert.Equal("unpriced-training-evidence", unavailable.Status);
    }
}