using TajsTokens.Core.Models;

namespace TajsTokens.Core.Research;

internal static class QuotaCostAblations
{
    public static QuotaCostFeatureExtension? For(string candidate) => candidate switch
    {
        "context-ablation" => new(["compactions", "context-pressure", "context-missing"], row =>
            [(double)row.Features.Compactions, row.Features.LastInputWindowRatio ?? 0, row.Features.LastInputWindowRatio is null ? 1d : 0d]),
        "activity-ablation" => new(["root-sessions", "subagent-sessions", "unknown-sessions", "completed-turn-hours", "peak-overlap"], row =>
            [row.Features.TokenActiveRootSessions, row.Features.TokenActiveSubagentSessions, row.Features.TokenActiveUnknownSessions,
             row.Features.CompletedTurnWallHours, row.Features.PeakObservedTurnOverlap]),
        "runtime-ablation" => new(["ttft-seconds", "runtime-missing"], row =>
            [(row.MeanTtftMilliseconds ?? 0) / 1000, row.RuntimeSamples == 0 ? 1d : 0d]),
        "time-ablation" => new(["utc-week-sin", "utc-week-cos"], row =>
            [1 + Math.Sin(WeekAngle(row.StartUtc)), 1 + Math.Cos(WeekAngle(row.StartUtc))]),
        _ => null
    };
    private static double WeekAngle(DateTimeOffset time) =>
        2 * Math.PI * ((int)time.UtcDateTime.DayOfWeek * 24 + time.UtcDateTime.TimeOfDay.TotalHours) / 168;
}
