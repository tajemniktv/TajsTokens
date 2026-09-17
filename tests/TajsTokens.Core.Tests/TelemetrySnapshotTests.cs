using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetrySnapshotTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddHours(1);

    [Fact]
    public void ExactAnchorValue_RetainsFreshnessAndForecast()
    {
        var anchor = Anchor();
        var telemetry = Snapshot(anchor);

        Assert.True(telemetry.IsQuotaSnapshotFresh(anchor with { }));
        Assert.Same(telemetry.CurrentForecasts[0], telemetry.FindCurrentForecast(anchor with { }));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("reset")]
    [InlineData("duration")]
    [InlineData("meter")]
    [InlineData("capture")]
    public void DifferentObservation_CannotBorrowFreshnessOrForecast(string difference)
    {
        var anchor = Anchor();
        var alternate = difference switch
        {
            "source" => anchor with { Source = "codex-rollout" },
            "reset" => anchor with { ResetsAtUtc = anchor.ResetsAtUtc!.Value.AddHours(5) },
            "duration" => anchor with { WindowMinutes = 301 },
            "meter" => anchor with { UsedPercent = 96 },
            _ => anchor with { CapturedAtUtc = Now.AddMinutes(-1) }
        };
        var telemetry = Snapshot(anchor) with { QuotaSnapshots = [alternate] };

        // Lane lookup remains useful for lane-level diagnostics, not observation freshness.
        Assert.NotNull(telemetry.FindQuotaLane(alternate));
        Assert.False(telemetry.IsQuotaSnapshotFresh(alternate));
        Assert.Null(telemetry.FindCurrentForecast(alternate));
        Assert.Empty(new QuotaAlertEngine([10]).Evaluate(telemetry));
    }

    private static QuotaSnapshot Anchor() => new(
        QuotaWindowKind.FiveHour, Now, 95, 300, Now.AddHours(4),
        "codex", "default", "codex-app-server");

    private static TelemetrySnapshot Snapshot(QuotaSnapshot anchor) => new(
        Now, RefreshTrigger.Interval, [], [], [anchor], false, true, true, [], [])
    {
        QuotaLanes =
        [
            new QuotaLaneState(anchor.Kind, anchor.Provider, anchor.Profile, anchor,
                TelemetryHealthState.Live, Now)
        ],
        CurrentForecasts =
        [
            new CurrentQuotaForecast(anchor, TelemetryHealthState.Live,
                new Forecast(anchor.Kind, Now, 1, null, true, 2, 0), "same-source history")
        ]
    };
}
