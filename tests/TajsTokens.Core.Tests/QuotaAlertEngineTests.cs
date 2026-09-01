using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaAlertEngineTests
{
    [Fact]
    public void LowQuota_EmitsOnlyNearestCrossedThresholdPerWindow()
    {
        var engine = new QuotaAlertEngine([30, 20, 10, 5]);
        var reset = DateTimeOffset.UnixEpoch.AddHours(5);

        var alerts = engine.Evaluate(Snapshot(remaining: 4, reset));

        var alert = Assert.Single(alerts);
        Assert.Contains("low", alert.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4%", alert.Message, StringComparison.Ordinal);
        Assert.Contains(":5", alert.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void LowQuota_DoesNotRepeatSameThresholdForSameWindow()
    {
        var engine = new QuotaAlertEngine([30, 20, 10, 5]);
        var reset = DateTimeOffset.UnixEpoch.AddHours(5);

        Assert.Single(engine.Evaluate(Snapshot(remaining: 19, reset)));
        Assert.Empty(engine.Evaluate(Snapshot(remaining: 18, reset)));
        Assert.Single(engine.Evaluate(Snapshot(remaining: 9, reset)));
    }

    [Fact]
    public void LowQuota_WithoutResetTimestamp_DoesNotRepeatAtHourBoundary()
    {
        var engine = new QuotaAlertEngine([10]);

        Assert.Single(engine.Evaluate(Snapshot(9, null, DateTimeOffset.UnixEpoch.AddMinutes(1))));
        Assert.Empty(engine.Evaluate(Snapshot(8, null, DateTimeOffset.UnixEpoch.AddHours(2))));
    }

    [Fact]
    public void LowQuota_WithoutResetTimestamp_RearmsAfterObservedReplenishment()
    {
        var engine = new QuotaAlertEngine([10]);

        Assert.Single(engine.Evaluate(Snapshot(9, null, DateTimeOffset.UnixEpoch.AddMinutes(1))));
        var replenished = engine.Evaluate(Snapshot(90, null, DateTimeOffset.UnixEpoch.AddHours(1)));
        Assert.Contains(replenished, alert => alert.Title.Contains("refreshed", StringComparison.OrdinalIgnoreCase));

        var nextWindowLow = engine.Evaluate(Snapshot(9, null, DateTimeOffset.UnixEpoch.AddHours(2)));
        Assert.Contains(nextWindowLow, alert => alert.Title.Contains("low", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateThresholds_AppliesToSubsequentEvaluations()
    {
        var engine = new QuotaAlertEngine([30]);
        var reset = DateTimeOffset.UnixEpoch.AddHours(5);

        Assert.Single(engine.Evaluate(Snapshot(20, reset)));
        engine.UpdateThresholds([10]);

        var alerts = engine.Evaluate(Snapshot(9, reset, DateTimeOffset.UnixEpoch.AddMinutes(2)));
        Assert.Single(alerts);
        Assert.Contains(":10", alerts[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleQuota_DoesNotGenerateLowQuotaAlert()
    {
        var engine = new QuotaAlertEngine([30, 20, 10, 5]);
        var snapshot = Snapshot(remaining: 4, DateTimeOffset.UnixEpoch.AddHours(5)) with { QuotaDataFresh = false };

        Assert.Empty(engine.Evaluate(snapshot));
    }

    [Fact]
    public void PartialQuotaGeneration_AlertsFreshLaneAndIgnoresStaleLane()
    {
        var engine = new QuotaAlertEngine([10]);
        var captured = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var fiveHour = new QuotaSnapshot(
            QuotaWindowKind.FiveHour, captured, 95, 300, captured.AddHours(5), "codex", "default", "test");
        var weekly = new QuotaSnapshot(
            QuotaWindowKind.Weekly, captured, 95, 10_080, captured.AddDays(7), "codex", "default", "test");
        var snapshot = new TelemetrySnapshot(
            captured,
            RefreshTrigger.Interval,
            [],
            [],
            [fiveHour, weekly],
            false,
            false,
            true,
            [new ProviderHealthSnapshot("Codex app-server", TelemetryHealthState.Stale, "partial", captured)],
            [])
        {
            QuotaLanes =
            [
                new QuotaLaneState(QuotaWindowKind.FiveHour, "codex", "default", fiveHour, TelemetryHealthState.Live, captured),
                new QuotaLaneState(QuotaWindowKind.Weekly, "codex", "default", weekly, TelemetryHealthState.Stale, captured)
            ]
        };

        var alert = Assert.Single(engine.Evaluate(snapshot));
        Assert.Contains(nameof(QuotaWindowKind.FiveHour), alert.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowAdvanceWithRecoveredQuota_EmitsResetAlertOnce()
    {
        var engine = new QuotaAlertEngine([10, 5]);
        var firstReset = DateTimeOffset.UnixEpoch.AddHours(5);
        var secondReset = firstReset.AddHours(5);

        Assert.Empty(engine.Evaluate(Snapshot(remaining: 40, firstReset)));
        var alerts = engine.Evaluate(Snapshot(remaining: 95, secondReset));

        var alert = Assert.Single(alerts);
        Assert.Contains("refreshed", alert.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(engine.Evaluate(Snapshot(remaining: 94, secondReset)));
    }

    private static TelemetrySnapshot Snapshot(
        double remaining,
        DateTimeOffset? reset,
        DateTimeOffset? capturedAt = null)
    {
        var captured = capturedAt ?? DateTimeOffset.UnixEpoch.AddMinutes(1);
        var quota = new QuotaSnapshot(
            QuotaWindowKind.FiveHour,
            captured,
            100 - remaining,
            300,
            reset,
            "codex",
            "default",
            "test");

        return new TelemetrySnapshot(
            captured,
            RefreshTrigger.Interval,
            [],
            [],
            [quota],
            false,
            true,
            true,
            [new ProviderHealthSnapshot("Codex app-server", TelemetryHealthState.Live, "ok", captured)],
            []);
    }
}
