using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ComposedQuotaBreakdownsTests
{
    [Fact]
    public void PartitionsConserveOutcomesAndComparisonsUseMatchedSubsets()
    {
        var at = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        ComposedQuotaTrial Trial(int i, double shift, Dictionary<string, double> mix) => new(at.AddHours(i),
            at.AddHours(i + .5), at.AddDays(7).AddSeconds(shift), 1 + i * 2, 1, 2,
            10 + i * 10, 2, 3, 50, new(at.AddHours(i), .5, 10, [10, 0, 0, 0, 0], mix,
                new Dictionary<string, double>(), 1, at, ForecastReplayAvailability.CollectedByOrigin, "fixture"))
        { OriginActivity = "RecentActivity", RecordedOutcomeTokens = i == 0 ? 0 : 10 };
        var trials = new[] {
            Trial(0, 0, new() { ["m"] = .7 }) with { ZeroUseIntervalLoss = .5, ObservedRemainingPercent = 15, LowerRemainingPercent = 10, UpperRemainingPercent = 20 },
            Trial(1, .8, new() { ["m"] = .8 }) with { CompleteOutcomeTokenCategories = false, OutcomeQualityFlags = ["missing-runtime", "incomplete-token-categories", "missing-runtime"] },
            Trial(2, 1.6, new() { ["m"] = .5, ["n"] = .5 }) with { IncumbentIntervalLoss = 2, ObservedRemainingPercent = 25, LowerRemainingPercent = 0, UpperRemainingPercent = 10 }
        };
        var report = ComposedQuotaBreakdowns.Build(trials);
        Assert.All(report.GroupBy(x => x.Dimension), partition => Assert.Equal(3, partition.Sum(x => x.Outcomes)));
        Assert.Equal(2, report.Count(x => x.Dimension == "reset")); // No transitive timestamp chaining.
        var activity = Assert.Single(report, x => x.Dimension == "origin-activity");
        Assert.Equal(20, activity.ForecastIntervalLoss);
        Assert.Equal(1, activity.SignedBias);
        Assert.Equal(1, activity.IncumbentPairs);
        Assert.Equal(30, activity.PairedForecastIntervalLoss);
        Assert.Equal(2, activity.IncumbentIntervalLoss);
        Assert.Equal(1, activity.ZeroUsePairs);
        Assert.Equal(10, activity.ZeroPairedForecastIntervalLoss);
        Assert.Equal(.5, activity.ZeroUseIntervalLoss);
        Assert.Equal(2, activity.BandOutcomes);
        Assert.Equal(.5, activity.BandCoverage);
        Assert.Equal(10, activity.MeanBandWidth);
        Assert.Contains(report, x => x.Dimension == "origin-model-mix" && x.Group == "unknown-or-partial");
        Assert.Contains(report, x => x.Dimension == "origin-model-mix" && x.Group == "dominant: m");
        Assert.Contains(report, x => x.Dimension == "origin-model-mix" && x.Group == "mixed");
        Assert.Contains(report, x => x.Dimension == "outcome-category-coverage" && x.Group == "incomplete-reported-vectors" && x.Outcomes == 1);
        Assert.Contains(report, x => x.Dimension == "outcome-category-coverage" && x.Group == "unknown" && x.Outcomes == 2);
        Assert.Contains(report, x => x.Dimension == "outcome-quality" && x.Group == "incomplete-token-categories; missing-runtime" && x.Outcomes == 1);
        Assert.Contains(report, x => x.Dimension == "outcome-quality" && x.Group == "no-recorded-flags-not-proven-complete" && x.Outcomes == 2);
        Assert.Empty(ComposedQuotaBreakdowns.Build([]));
    }

    [Fact]
    public void StrictTrialBreakdownsDoNotBorrowLateActivity()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        data = data with { Tokens = data.Tokens.Select(x => x with {
            ObservedAtUtc = x.ObservedAtUtc.AddMinutes(-12), CapturedAtUtc = x.CapturedAtUtc?.AddMinutes(-12) }).ToArray() };
        var before = ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin);
        Assert.Contains(before.Scores.SelectMany(x => x.Trials), x => x.OriginActivity == "NoRecentActivity");
        var origin = before.Scores.SelectMany(x => x.Trials).Min(x => x.OriginUtc);
        var late = new CodexWorkloadObservation("late", "source", "file", 0, 1, "session", "task_started",
            origin.AddMinutes(-1), data.CapturedAtUtc, "turn", null, null, null, null, null);
        var after = ComposedQuotaEvaluator.Evaluate(data with { Workload = [late] }, availability: ForecastReplayAvailability.CollectedByOrigin);
        Assert.Contains(after.Scores, x => x.Breakdowns.Count > 0);
        foreach (var score in before.Scores)
        {
            var updated = after.Scores.Single(x => x.Cohort == score.Cohort && x.HorizonHours == score.HorizonHours && x.CostModel == score.CostModel);
            Assert.Equal(score.Breakdowns, updated.Breakdowns);
            Assert.All(updated.Trials, x => Assert.NotNull(x.ZeroUseIntervalLoss));
            Assert.All(updated.Trials, x => {
                Assert.NotNull(x.CompleteOutcomeTokenCategories);
                Assert.Contains("local-co-observation-not-account-attribution", x.OutcomeQualityFlags);
            });
        }
    }
}
