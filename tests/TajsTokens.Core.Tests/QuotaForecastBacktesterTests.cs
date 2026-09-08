using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaForecastBacktesterTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void ReanchorsAndUnannouncedDropsTerminateSegments()
    {
        var rows = new[] { Point(0, 10), Point(1, 50), Point(2, 5), Point(3, 15) };
        var epochs = QuotaForecastBacktester.SplitEpochs(rows);
        Assert.Equal(2, epochs.Count);
        Assert.Equal(10, QuotaPaceModels.Estimate(epochs[1], "epoch"));
        var forecast = new ForecastingService().BuildForecast(rows, Start.AddHours(3));
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
    public void CalibrationCannotSeeFutureOutcomesOrCurrentResetAndUsesOneScorePerEpoch()
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
        var errors = QuotaForecastBacktester.CalibrationErrors(trials, Start, Start.AddHours(5), 2);
        Assert.Equal(4, Assert.Single(errors));
        Assert.Null(QuotaForecastBacktester.ErrorRadius(errors));
    }

    [Fact]
    public void MissingTerminalObservationDoesNotBecomeSurvivalLabel()
    {
        var rows = Enumerable.Range(0, 9).Select(i => Point(i * 0.5, 10 + i)).ToArray();
        var trials = QuotaForecastBacktester.Replay(rows, "epoch", 0.5);
        Assert.NotEmpty(trials);
        Assert.All(trials, trial => Assert.Null(trial.ObservedExhaustion));
        Assert.Empty(QuotaForecastBacktester.Replay(rows, "epoch", null));
    }

    [Fact]
    public void DifferentSourcesNeverSupplyCurrentSlope()
    {
        var forecast = new ForecastingService().BuildForecast([Point(0, 10) with { Source = "embedded" }, Point(1, 50)], Start.AddHours(1));
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.ProjectedRemainingAtResetPercent);
    }

    private static QuotaSnapshot Point(double hours, double used) => new(QuotaWindowKind.FiveHour, Start.AddHours(hours), used,
        300, Start.AddHours(5), "codex", "default", "codex-app-server:codex");
}
