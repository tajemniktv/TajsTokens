using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class SystemTrayStatusPresenterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OmittedWindowIsHealthyAndDoesNotConstrainReportedQuota(bool hasHistory)
    {
        var snapshot = CreateSnapshot(hasHistory, true);
        var status = SystemTrayStatusPresenter.Build(snapshot);

        Assert.True(status.IsFresh);
        Assert.Equal("live", status.HealthLabel);
        Assert.Equal(70, status.ConstrainedRemainingPercent);
        Assert.Contains("5h not reported", status.Tooltip);
        Assert.Contains("week 70%", status.Tooltip);
        Assert.DoesNotContain("5h 1%", status.Tooltip);
    }

    [Fact]
    public void StaleWindowWithoutOmissionRemainsPartial()
    {
        var status = SystemTrayStatusPresenter.Build(CreateSnapshot(true, false));
        Assert.False(status.IsFresh);
        Assert.Equal("partial", status.HealthLabel);
        Assert.Equal(1, status.ConstrainedRemainingPercent);
        Assert.Contains("5h 1%", status.Tooltip);
    }

    [Fact]
    public void ReportedWindowResumesNormalDisplay()
    {
        var snapshot = CreateSnapshot(true, true);
        snapshot = snapshot with
        {
            QuotaLanes = snapshot.QuotaLanes.Select(lane => lane with
            {
                NotReportedByProvider = false,
                State = TelemetryHealthState.Live
            }).ToArray()
        };
        var status = SystemTrayStatusPresenter.Build(snapshot);
        Assert.True(status.IsFresh);
        Assert.Equal(1, status.ConstrainedRemainingPercent);
        Assert.DoesNotContain("not reported", status.Tooltip);
    }

    private static TelemetrySnapshot CreateSnapshot(bool hasHistory, bool omitted)
    {
        var now = DateTimeOffset.UtcNow;
        var fiveHour = new QuotaSnapshot(QuotaWindowKind.FiveHour, now.AddMinutes(-5), 99, 300,
            now.AddHours(1), "codex", "default", "app-server");
        var weekly = new QuotaSnapshot(QuotaWindowKind.Weekly, now, 30, 10080,
            now.AddDays(1), "codex", "default", "app-server");
        return TelemetrySnapshot.Empty with
        {
            QuotaSnapshots = hasHistory ? [fiveHour, weekly] : [weekly],
            QuotaDataFresh = omitted,
            QuotaLanes =
            [
                new(QuotaWindowKind.FiveHour, "codex", "default", hasHistory ? fiveHour : null,
                    hasHistory ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                    NotReportedByProvider: omitted),
                new(QuotaWindowKind.Weekly, "codex", "default", weekly, TelemetryHealthState.Live)
            ]
        };
    }
}
