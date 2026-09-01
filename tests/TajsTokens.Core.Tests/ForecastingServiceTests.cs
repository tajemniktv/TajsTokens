using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ForecastingServiceTests
{
    private readonly ForecastingService _service = new();

    [Fact]
    public void BuildForecast_WhenBurnRateWouldExhaustBeforeReset_MarksAsNotSurviving()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(2);
        var snapshots = new List<QuotaSnapshot>
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 20, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 45, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 70, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.False(forecast.SurvivesUntilReset);
        Assert.Equal(ForecastState.ExhaustionLikelyBeforeReset, forecast.State);
        Assert.NotNull(forecast.EstimatedExhaustionAtUtc);
        Assert.True(forecast.EstimatedExhaustionAtUtc < snapshots[^1].ResetsAtUtc);
        Assert.Equal(0, forecast.ProjectedRemainingAtResetPercent);
    }

    [Fact]
    public void BuildForecast_WhenBurnRateIsLow_MarksAsSurviving()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(2);
        var snapshots = new List<QuotaSnapshot>
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 12, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 14, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 16, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.True(forecast.SurvivesUntilReset);
        Assert.Equal(ForecastState.SafeUntilReset, forecast.State);
        Assert.True(forecast.BurnRatePercentPerHour > 0);
        Assert.True(forecast.SustainablePercentPerHour > 0);
        Assert.True(forecast.ProjectedRemainingAtResetPercent > 0);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
    }

    [Fact]
    public void BuildForecast_WithInsufficientData_PreservesWindowKindAndReturnsUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new[] { Snapshot(QuotaWindowKind.Weekly, now, 15, now.AddDays(4)) };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.Equal(QuotaWindowKind.Weekly, forecast.Kind);
        Assert.Equal(ForecastState.Learning, forecast.State);
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
        Assert.Null(forecast.SurvivesUntilReset);
        Assert.NotNull(forecast.SustainablePercentPerHour);
    }

    [Fact]
    public void BuildForecast_ResetDecrease_IsNotTreatedAsNegativeBurn()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(2);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-3), 80, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 5, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 15, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 25, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.InRange(forecast.BurnRatePercentPerHour!.Value, 9.9, 10.1);
    }

    [Fact]
    public void BuildForecast_ChangedResetBoundary_WithPositiveDelta_DoesNotCrossWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var oldReset = now.AddHours(-2);
        var currentReset = now.AddHours(3);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-3), 20, oldReset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 80, currentReset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 90, currentReset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.InRange(forecast.BurnRatePercentPerHour!.Value, 9.9, 10.1);
    }

    [Fact]
    public void BuildForecast_FutureSamples_AreIgnored()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(3);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 20, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(1), 95, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.InRange(forecast.BurnRatePercentPerHour!.Value, 9.9, 10.1);
    }

    [Fact]
    public void BuildForecast_WhenAllSamplesAreFuture_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddMinutes(1), 10, now.AddHours(3))
        };

        Assert.Throws<ArgumentException>(() => _service.BuildForecast(snapshots, now));
    }

    [Fact]
    public void BuildForecast_MixedWindowKinds_Throws()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, now.AddHours(2)),
            Snapshot(QuotaWindowKind.Weekly, now, 20, now.AddDays(4))
        };

        Assert.Throws<ArgumentException>(() => _service.BuildForecast(snapshots, now));
    }

    [Fact]
    public void BuildForecast_UnknownQuotaValue_ReturnsUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(2);
        var snapshots = new[]
        {
            new QuotaSnapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), null, 300, reset, "codex", "default", "test"),
            new QuotaSnapshot(QuotaWindowKind.FiveHour, now, null, 300, reset, "codex", "default", "test")
        };

        var forecast = _service.BuildForecast(snapshots, now);
        Assert.Equal(ForecastState.Learning, forecast.State);
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.SurvivesUntilReset);
    }

    [Fact]
    public void BuildForecast_ExhaustionAfterReset_IsSuppressedAndMarginIsPrimary()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(1);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 20, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 22, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 24, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.True(forecast.SurvivesUntilReset);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
        Assert.InRange(forecast.ProjectedRemainingAtResetPercent!.Value, 73.9, 74.1);
        Assert.InRange(forecast.BurnPressure!.Value, 0, 1);
    }

    [Fact]
    public void BuildForecast_FlatQuantizedMeter_DoesNotClaimExactZeroBurn()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(2);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddMinutes(-30), 41, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddMinutes(-20), 41, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddMinutes(-10), 41, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 41, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.Equal(ForecastState.IdleWithinMeterPrecision, forecast.State);
        Assert.True(forecast.IsQuantizedFlat);
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
        Assert.Null(forecast.SurvivesUntilReset);
        Assert.True(forecast.Confidence < 0.5);
    }

    [Fact]
    public void BuildForecast_MixedFlatAndMovingIntervals_RetainsFlatSamplesInEwma()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(4);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 10, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 10, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 30, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.False(forecast.IsQuantizedFlat);
        Assert.InRange(forecast.BurnRatePercentPerHour!.Value, 8.99, 9.01);
    }

    [Fact]
    public void BuildForecast_ExpiredObservedEpoch_ReturnsLearningUntilNewProviderSample()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredReset = now.AddMinutes(-1);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 30, expiredReset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 50, expiredReset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.Equal(ForecastState.Learning, forecast.State);
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
        Assert.Null(forecast.SurvivesUntilReset);
    }

    [Fact]
    public void BuildForecast_RisingRecentRates_ReportsAcceleratingTrend()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(2);
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-4), 10, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-3), 12, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 16, reset),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 24, reset),
            Snapshot(QuotaWindowKind.FiveHour, now, 36, reset)
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.Equal("accelerating", forecast.Trend);
    }

    private static QuotaSnapshot Snapshot(QuotaWindowKind kind, DateTimeOffset captured, double used, DateTimeOffset reset) =>
        new(kind, captured, used, kind == QuotaWindowKind.FiveHour ? 300 : 10_080, reset, "codex", "default", "test");
}
