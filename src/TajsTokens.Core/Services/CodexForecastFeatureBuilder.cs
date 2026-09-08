using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Temporal projection over content-free observations. No latest-state joins or future completions.</summary>
public static class CodexForecastFeatureBuilder
{
    public static CodexForecastFeatures Build(CodexForecastDataset data, DateTimeOffset origin,
        double lookbackHours = 2, ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime)
    {
        if (!double.IsFinite(lookbackHours) || lookbackHours <= 0 || lookbackHours > 168)
            throw new ArgumentOutOfRangeException(nameof(lookbackHours));
        var from = origin.AddHours(-lookbackHours);
        bool Available(DateTimeOffset? captured) => availability == ForecastReplayAvailability.ReconstructedEventTime || captured <= origin;
        var metadata = data.Workload.Where(x => x.ObservedAtUtc <= origin && Available(x.CapturedAtUtc))
            .OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.StartByteOffset).ToArray();
        var tokens = data.Tokens.Where(x => x.ObservedAtUtc > from && x.ObservedAtUtc <= origin && Available(x.CapturedAtUtc)).ToArray();
        var contexts = data.Context.Where(x => x.ObservedAtUtc > from && x.ObservedAtUtc <= origin && Available(x.CapturedAtUtc)).ToArray();
        var sessionMeta = metadata.Where(x => x.EventType == "session_meta").GroupBy(x => x.SessionId)
            .ToDictionary(x => x.Key, x => x.Last());
        var subagentSessions = metadata.Where(x => x.RootTurnId is not null || x.ParentThreadId is not null || x.SessionSourceKind == "subagent")
            .Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
        var rootSessions = sessionMeta.Where(x => x.Value.SessionSourceKind is "cli" or "vscode" or "exec" or "mcp")
            .Select(x => x.Key).Where(x => !subagentSessions.Contains(x)).ToHashSet(StringComparer.Ordinal);
        var tokenSessions = tokens.Select(x => x.SessionId).Distinct().ToArray();

        var open = 0;
        var completed = 0;
        var wallHours = 0d;
        var overlapEdges = new List<(DateTimeOffset Time, int Delta)>();
        foreach (var turn in metadata.Where(x => x.TurnId is not null).GroupBy(x => (x.SessionId, x.TurnId)))
        {
            var start = turn.FirstOrDefault(x => x.EventType == "task_started");
            if (start?.ObservedAtUtc is not DateTimeOffset started || started > origin) continue;
            var terminal = turn.LastOrDefault(x => x.EventType is "task_complete" or "turn_aborted");
            if (terminal?.ObservedAtUtc is DateTimeOffset ended && ended >= started && ended > from)
            {
                completed++;
                // Wall-time overlap, not inferred model-compute/agent-hours. Native duration is
                // retained separately; it must not rewrite event-time activity retroactively.
                wallHours += (ended - (started > from ? started : from)).TotalHours;
                if (ended > started)
                {
                    overlapEdges.Add((started > from ? started : from, 1));
                    overlapEdges.Add((ended, -1));
                }
            }
            else if (terminal is null && turn.Any(x => x.ObservedAtUtc > from))
            {
                // Recently observed start/context without a terminal. This is not a liveness guarantee.
                open++;
                if (started < origin)
                {
                    overlapEdges.Add((started > from ? started : from, 1));
                    overlapEdges.Add((origin, -1));
                }
            }
        }
        long Sum(Func<CodexPredictiveTokenEvent, long> selector) => (long)Math.Min(long.MaxValue, tokens.Sum(x => (decimal)Math.Max(0, selector(x))));
        var total = Sum(x => x.ReportedTotalTokens);
        var input = tokens.Sum(x => (decimal)Math.Max(0, x.UncachedInputTokens) + Math.Max(0, x.CacheReadTokens) + Math.Max(0, x.CacheWriteTokens));
        var output = tokens.Sum(x => (decimal)Math.Max(0, x.NonReasoningOutputTokens) + Math.Max(0, x.ReasoningOutputTokens));
        IReadOnlyDictionary<string, double> Shares(Func<CodexPredictiveTokenEvent, string?> field) => tokens
            .Where(x => !string.IsNullOrWhiteSpace(field(x)))
            .GroupBy(x => field(x)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => total > 0 ? (double)(g.Sum(x => (decimal)Math.Max(0, x.ReportedTotalTokens)) / total) : 0);
        var context = contexts.Where(x => x.InputTokens is >= 0 && x.ContextWindowTokens is > 0)
            .OrderBy(x => x.ObservedAtUtc).LastOrDefault();
        var concurrent = 0;
        var peak = 0;
        foreach (var edge in overlapEdges.OrderBy(x => x.Time).ThenBy(x => x.Delta))
        {
            concurrent += edge.Delta;
            peak = Math.Max(peak, concurrent);
        }
        return new CodexForecastFeatures(origin, lookbackHours, total,
            input > 0 ? (double)(Sum(x => x.CacheReadTokens) / input) : null,
            output > 0 ? (double)(Sum(x => x.ReasoningOutputTokens) / output) : null,
            tokenSessions.Count(rootSessions.Contains), tokenSessions.Count(subagentSessions.Contains),
            tokenSessions.Count(x => !rootSessions.Contains(x) && !subagentSessions.Contains(x)),
            open, wallHours, completed, contexts.Count(x => x.IsCompaction),
            context is not null ? (double)context.InputTokens!.Value / context.ContextWindowTokens!.Value : null,
            Shares(x => x.Model), Shares(x => x.ReasoningEffort), availability, peak, tokens.Length, contexts.Length);
    }
}
