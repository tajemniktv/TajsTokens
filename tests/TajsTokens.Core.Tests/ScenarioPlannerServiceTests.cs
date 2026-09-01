using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ScenarioPlannerServiceTests
{
    private static readonly DateTimeOffset EvaluationTime = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
    private readonly ScenarioPlannerService _planner = new();

    [Fact]
    public void Estimate_WithSparseHistory_ReturnsHonestInsufficientState()
    {
        var history = Enumerable.Range(0, 3)
            .Select(index => Sample(QuotaWindowKind.FiveHour, EvaluationTime.AddHours(-index - 2), 4 + index, 1, 0))
            .ToArray();

        var estimate = _planner.Estimate(new ScenarioRequest(2, 1, 0), history, EvaluationTime);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.False(estimate.Weekly.HasEnoughHistory);
        Assert.Null(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("Not enough", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_WithDualWindowHistory_ReturnsRangesAndConfidence()
    {
        var history = BuildSyntheticHistory();

        var estimate = _planner.Estimate(
            new ScenarioRequest(2, 1, 2, 1.0, "gpt-5.6-luna", "xhigh"),
            history,
            EvaluationTime);

        Assert.True(estimate.FiveHour.HasEnoughHistory);
        Assert.True(estimate.Weekly.HasEnoughHistory);
        Assert.NotNull(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.NotNull(estimate.Weekly.ExpectedQuotaDeltaPercent);
        Assert.True(estimate.FiveHour.LowerQuotaDeltaPercent <= estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.True(estimate.FiveHour.UpperQuotaDeltaPercent >= estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.True(estimate.FiveHour.Confidence > 0.5);
        Assert.Contains("No universal token", estimate.Methodology, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_HigherConcurrencyAndIntensity_ProducesHigherPredictionForSyntheticHistory()
    {
        var history = BuildSyntheticHistory();

        var light = _planner.Estimate(new ScenarioRequest(1, 1, 0, 0.7), history, EvaluationTime);
        var heavy = _planner.Estimate(new ScenarioRequest(3, 2, 4, 1.4), history, EvaluationTime);

        Assert.True(light.FiveHour.HasEnoughHistory);
        Assert.True(heavy.FiveHour.HasEnoughHistory);
        Assert.True(heavy.FiveHour.ExpectedQuotaDeltaPercent > light.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.True(heavy.Weekly.ExpectedQuotaDeltaPercent > light.Weekly.ExpectedQuotaDeltaPercent);
    }

    [Fact]
    public void Estimate_MissingRequestedCohort_UsesBroaderHistoryWithConfidencePenalty()
    {
        var history = BuildSyntheticHistory();

        var baseline = _planner.Estimate(new ScenarioRequest(2, 1, 1), history, EvaluationTime);
        var unknownModel = _planner.Estimate(
            new ScenarioRequest(2, 1, 1, 1, "never-seen-model", "ultra-never-seen"),
            history,
            EvaluationTime);

        Assert.True(unknownModel.FiveHour.HasEnoughHistory);
        Assert.True(unknownModel.FiveHour.Confidence < baseline.FiveHour.Confidence);
        Assert.Contains("broader account cohort", unknownModel.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_ReasoningFallbackAfterModelMatch_ReportsRetainedModelCohort()
    {
        var history = BuildSyntheticHistory();

        var estimate = _planner.Estimate(
            new ScenarioRequest(2, 1, 1, 1, "gpt-5.6-luna", "never-seen-reasoning"),
            history,
            EvaluationTime);

        Assert.True(estimate.FiveHour.HasEnoughHistory);
        Assert.Contains("model gpt-5.6-luna cohort was retained", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broader account cohort was used", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_WhenNewestHistoryIsStale_ReturnsUnavailableInsteadOfConfidentPrediction()
    {
        var history = BuildSyntheticHistory();
        var evaluation = EvaluationTime.AddDays(40);

        var estimate = _planner.Estimate(new ScenarioRequest(2, 1, 1), history, evaluation);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.False(estimate.Weekly.HasEnoughHistory);
        Assert.Null(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("stale", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    private static ScenarioHistorySample[] BuildSyntheticHistory()
    {
        var start = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        var samples = new List<ScenarioHistorySample>();
        for (var index = 0; index < 18; index++)
        {
            var hours = 0.5 + (index % 4) * 0.5;
            var roots = 1 + (index % 2);
            var children = index % 5;
            var fiveHourDelta = 1.5 + (1.8 * hours) + (2.2 * roots * hours) + (1.3 * children * hours) + ((index % 3) - 1) * 0.2;
            var weeklyDelta = 0.4 + (0.55 * hours) + (0.6 * roots * hours) + (0.35 * children * hours) + ((index % 3) - 1) * 0.08;
            var observed = start.AddHours(index * 4);

            samples.Add(new ScenarioHistorySample(
                QuotaWindowKind.FiveHour,
                observed,
                observed.AddHours(hours),
                fiveHourDelta,
                roots,
                children,
                "gpt-5.6-luna",
                "xhigh"));
            samples.Add(new ScenarioHistorySample(
                QuotaWindowKind.Weekly,
                observed,
                observed.AddHours(hours),
                weeklyDelta,
                roots,
                children,
                "gpt-5.6-luna",
                "xhigh"));
        }

        return samples.ToArray();
    }

    private static ScenarioHistorySample Sample(
        QuotaWindowKind kind,
        DateTimeOffset start,
        double delta,
        int roots,
        int children) =>
        new(kind, start, start.AddHours(1), delta, roots, children, "model", "high");
}
