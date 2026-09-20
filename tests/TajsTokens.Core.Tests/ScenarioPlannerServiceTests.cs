// Taj's Tokens | ScenarioPlannerServiceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class ScenarioPlannerServiceTests
{
    private static readonly DateTimeOffset s_evaluationTime = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);


    [Theory]
    [InlineData(4, false)]
    [InlineData(8, true)]
    public void UncertaintyRequiresGenuineResetGenerationsNotJitterVariants(int generations, bool expectedBand)
    {
        ScenarioHistorySample[] history = Enumerable.Range(0, generations).SelectMany(g => Enumerable.Range(0, 12).Select(i =>
        {
            DateTimeOffset start = s_evaluationTime.AddDays(-generations + g).AddHours(i);
            return Sample(QuotaWindowKind.FiveHour, start, 2 + i % 3, 1, 0) with
            {
                ResetUtc = s_evaluationTime.AddDays(-generations + g).AddHours(18).AddSeconds(i % 2),
            };
        })).ToArray();
        var request = new ScenarioRequest(1, 1, 0);
        ScenarioWindowEstimate estimate = QuotaPredictionService.Simulate(request, history, s_evaluationTime).FiveHour;
        Assert.True(estimate.HasEnoughHistory);
        Assert.Equal(expectedBand, estimate.LowerQuotaDeltaPercent.HasValue);
        ScenarioHistorySample[] steady = history.Select(x => x with { ResetUtc = x.ResetUtc!.Value.AddSeconds(-x.ResetUtc.Value.Second) })
            .ToArray();
        Assert.Equal(QuotaPredictionService.Simulate(request, steady, s_evaluationTime).FiveHour, estimate);
    }

    [Fact]
    public void ScenarioDoesNotBorrowSamplesFromAnOlderPlanCohort()
    {
        var cohort = new QuotaHistoryCohort(
            "codex",
            "default",
            QuotaWindowKind.FiveHour,
            "app-server",
            "account",
            "codex",
            "old-plan",
            null,
            300);
        ScenarioHistorySample[] history = BuildSyntheticHistory().Where(x => x.Kind == QuotaWindowKind.FiveHour)
            .Select((x, i) => x with
            {
                AccountKey = "account", Source = "app-server", Cohort = i < 10 ? cohort : cohort with { PlanType = "new-plan" },
            }).ToArray();
        ScenarioWindowEstimate result = QuotaPredictionService.Simulate(
            new ScenarioRequest(1, 1, 1, AccountKey: "account"),
            history,
            s_evaluationTime).FiveHour;
        Assert.False(result.HasEnoughHistory);
        Assert.Equal(8, result.SampleCount);
    }

    [Fact]
    public void Estimate_WithSparseHistory_ReturnsHonestInsufficientState()
    {
        ScenarioHistorySample[] history = Enumerable.Range(0, 3)
            .Select(index => Sample(QuotaWindowKind.FiveHour, s_evaluationTime.AddHours(-index - 2), 4 + index, 1, 0))
            .ToArray();

        ScenarioEstimate estimate = QuotaPredictionService.Simulate(new ScenarioRequest(2, 1, 0), history, s_evaluationTime);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.False(estimate.Weekly.HasEnoughHistory);
        Assert.Null(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("Not enough", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_WithDualWindowHistory_ReturnsPredictionWithoutInventedCalibration()
    {
        ScenarioHistorySample[] history = BuildSyntheticHistory();

        ScenarioEstimate estimate = QuotaPredictionService.Simulate(
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
        ScenarioHistorySample[] history = BuildSyntheticHistory();

        ScenarioEstimate light = QuotaPredictionService.Simulate(new ScenarioRequest(1, 1, 0, 0.7), history, s_evaluationTime);
        ScenarioEstimate heavy = QuotaPredictionService.Simulate(new ScenarioRequest(3, 2, 4, 1.4), history, s_evaluationTime);

        Assert.False(light.FiveHour.HasEnoughHistory);
        Assert.False(heavy.FiveHour.HasEnoughHistory);
        Assert.Contains("Intensity", light.FiveHour.Explanation);
        ScenarioEstimate outside = QuotaPredictionService.Simulate(new ScenarioRequest(3, 2, 4), history, s_evaluationTime);
        Assert.Contains("outside observed support", outside.FiveHour.Explanation);
    }

    [Fact]
    public void Estimate_MissingRequestedCohort_DoesNotBorrowBroaderHistory()
    {
        ScenarioHistorySample[] history = BuildSyntheticHistory();

        ScenarioEstimate baseline = QuotaPredictionService.Simulate(new ScenarioRequest(2, 1, 1), history, s_evaluationTime);
        ScenarioEstimate unknownModel = QuotaPredictionService.Simulate(
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
        ScenarioHistorySample[] history = BuildSyntheticHistory();

        ScenarioEstimate estimate = QuotaPredictionService.Simulate(
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
        ScenarioHistorySample[] history = BuildSyntheticHistory();
        DateTimeOffset evaluation = s_evaluationTime.AddDays(40);

        ScenarioEstimate estimate = QuotaPredictionService.Simulate(new ScenarioRequest(2, 1, 1), history, evaluation);

        Assert.False(estimate.FiveHour.HasEnoughHistory);
        Assert.False(estimate.Weekly.HasEnoughHistory);
        Assert.Null(estimate.FiveHour.ExpectedQuotaDeltaPercent);
        Assert.Contains("stale", estimate.FiveHour.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Estimate_FutureOutcomesCannotChangeEstimate_AndFlatIntervalsAreRetained()
    {
        ScenarioHistorySample[] history = BuildSyntheticHistory().Select(x => x with { QuotaDeltaPercent = 0 }).ToArray();
        var request = new ScenarioRequest(1, 1, 1);
        ScenarioEstimate before = QuotaPredictionService.Simulate(request, history, s_evaluationTime);
        IEnumerable<ScenarioHistorySample> future = history.Select(x =>
            x with { StartUtc = x.StartUtc.AddDays(10), EndUtc = x.EndUtc.AddDays(10), QuotaDeltaPercent = 99 });
        ScenarioEstimate after = QuotaPredictionService.Simulate(request, history.Concat(future).ToArray(), s_evaluationTime);
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
        for (int index = 0; index < 18; index++)
        {
            double hours = 0.5 + index % 4 * 0.5;
            int roots = 1 + index % 2;
            int children = index % 5;
            double fiveHourDelta = 1.5 + 1.8 * hours + 2.2 * roots * hours + 1.3 * children * hours + (index % 3 - 1) * 0.2;
            double weeklyDelta = 0.4 + 0.55 * hours + 0.6 * roots * hours + 0.35 * children * hours + (index % 3 - 1) * 0.08;
            DateTimeOffset observed = start.AddHours(index * 4);

            samples.Add(
                new ScenarioHistorySample(
                    QuotaWindowKind.FiveHour,
                    observed,
                    observed.AddHours(hours),
                    fiveHourDelta,
                    roots,
                    children,
                    "gpt-5.6-luna",
                    "xhigh"));
            samples.Add(
                new ScenarioHistorySample(
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
        int children)
    {
        return new ScenarioHistorySample(kind, start, start.AddHours(1), delta, roots, children, "model", "high");
    }
}