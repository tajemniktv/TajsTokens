using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetryDiagnosticsPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly QuotaSnapshot Quota = new(QuotaWindowKind.FiveHour, Now, 20, 300,
        Now.AddHours(5), "codex", "default", "codex-app-server:codex", "fixture-account");

    [Fact]
    public void EmptySnapshotIsWaitingNotAnInventedFailure()
    {
        var result = TelemetryDiagnosticsPresenter.Present(TelemetrySnapshot.Empty);
        Assert.Contains("Waiting", result.Summary);
        Assert.Empty(result.Sources);
        Assert.All(result.QuotaWindows, window => Assert.Equal("Unavailable", window.Status));
    }

    [Fact]
    public void SourceFailureKeepsExactDetailAndOffersNonDestructiveGuidance()
    {
        var result = TelemetryDiagnosticsPresenter.Present(TelemetrySnapshot.Empty with
        {
            CapturedAtUtc = Now,
            Sources = [new("SQLite", TelemetryHealthState.Error, "sanitized fixture: access denied")]
        });
        var source = Assert.Single(result.Sources);
        Assert.Equal("Error", source.Status);
        Assert.Contains("sanitized fixture: access denied", source.Detail);
        Assert.Contains("do not delete", source.Detail);
        Assert.Contains("No successful read", source.Summary);
    }

    [Fact]
    public void OmittedAndStaleLanesRemainDistinct()
    {
        var snapshot = Snapshot(TelemetryHealthState.Stale) with
        {
            QuotaLanes = [new(QuotaWindowKind.FiveHour, "codex", "default", Quota, TelemetryHealthState.Stale, Now),
                new(QuotaWindowKind.Weekly, "codex", "default", null, TelemetryHealthState.Unavailable, null) { NotReportedByProvider = true }]
        };
        var result = TelemetryDiagnosticsPresenter.Present(snapshot);
        Assert.Equal("Stale", result.QuotaWindows[0].Status);
        Assert.Contains("paused", result.QuotaWindows[0].Summary);
        Assert.Equal("Not reported", result.QuotaWindows[1].Status);
        Assert.Contains("not a failed read", result.QuotaWindows[1].Detail);
        Assert.Contains("does not mean unlimited", result.QuotaWindows[1].Detail);
    }

    [Fact]
    public void ForecastExplainsMatchingAnchorOnly()
    {
        var current = new CurrentQuotaForecast(Quota, TelemetryHealthState.Live, null, "Fixture policy", "Fixture reason");
        var matching = TelemetryDiagnosticsPresenter.Present(Snapshot(TelemetryHealthState.Live) with { CurrentForecasts = [current] });
        Assert.Contains("Fixture reason", matching.QuotaWindows[0].Detail);
        Assert.Contains("80%", matching.QuotaWindows[0].Summary);
        var otherAccount = current with { Current = Quota with { AccountKey = "other" } };
        var unmatched = TelemetryDiagnosticsPresenter.Present(Snapshot(TelemetryHealthState.Live) with { CurrentForecasts = [otherAccount] });
        Assert.DoesNotContain("Fixture reason", unmatched.QuotaWindows[0].Detail);
        Assert.Contains("not substituted", unmatched.QuotaWindows[0].Detail);
    }

    private static TelemetrySnapshot Snapshot(TelemetryHealthState health) => TelemetrySnapshot.Empty with
    {
        CapturedAtUtc = Now,
        QuotaSnapshots = [Quota],
        QuotaLanes = [new(Quota.Kind, Quota.Provider, Quota.Profile, Quota, health, Now)]
    };
}
