using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class SystemTrayStatusPresenter
{
    public static SystemTrayStatus Build(TelemetrySnapshot snapshot)
    {
        var fiveHour = LatestQuota(snapshot, QuotaWindowKind.FiveHour);
        var weekly = LatestQuota(snapshot, QuotaWindowKind.Weekly);
        var remainingValues = new[] { fiveHour?.RemainingPercent, weekly?.RemainingPercent }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();

        int? constrained = remainingValues.Length == 0
            ? null
            : (int)Math.Round(remainingValues.Min(), MidpointRounding.AwayFromZero);

        var anyFreshLane = snapshot.QuotaLanes.Count == 0
            ? snapshot.QuotaDataFresh
            : snapshot.QuotaLanes.Any(lane => lane.IsFresh);
        var allFreshLanes = snapshot.QuotaLanes.Count == 0
            ? snapshot.QuotaDataFresh
            : snapshot.QuotaLanes.Count > 0 && snapshot.QuotaLanes.All(lane => lane.IsFresh || lane.NotReportedByProvider);
        var health = allFreshLanes
            ? "live"
            : anyFreshLane
                ? "partial"
                : snapshot.QuotaSnapshots.Count > 0
                    ? "stale"
                    : "quota unavailable";

        var tooltip = $"TajsTokens · 5h {FormatQuota(snapshot, QuotaWindowKind.FiveHour, fiveHour)} · week {FormatQuota(snapshot, QuotaWindowKind.Weekly, weekly)} · {health}";
        return new SystemTrayStatus(tooltip, constrained, allFreshLanes, health);
    }

    private static QuotaSnapshot? LatestQuota(TelemetrySnapshot snapshot, QuotaWindowKind kind) =>
        snapshot.QuotaSnapshots
            .Where(item => item.Kind == kind && snapshot.FindQuotaLane(item)?.NotReportedByProvider != true)
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefault();

    private static string FormatQuota(TelemetrySnapshot telemetry, QuotaWindowKind kind, QuotaSnapshot? snapshot) =>
        telemetry.QuotaLanes.Any(lane => lane.Kind == kind && lane.NotReportedByProvider)
            ? "not reported"
            : snapshot?.RemainingPercent is double remaining ? $"{remaining:0}%" : "?";
}
