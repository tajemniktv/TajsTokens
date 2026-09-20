// Taj's Tokens | CodexNowcastActivity.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public enum NowcastActivityState
{
    RecentActivity,
    QuietOpenTurn,
    NoRecentActivity,
    Unknown,
}

public sealed record NowcastActivity(
    NowcastActivityState State,
    DateTimeOffset? LastActivityAtUtc,
    int ObservedOpenTurns,
    string Explanation)
{
    public bool SupportsNowcast => State == NowcastActivityState.RecentActivity;
}

/// <summary>Observed local activity, not a liveness oracle or a calibrated activity probability.</summary>
public static class CodexNowcastActivity
{
    public const string Policy = "local-nowcast-activity/v1";
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

    internal static bool IsActivityEvent(string type)
    {
        return type is "task_started" or "function_call" or
            "function_call_output" or "custom_tool_call" or "custom_tool_call_output";
    }

    public static NowcastActivity Evaluate(
        CodexForecastDataset data,
        DateTimeOffset origin,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime)
    {
        bool Available(DateTimeOffset? at)
        {
            return availability != ForecastReplayAvailability.CollectedByOrigin || at <= origin;
        }

        IEnumerable<CodexPredictiveTokenEvent> tokens =
            data.Tokens.Where(x => x.ObservedAtUtc <= origin && Available(x.CapturedAtUtc) && x.ReportedTotalTokens > 0);
        CodexWorkloadObservation[] workload = data.Workload.Where(x => x.ObservedAtUtc <= origin && Available(x.CapturedAtUtc)).ToArray();
        // Settings, session metadata and context snapshots alone do not prove ongoing work.
        IEnumerable<CodexWorkloadObservation> activity = workload.Where(x => IsActivityEvent(x.EventType));
        DateTimeOffset? latest = tokens.Select(x => (DateTimeOffset?)x.ObservedAtUtc)
            .Concat(activity.Select(x => x.ObservedAtUtc)).DefaultIfEmpty().Max();
        int open = workload.Where(x => x.TurnId is not null && x.EventType is "task_started" or "task_complete" or "turn_aborted")
            .GroupBy(x => (x.SessionId, x.TurnId))
            .Count(g => g.Any(x => x.EventType == "task_started") && !g.Any(x => x.EventType is "task_complete" or "turn_aborted"));
        if (latest is { } at && origin - at < IdleAfter)
            return new NowcastActivity(
                NowcastActivityState.RecentActivity,
                at,
                open,
                "Recent local token/turn/tool activity; continued activity is uncertain. No activity probability is estimated.");
        if (open > 0)
            return new NowcastActivity(
                NowcastActivityState.QuietOpenTurn,
                latest,
                open,
                "Nowcast paused: quiet observed open turn(s). Work may still be running or collection may be incomplete; quota outlook uses its independent pace/history model.");
        if (latest is not null)
            return new NowcastActivity(
                NowcastActivityState.NoRecentActivity,
                latest,
                0,
                "Nowcast paused: no local activity observed in the last 10 minutes. This is not proof of no Codex activity; quota outlook remains independent.");
        return new NowcastActivity(
            NowcastActivityState.Unknown,
            null,
            0,
            "Nowcast unavailable: no supported local activity evidence. Refresh/collection status must be checked separately.");
    }
}