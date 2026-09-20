using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaForecastCalibrationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void OneSecondResetJitterRetainsLearningAndWalkForwardTargets()
    {
        var steady = Enumerable.Range(0, 49).Select(i => Point(i / 12d, 10 + i)).ToArray();
        var jitter = steady.Select((row, i) => row with { ResetsAtUtc = row.ResetsAtUtc!.Value.AddSeconds(i % 2) }).ToArray();
        Assert.Single(QuotaForecastCalibration.SplitEpochs(jitter));
        var forecast = QuotaPredictionService.BuildResetOutlook(jitter, jitter[^1].CapturedAtUtc);
        Assert.NotEqual(ForecastState.Learning, forecast.State);
        Assert.Equal(49, forecast.Evidence!.ObservationCount);
        Assert.Equal(4, forecast.Evidence.ObservedHours);
        Assert.Equal(steady[^1].ResetsAtUtc, jitter[^1].ResetsAtUtc);
        foreach (var model in new[] { "persistence", "epoch", "legacy-ewma" })
        {
            var expected = QuotaForecastCalibration.Replay(steady, model, 0.5);
            var actual = QuotaForecastCalibration.Replay(jitter, model, 0.5);
            Assert.NotEmpty(actual);
            Assert.Equal(expected.Select(x => (x.OriginUtc, x.OutcomeUtc, x.PredictedRemaining, x.ObservedRemaining)),
                actual.Select(x => (x.OriginUtc, x.OutcomeUtc, x.PredictedRemaining, x.ObservedRemaining)));
            // Appending future data cannot change already matured predictions.
            var prefix = QuotaForecastCalibration.Replay(jitter.Take(25).ToArray(), model, 0.5);
            Assert.Equal(prefix, actual.Where(x => x.OutcomeUtc <= jitter[24].CapturedAtUtc));
        }
    }

    [Fact]
    public void JitterToleranceDoesNotChainOrCrossDropsMetadataOrElapsedBoundaries()
    {
        var drift = Enumerable.Range(0, 4).Select(i => Point(i, 10 + i) with
            { ResetsAtUtc = Start.AddHours(5).AddSeconds(i) }).ToArray();
        Assert.Equal(new[] { 2, 2 }, QuotaForecastCalibration.SplitEpochs(drift).Select(x => x.Count));
        var first = Point(0, 50);
        foreach (var changed in new[]
        {
            Point(1, 5) with { ResetsAtUtc = first.ResetsAtUtc!.Value.AddSeconds(1) },
            Point(1, 60) with { PlanType = "new-plan" },
            Point(1, 60) with { WindowMinutes = 301 },
            Point(1, 60) with { LimitId = "other" },
            Point(5, 60) with { CapturedAtUtc = Start.AddHours(5).AddMilliseconds(500), ResetsAtUtc = Start.AddHours(5).AddSeconds(1) }
        }) Assert.Equal(2, QuotaForecastCalibration.SplitEpochs([first, changed]).Count);
    }

    [Fact]
    public void JitterDoesNotManufactureIndependentCalibrationGenerations()
    {
        var trials = Enumerable.Range(0, 10).Select(i => new QuotaForecastTrial("epoch", "2h",
            Start.AddDays(-2).AddMinutes(i), Start.AddDays(-2).AddHours(2).AddMinutes(i),
            Start.AddDays(-1).AddSeconds(i % 2), 2, 60, 50, null, null, 0, null, false, null)).ToArray();
        Assert.Single(QuotaForecastCalibration.ResetGenerations(trials));
        var errors = QuotaForecastCalibration.CalibrationErrors(trials, Start, Start.AddHours(5), 2);
        Assert.Single(errors);
        Assert.Null(QuotaForecastCalibration.ErrorRadius(errors));
    }

    [Fact]
    public void ReanchorsAndUnannouncedDropsTerminateSegments()
    {
        var rows = new[] { Point(0, 10), Point(1, 50), Point(2, 5), Point(3, 15) };
        var epochs = QuotaForecastCalibration.SplitEpochs(rows);
        Assert.Equal(2, epochs.Count);
        Assert.Equal(10, QuotaPaceModels.Estimate(epochs[1], "epoch"));
        var forecast = QuotaPredictionService.BuildResetOutlook(rows, Start.AddHours(3));
        Assert.Equal(10, forecast.BurnRatePercentPerHour);
    }

    [Fact]
    public void TimeBasedPaceDoesNotChangeWithExtraFlatPollingSamples()
    {
        var sparse = new[] { Point(0, 10), Point(1, 20), Point(2, 30) };
        var dense = sparse.Append(Point(0.9, 10)).Append(Point(1.9, 20)).OrderBy(x => x.CapturedAtUtc).ToArray();
        Assert.Equal(QuotaPaceModels.Estimate(sparse, "epoch"), QuotaPaceModels.Estimate(dense, "epoch"));
    }

    [Fact]
    public void FixedHorizonCalibrationIncludesCurrentCycleButCannotSeeFutureOutcomes()
    {
        QuotaForecastTrial Trial(DateTimeOffset reset, DateTimeOffset outcome, double error) => new("epoch", "2h", outcome.AddHours(-2),
            outcome, reset, 2, 50 + error, 50, null, null, 0, null, false, null);
        var trials = new[]
        {
            Trial(Start.AddDays(-1), Start.AddDays(-2), 3),
            Trial(Start.AddDays(-1), Start.AddDays(-1).AddHours(-1), 4),
            Trial(Start.AddHours(5), Start.AddHours(-1), 999),
            Trial(Start.AddDays(-3), Start.AddHours(1), 999)
        };
        var errors = QuotaForecastCalibration.CalibrationErrors(trials, Start, Start.AddHours(5), 2);
        Assert.Equal(new[] { 3d, 4d, 999d }, errors);
        var resetErrors = QuotaForecastCalibration.CalibrationErrors(
            trials.Select(x => x with { Target = "near-reset-proxy" }), Start, Start.AddHours(5), 2);
        Assert.Equal(4, Assert.Single(resetErrors));
        Assert.Null(QuotaForecastCalibration.ErrorRadius(errors));
    }

    [Fact]
    public void MissingTerminalObservationDoesNotBecomeSurvivalLabel()
    {
        var rows = Enumerable.Range(0, 9).Select(i => Point(i * 0.5, 10 + i)).ToArray();
        var trials = QuotaForecastCalibration.Replay(rows, "epoch", 0.5);
        Assert.NotEmpty(trials);
        Assert.All(trials, trial => Assert.Null(trial.ObservedExhaustion));
        Assert.Empty(QuotaForecastCalibration.Replay(rows, "epoch", null));
    }

    [Fact]
    public void DifferentSourcesNeverSupplyCurrentSlope()
    {
        var forecast = QuotaPredictionService.BuildResetOutlook([Point(0, 10) with { Source = "embedded" }, Point(1, 50)], Start.AddHours(1));
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.ProjectedRemainingAtResetPercent);
    }

    private static QuotaSnapshot Point(double hours, double used) => new(QuotaWindowKind.FiveHour, Start.AddHours(hours), used,
        300, Start.AddHours(5), "codex", "default", "codex-app-server:codex");
}
