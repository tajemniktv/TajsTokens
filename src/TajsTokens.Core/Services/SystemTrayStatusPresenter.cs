// Taj's Tokens | SystemTrayStatusPresenter.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public static class SystemTrayStatusPresenter
{
    public static SystemTrayStatus Build(CodexIntelligenceSnapshot snapshot)
    {
        return Build(snapshot.CurrentState);
    }

    public static SystemTrayStatus Build(TelemetrySnapshot snapshot)
    {
        return Build(CodexIntelligenceProjection.CurrentState(snapshot));
    }

    public static SystemTrayStatus Build(CodexCurrentState snapshot)
    {
        QuotaSnapshot? fiveHour = LatestQuota(snapshot, QuotaWindowKind.FiveHour);
        QuotaSnapshot? weekly = LatestQuota(snapshot, QuotaWindowKind.Weekly);
        double[] remainingValues = new[] { fiveHour?.RemainingPercent, weekly?.RemainingPercent }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();

        int? constrained = remainingValues.Length == 0
            ? null
            : (int)Math.Round(remainingValues.Min(), MidpointRounding.AwayFromZero);

        bool anyFreshLane = snapshot.QuotaLanes.Count == 0
            ? snapshot.QuotaDataFresh
            : snapshot.QuotaLanes.Any(lane => lane.IsFresh);
        bool allFreshLanes = snapshot.QuotaLanes.Count == 0
            ? snapshot.QuotaDataFresh
            : snapshot.QuotaLanes.Count > 0 && snapshot.QuotaLanes.All(lane => lane.IsFresh || lane.NotReportedByProvider);
        string health = allFreshLanes
            ? "live"
            : anyFreshLane
                ? "partial"
                : snapshot.QuotaSnapshots.Count > 0
                    ? "stale"
                    : "quota unavailable";

        string tooltip =
            $"TajsTokens · 5h {FormatQuota(snapshot, QuotaWindowKind.FiveHour, fiveHour)} · week {FormatQuota(snapshot, QuotaWindowKind.Weekly, weekly)} · {health}";
        return new SystemTrayStatus(tooltip, constrained, allFreshLanes, health);
    }

    private static QuotaSnapshot? LatestQuota(CodexCurrentState snapshot, QuotaWindowKind kind)
    {
        return snapshot.QuotaSnapshots
            .Where(item => item.Kind == kind && snapshot.FindQuotaLane(item)?.NotReportedByProvider != true)
            .OrderByDescending(item => item.CapturedAtUtc)
            .FirstOrDefault();
    }

    private static string FormatQuota(CodexCurrentState telemetry, QuotaWindowKind kind, QuotaSnapshot? snapshot)
    {
        return telemetry.QuotaLanes.Any(lane => lane.Kind == kind && lane.NotReportedByProvider)
            ? "not reported"
            : snapshot?.RemainingPercent is double remaining
                ? $"{remaining:0}%"
                : "?";
    }
}