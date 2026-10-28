using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>The scenario owner's existing selection policy, with denominators and overlapping reasons.</summary>
public static class ScenarioHistoryEligibility
{
    public static ScenarioHistorySelection Select(ScenarioRequest request, IReadOnlyList<ScenarioHistorySample> history,
        QuotaWindowKind kind, DateTimeOffset now)
    {
        var stages = new List<ForecastEligibilityStage>();
        ScenarioHistorySample[] rows = history.ToArray();
        void Filter(string stage, params (string Reason, Func<ScenarioHistorySample, bool> Reject)[] checks)
        {
            var reasons = checks.Select(c => (c.Reason, Count: rows.Count(c.Reject))).Where(x => x.Count > 0)
                .ToDictionary(x => x.Reason, x => x.Count, StringComparer.Ordinal);
            int before = rows.Length;
            rows = rows.Where(x => !checks.Any(c => c.Reject(x))).ToArray();
            stages.Add(new(stage, "history-intervals", before, rows.Length, reasons));
        }
        Filter("compatible-window-account-outcome",
            ("other-window", x => x.Kind != kind),
            ("other-account", x => x.AccountKey != request.AccountKey),
            ("outcome-not-yet-mature", x => x.EndUtc > now),
            ("invalid-interval", x => x.EndUtc <= x.StartUtc),
            ("invalid-quota-delta", x => !double.IsFinite(x.QuotaDeltaPercent) || x.QuotaDeltaPercent is < 0 or > 100),
            ("invalid-session-counts", x => x.RootAgents < 0 || x.Subagents < 0),
            ("other-requested-cohort", x => request.Cohorts.Count != 0 && (x.Cohort is null || !request.Cohorts.Contains(x.Cohort))),
            ("crosses-reset", x => x.ResetUtc is { } reset && x.EndUtc > reset));
        rows = rows.OrderBy(x => x.StartUtc).ToArray();
        if (rows.Length > 0 && now - rows.Max(x => x.EndUtc) > TimeSpan.FromDays(30))
        {
            stages.Add(new("fresh-history", "history-intervals", rows.Length, 0,
                new Dictionary<string, int> { ["history-older-than-30-days"] = rows.Length }));
            return new(rows, null, true, stages);
        }
        ScenarioHistorySample? latest = rows.MaxBy(x => x.EndUtc);
        Filter("latest-source-cohort", ("other-latest-source-or-cohort", x =>
            x.Source != latest?.Source || x.AccountKey != latest?.AccountKey || x.Cohort != latest?.Cohort));
        Filter("recent-requested-model-effort",
            ("older-than-30-days", x => x.EndUtc < now.AddDays(-30)),
            ("requested-model-unmatched", x => !string.IsNullOrWhiteSpace(request.Model) &&
                !string.Equals(x.DominantModel, request.Model, StringComparison.Ordinal)),
            ("requested-effort-unmatched", x => !string.IsNullOrWhiteSpace(request.ReasoningEffort) &&
                !string.Equals(x.DominantReasoningEffort, request.ReasoningEffort, StringComparison.Ordinal)));
        return new(rows, latest, false, stages);
    }

    public static IReadOnlyList<string> OriginReasons(CodexForecastFeatures features)
    {
        var reasons = new List<string>();
        if (features.ObservedTokenEvents == 0) reasons.Add("no-timely-token-events");
        if (features.TokenActiveRootSessions + features.TokenActiveSubagentSessions == 0) reasons.Add("no-classified-token-sessions");
        if (features.TokenActiveUnknownSessions > 0) reasons.Add("unknown-role-token-sessions");
        return reasons;
    }

    public static IReadOnlyList<string> RequestSupportReasons(ScenarioRequest request, IReadOnlyList<ScenarioHistorySample> samples)
    {
        var reasons = new List<string>();
        if (request.IntensityMultiplier != 1) reasons.Add("unsupported-intensity");
        if (samples.Count == 0) return reasons;
        if (request.DurationHours < samples.Min(x => (x.EndUtc - x.StartUtc).TotalHours) ||
            request.DurationHours > samples.Max(x => (x.EndUtc - x.StartUtc).TotalHours)) reasons.Add("duration-outside-support");
        if (request.RootAgents < samples.Min(x => x.RootAgents) || request.RootAgents > samples.Max(x => x.RootAgents))
            reasons.Add("root-sessions-outside-support");
        if (request.Subagents < samples.Min(x => x.Subagents) || request.Subagents > samples.Max(x => x.Subagents))
            reasons.Add("subagent-sessions-outside-support");
        return reasons;
    }
}
