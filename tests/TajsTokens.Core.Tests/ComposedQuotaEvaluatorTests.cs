using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ComposedQuotaEvaluatorTests
{
    internal static CodexForecastDataset TimelyData()
    {
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        return new(Enumerable.Range(0, 65).Select(i => new QuotaSnapshot(QuotaWindowKind.Weekly,
            start.AddMinutes(i * 15), i, 10080, start.AddDays(7), "codex", "default", "codex-app-server:codex", "account")
            { HasSourceTimestamp = true, CollectedAtUtc = start.AddMinutes(i * 15), PlanType = "pro", LimitId = "codex" }).ToArray(),
            [], Enumerable.Range(0, 65).Select(i => new CodexPredictiveTokenEvent("s", start.AddMinutes(i * 15),
                start.AddMinutes(i * 15), "m", "high", 10000, 0, 0, 0, 0, 10000)).ToArray(), [], start.AddDays(1), "fixture");
    }

    [Fact]
    public void UnusedLateMetadataDoesNotInvalidateTokenCostTraining()
    {
        var data = TimelyData();
        var late = new CodexWorkloadObservation("late-tier", "source", "file", 0, 1, "s",
            "thread_settings_applied", data.Quota[0].CapturedAtUtc, data.CapturedAtUtc,
            null, null, null, null, null, null, ServiceTier: "priority");
        var amended = data with { Workload = [late] };
        var costRows = QuotaCostObservationBuilder.Build(amended);
        Assert.All(costRows, row =>
        {
            Assert.Equal(data.CapturedAtUtc, row.EvidenceAvailableAtUtc);
            Assert.Equal(row.EndUtc, row.TokenCostEvidenceAvailableAtUtc);
        });
        var before = ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin);
        var after = ComposedQuotaEvaluator.Evaluate(amended, availability: ForecastReplayAvailability.CollectedByOrigin);
        foreach (var score in before.Scores)
        {
            var updated = after.Scores.Single(x => x.Cohort == score.Cohort && x.HorizonHours == score.HorizonHours && x.CostModel == score.CostModel);
            Assert.Equal(score.Trials.Select(x => (x.OriginUtc, x.PredictedDelta)), updated.Trials.Select(x => (x.OriginUtc, x.PredictedDelta)));
            Assert.Equal(score.WithheldReasons, updated.WithheldReasons);
        }
        Assert.Contains(after.Scores, x => x.HeldOutIntervals > 0);
    }

    [Fact]
    public void StrictEvaluationRequiresTrainingAvailableAtOriginAndTimelyMeters()
    {
        var data = TimelyData();
        var strict = ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin);
        var trials = strict.Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total").Trials;
        Assert.NotEmpty(trials);
        Assert.All(trials, x =>
        {
            Assert.Equal(ForecastReplayAvailability.CollectedByOrigin, x.Availability);
            Assert.Equal(ForecastReplayAvailability.CollectedByOrigin, x.Workload.Availability);
            Assert.Equal(x.OutcomeUtc, x.CalibrationAvailableAtUtc);
            Assert.Null(x.LowerRemainingPercent); // One reset cannot establish joint uncertainty.
            Assert.Null(x.UpperRemainingPercent);
        });
        var delayedTraining = data with { Tokens = data.Tokens.Select((x, i) => i < 40 ? x with { CapturedAtUtc = data.CapturedAtUtc } : x).ToArray() };
        var unknownTraining = data with { Tokens = data.Tokens.Select((x, i) => i == 2 ? x with { CapturedAtUtc = null } : x).ToArray() };
        var delayedMeters = data with { Quota = data.Quota.Select(x => x with { CollectedAtUtc = data.CapturedAtUtc }).ToArray() };
        foreach (var (unavailable, reason) in new[] { (delayedTraining, "training-collected-after-origin"),
                     (unknownTraining, "training-collection-unknown"), (delayedMeters, "origin-meter-collected-after-origin") })
        {
            Assert.NotEmpty(ComposedQuotaEvaluator.Evaluate(unavailable).Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total").Trials);
            var score = ComposedQuotaEvaluator.Evaluate(unavailable, availability: ForecastReplayAvailability.CollectedByOrigin)
                .Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total");
            Assert.Empty(score.Trials);
            Assert.Equal(score.MissingComposition, score.WithheldReasons[reason]);
        }
    }

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
