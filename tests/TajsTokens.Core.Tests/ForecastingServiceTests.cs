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
        var snapshots = new List<QuotaSnapshot>
        {
            new(QuotaWindowKind.FiveHour, now.AddHours(-2), 20_000, 100_000, now.AddHours(2)),
            new(QuotaWindowKind.FiveHour, now.AddHours(-1), 45_000, 100_000, now.AddHours(2)),
            new(QuotaWindowKind.FiveHour, now, 70_000, 100_000, now.AddHours(2))
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.False(forecast.SurvivesUntilReset);
        Assert.NotNull(forecast.EstimatedExhaustionAtUtc);
        Assert.True(forecast.EstimatedExhaustionAtUtc < snapshots[^1].ResetsAtUtc);
    }

    [Fact]
    public void BuildForecast_WhenBurnRateIsLow_MarksAsSurviving()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<QuotaSnapshot>
        {
            new(QuotaWindowKind.FiveHour, now.AddHours(-2), 12_000, 100_000, now.AddHours(2)),
            new(QuotaWindowKind.FiveHour, now.AddHours(-1), 14_000, 100_000, now.AddHours(2)),
            new(QuotaWindowKind.FiveHour, now, 16_000, 100_000, now.AddHours(2))
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.True(forecast.SurvivesUntilReset);
        Assert.True(forecast.BurnRatePerHour > 0);
    }

    [Fact]
    public void BuildForecast_WithInsufficientData_ReturnsConservativeDefault()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<QuotaSnapshot>
        {
            new(QuotaWindowKind.FiveHour, now, 1_000, 100_000, now.AddHours(4))
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.Equal(0, forecast.BurnRatePerHour);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
    }
}
