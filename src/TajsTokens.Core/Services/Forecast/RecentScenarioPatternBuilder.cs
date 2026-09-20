// Taj's Tokens | RecentScenarioPatternBuilder.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public static class RecentScenarioPatternBuilder
{
    public static RecentScenarioPattern Build(CodexForecastDataset data, DateTimeOffset now)
    {
        RecentScenarioPattern Unknown(string reason)
        {
            return new RecentScenarioPattern(now, false, 0, 0, null, null, reason);
        }

        if (data.CapturedAtUtc < now.AddMinutes(-10)) return Unknown("Telemetry snapshot is stale; refresh before using a recent pattern.");
        CodexPredictiveTokenEvent? latest = data.Tokens
            .Where(x => x.ObservedAtUtc <= now && x.CapturedAtUtc <= now && x.ReportedTotalTokens > 0)
            .MaxBy(x => x.ObservedAtUtc);
        if (latest is null || latest.ObservedAtUtc < now.AddMinutes(-10))
            return Unknown(
                "No recent positive local token activity. This does not establish idleness or exclude activity elsewhere; enter a hypothetical workload manually.");
        CodexForecastFeatures features = CodexForecastFeatureBuilder.Build(
            data,
            now,
            availability: ForecastReplayAvailability.CollectedByOrigin);
        if (features.TokenActiveUnknownSessions > 0)
            return Unknown("Some recently token-active sessions have unknown root/subagent roles; counts cannot be filled reliably.");
        if (features.TokenActiveRootSessions + features.TokenActiveSubagentSessions == 0)
            return Unknown("No classified token-active sessions are available.");

        // Match the scenario history owner's two-hour, exclusive-model/effort semantics.
        string? Exclusive(IReadOnlyDictionary<string, double> shares)
        {
            return shares.Count == 1 && shares.First().Value >= .999 ? shares.First().Key : null;
        }

        string? model = Exclusive(features.ModelTokenShares);
        string? effort = Exclusive(features.EffortTokenShares);
        return new RecentScenarioPattern(
            now,
            true,
            features.TokenActiveRootSessions,
            features.TokenActiveSubagentSessions,
            model,
            effort,
            "Filled from token-active local sessions in the preceding two hours, with positive tokens in the last ten minutes. " +
            "Counts are not simultaneous compute, account membership or a prediction that this activity continues. Duration is unchanged; review and edit before estimating. " +
            (model is null ? "Model is mixed/unknown; its filter was cleared. " : "Model uses the exclusive observed mix. ") +
            (effort is null ? "Effort is mixed/unknown; its filter was cleared." : "Effort uses the exclusive observed mix."));
    }
}