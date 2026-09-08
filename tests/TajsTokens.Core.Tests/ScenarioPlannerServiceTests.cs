using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ScenarioPlannerServiceTests
{
    private static readonly DateTimeOffset s_evaluationTime = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
    private readonly ScenarioPlannerService _planner = new();

    [Fact]
    public void Estimate_WithSparseHistory_ReturnsHonestInsufficientState()
    {
        var history = Enumerable.Range(0, 3)
            .Select(index => Sample(QuotaWindowKind.FiveHour, s_evaluationTime.AddHours(-index - 2), 4 + index, 1, 0))
            .ToArray();

        var estimate = _planner.Estimate(new ScenarioRequest(2, 1, 0), history, s_evaluationTime);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.False(estimate.Weekly.HasEnoughHistory);
        Assert.Null(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("Not enough", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_WithDualWindowHistory_ReturnsPredictionWithoutInventedCalibration()
    {
        var history = BuildSyntheticHistory();

        var estimate = _planner.Estimate(
            new ScenarioRequest(2, 1, 2, 1.0, "gpt-5.6-luna", "xhigh"),
            history,
            s_evaluationTime);

        Assert.True(estimate.FiveHour.HasEnoughHistory);
        Assert.True(estimate.Weekly.HasEnoughHistory);
        Assert.NotNull(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.NotNull(estimate.Weekly.ExpectedQuotaDeltaPercent);
        Assert.Null(estimate.FiveHour.LowerQuotaDeltaPercent);
        Assert.Null(estimate.FiveHour.UpperQuotaDeltaPercent);
        Assert.Equal(0, estimate.FiveHour.Confidence);
        Assert.Contains("No universal token", estimate.Methodology, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_UnsupportedIntensityAndExtrapolation_AreUnavailable()
    {
        var history = BuildSyntheticHistory();

        var light = _planner.Estimate(new ScenarioRequest(1, 1, 0, 0.7), history, s_evaluationTime);
        var heavy = _planner.Estimate(new ScenarioRequest(3, 2, 4, 1.4), history, s_evaluationTime);

        Assert.False(light.FiveHour.HasEnoughHistory);
        Assert.False(heavy.FiveHour.HasEnoughHistory);
        Assert.Contains("Intensity", light.FiveHour.Explanation);
        var outside = _planner.Estimate(new ScenarioRequest(3, 2, 4), history, s_evaluationTime);
        Assert.Contains("outside observed support", outside.FiveHour.Explanation);
    }

    [Fact]
    public void Estimate_MissingRequestedCohort_DoesNotBorrowBroaderHistory()
    {
        var history = BuildSyntheticHistory();

        var baseline = _planner.Estimate(new ScenarioRequest(2, 1, 1), history, s_evaluationTime);
        var unknownModel = _planner.Estimate(
            new ScenarioRequest(2, 1, 1, 1, "never-seen-model", "ultra-never-seen"),
            history,
            s_evaluationTime);

        Assert.True(baseline.FiveHour.HasEnoughHistory);
        Assert.False(unknownModel.FiveHour.HasEnoughHistory);
        Assert.Null(unknownModel.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("matching history", unknownModel.FiveHour.Explanation);
    }

    [Fact]
    public void Estimate_UnknownReasoningAfterModelMatch_IsUnavailable()
    {
        var history = BuildSyntheticHistory();

        var estimate = _planner.Estimate(
            new ScenarioRequest(2, 1, 1, 1, "gpt-5.6-luna", "never-seen-reasoning"),
            history,
            s_evaluationTime);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.Contains("matching history", estimate.FiveHour.Explanation);
        Assert.DoesNotContain("broader account cohort was used", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_WhenNewestHistoryIsStale_ReturnsUnavailableInsteadOfConfidentPrediction()
    {
        var history = BuildSyntheticHistory();
        var evaluation = s_evaluationTime.AddDays(40);

        var estimate = _planner.Estimate(new ScenarioRequest(2, 1, 1), history, evaluation);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.False(estimate.Weekly.HasEnoughHistory);
        Assert.Null(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("stale", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_FutureOutcomesCannotChangeEstimate_AndFlatIntervalsAreRetained()
    {
        var history = BuildSyntheticHistory().Select(x => x with { QuotaDeltaPercent = 0 }).ToArray();
        var request = new ScenarioRequest(1, 1, 1);
        var before = _planner.Estimate(request, history, s_evaluationTime);
        var future = history.Select(x => x with { StartUtc = x.StartUtc.AddDays(10), EndUtc = x.EndUtc.AddDays(10), QuotaDeltaPercent = 99 });
        var after = _planner.Estimate(request, history.Concat(future).ToArray(), s_evaluationTime);
        Assert.Equal(before, after);
        Assert.Equal(18, before.FiveHour.SampleCount);
        Assert.Equal(0, before.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Null(before.FiveHour.UpperQuotaDeltaPercent);
        Assert.Contains("precision-limited", before.FiveHour.Explanation);
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
