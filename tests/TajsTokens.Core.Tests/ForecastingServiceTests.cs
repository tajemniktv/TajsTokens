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
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 20, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 45, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now, 70, now.AddHours(2))
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
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 12, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 14, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now, 16, now.AddHours(2))
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.True(forecast.SurvivesUntilReset);
        Assert.True(forecast.BurnRatePercentPerHour > 0);
        Assert.True(forecast.SustainablePercentPerHour > 0);
    }

    [Fact]
    public void BuildForecast_WithInsufficientData_PreservesWindowKindAndReturnsUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new[] { Snapshot(QuotaWindowKind.Weekly, now, 15, now.AddDays(4)) };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.Equal(QuotaWindowKind.Weekly, forecast.Kind);
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.EstimatedExhaustionAtUtc);
        Assert.Null(forecast.SurvivesUntilReset);
    }

    [Fact]
    public void BuildForecast_ResetDecrease_IsNotTreatedAsNegativeBurn()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new[]
        {
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-3), 80, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-2), 5, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), 15, now.AddHours(2)),
            Snapshot(QuotaWindowKind.FiveHour, now, 25, now.AddHours(2))
        };

        var forecast = _service.BuildForecast(snapshots, now);

        Assert.InRange(forecast.BurnRatePercentPerHour!.Value, 9.9, 10.1);
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
        var snapshots = new[]
        {
            new QuotaSnapshot(QuotaWindowKind.FiveHour, now.AddHours(-1), null, 300, now.AddHours(2), "codex", "default", "test"),
            new QuotaSnapshot(QuotaWindowKind.FiveHour, now, null, 300, now.AddHours(2), "codex", "default", "test")
        };

        var forecast = _service.BuildForecast(snapshots, now);
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Null(forecast.SurvivesUntilReset);
    }

    private static QuotaSnapshot Snapshot(QuotaWindowKind kind, DateTimeOffset captured, double used, DateTimeOffset reset) =>
        new(kind, captured, used, kind == QuotaWindowKind.FiveHour ? 300 : 10_080, reset, "codex", "default", "test");
}
