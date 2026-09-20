// Taj's Tokens | QuotaCostAblations.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Research;

internal static class QuotaCostAblations
{
    public static QuotaCostFeatureExtension? For(string candidate)
    {
        return candidate switch
        {
            "context-ablation" => new QuotaCostFeatureExtension(
                ["compactions", "context-pressure", "context-missing"],
                row =>
                [
                    row.Features.Compactions, row.Features.LastInputWindowRatio ?? 0,
                    row.Features.LastInputWindowRatio is null ? 1d : 0d,
                ]),
            "activity-ablation" => new QuotaCostFeatureExtension(
                ["root-sessions", "subagent-sessions", "unknown-sessions", "completed-turn-hours", "peak-overlap"],
                row =>
                [
                    row.Features.TokenActiveRootSessions, row.Features.TokenActiveSubagentSessions, row.Features.TokenActiveUnknownSessions,
                    row.Features.CompletedTurnWallHours, row.Features.PeakObservedTurnOverlap,
                ]),
            "runtime-ablation" => new QuotaCostFeatureExtension(
                ["ttft-seconds", "runtime-missing"],
                row =>
                    [(row.MeanTtftMilliseconds ?? 0) / 1000, row.RuntimeSamples == 0 ? 1d : 0d]),
            "time-ablation" => new QuotaCostFeatureExtension(
                ["utc-week-sin", "utc-week-cos"],
                row =>
                    [1 + Math.Sin(WeekAngle(row.StartUtc)), 1 + Math.Cos(WeekAngle(row.StartUtc))]),
            _ => null,
        };
    }

    private static double WeekAngle(DateTimeOffset time)
    {
        return 2 * Math.PI * ((int)time.UtcDateTime.DayOfWeek * 24 + time.UtcDateTime.TimeOfDay.TotalHours) / 168;
    }
}