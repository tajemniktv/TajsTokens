// Taj's Tokens | QuotaAlertEngineTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class QuotaAlertEngineTests
{
    [Fact]
    public void LowQuota_EmitsOnlyNearestCrossedThresholdPerWindow()
    {
        var engine = new QuotaAlertEngine([30, 20, 10, 5]);
        DateTimeOffset reset = DateTimeOffset.UnixEpoch.AddHours(5);

        IReadOnlyList<AlertNotification> alerts = engine.Evaluate(Snapshot(4, reset));

        AlertNotification alert = Assert.Single(alerts);
        Assert.Contains("low", alert.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4%", alert.Message, StringComparison.Ordinal);
        Assert.Contains(":5", alert.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void LowQuota_DoesNotRepeatSameThresholdForSameWindow()
    {
        var engine = new QuotaAlertEngine([30, 20, 10, 5]);
        DateTimeOffset reset = DateTimeOffset.UnixEpoch.AddHours(5);

        Assert.Single(engine.Evaluate(Snapshot(19, reset)));
        Assert.Empty(engine.Evaluate(Snapshot(18, reset)));
        Assert.Single(engine.Evaluate(Snapshot(9, reset)));
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
        IReadOnlyList<AlertNotification> replenished = engine.Evaluate(Snapshot(90, null, DateTimeOffset.UnixEpoch.AddHours(1)));
        Assert.Contains(replenished, alert => alert.Title.Contains("refreshed", StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<AlertNotification> nextWindowLow = engine.Evaluate(Snapshot(9, null, DateTimeOffset.UnixEpoch.AddHours(2)));
        Assert.Contains(nextWindowLow, alert => alert.Title.Contains("low", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpdateThresholds_AppliesToSubsequentEvaluations()
    {
        var engine = new QuotaAlertEngine([30]);
        DateTimeOffset reset = DateTimeOffset.UnixEpoch.AddHours(5);

        Assert.Single(engine.Evaluate(Snapshot(20, reset)));
        engine.UpdateThresholds([10]);

        IReadOnlyList<AlertNotification> alerts = engine.Evaluate(Snapshot(9, reset, DateTimeOffset.UnixEpoch.AddMinutes(2)));
        Assert.Single(alerts);
        Assert.Contains(":10", alerts[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleQuota_DoesNotGenerateLowQuotaAlert()
    {
        var engine = new QuotaAlertEngine([30, 20, 10, 5]);
        TelemetrySnapshot snapshot = Snapshot(4, DateTimeOffset.UnixEpoch.AddHours(5)) with { QuotaDataFresh = false };

        Assert.Empty(engine.Evaluate(snapshot));
    }

    [Fact]
    public void PartialQuotaGeneration_AlertsFreshLaneAndIgnoresStaleLane()
    {
        var engine = new QuotaAlertEngine([10]);
        DateTimeOffset captured = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var fiveHour = new QuotaSnapshot(
            QuotaWindowKind.FiveHour,
            captured,
            95,
            300,
            captured.AddHours(5),
            "codex",
            "default",
            "test");
        var weekly = new QuotaSnapshot(
            QuotaWindowKind.Weekly,
            captured,
            95,
            10_080,
            captured.AddDays(7),
            "codex",
            "default",
            "test");
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
                new QuotaLaneState(QuotaWindowKind.Weekly, "codex", "default", weekly, TelemetryHealthState.Stale, captured),
            ],
        };

        AlertNotification alert = Assert.Single(engine.Evaluate(snapshot));
        Assert.Contains(nameof(QuotaWindowKind.FiveHour), alert.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowAdvanceWithRecoveredQuota_EmitsResetAlertOnce()
    {
        var engine = new QuotaAlertEngine([10, 5]);
        DateTimeOffset firstReset = DateTimeOffset.UnixEpoch.AddHours(5);
        DateTimeOffset secondReset = firstReset.AddHours(5);

        Assert.Empty(engine.Evaluate(Snapshot(40, firstReset)));
        IReadOnlyList<AlertNotification> alerts = engine.Evaluate(Snapshot(95, secondReset));

        AlertNotification alert = Assert.Single(alerts);
        Assert.Contains("refreshed", alert.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(engine.Evaluate(Snapshot(94, secondReset)));
    }

    private static TelemetrySnapshot Snapshot(
        double remaining,
        DateTimeOffset? reset,
        DateTimeOffset? capturedAt = null)
    {
        DateTimeOffset captured = capturedAt ?? DateTimeOffset.UnixEpoch.AddMinutes(1);
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