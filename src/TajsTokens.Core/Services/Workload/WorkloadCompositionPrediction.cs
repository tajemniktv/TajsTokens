// Taj's Tokens | WorkloadCompositionPrediction.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Shared scalar-to-vector projection; retrospective targets never enter composition.</summary>
public static class WorkloadCompositionPrediction
{
    public const string Version = "recent-workload-composition/v2";

    public static PredictedWorkload? Project(
        CodexForecastDataset data,
        DateTimeOffset origin,
        TokenHorizonPrediction prediction,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime)
    {
        if (!double.IsFinite(prediction.ExpectedTokens) || prediction.ExpectedTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(prediction));
        CodexPredictiveTokenEvent[] rows = data.Tokens.Where(x => x.ObservedAtUtc > origin.AddHours(-2) && x.ObservedAtUtc <= origin &&
                                                                  (availability == ForecastReplayAvailability.ReconstructedEventTime ||
                                                                   x.CapturedAtUtc <= origin)).ToArray();
        if (rows.Length == 0) return null;
        if (rows.Any(x => !QuotaEvaluationCoverageBuilder.HasCompleteCategories(x))) return null;

        double Sum(Func<CodexPredictiveTokenEvent, long> field)
        {
            return rows.Sum(x => (double)Math.Max(0, field(x)));
        }

        double[] parts =
        [
            Sum(x => x.UncachedInputTokens), Sum(x => x.CacheReadTokens), Sum(x => x.CacheWriteTokens),
            Sum(x => x.NonReasoningOutputTokens), Sum(x => x.ReasoningOutputTokens),
        ];
        double components = parts.Sum();
        if (components <= 0) return null; // Unknown composition is not an invented all-zero vector.
        double total = Sum(x => x.ReportedTotalTokens);

        IReadOnlyDictionary<string, double> Shares(Func<CodexPredictiveTokenEvent, string?> field)
        {
            return rows
                .Where(x => !string.IsNullOrWhiteSpace(field(x))).GroupBy(x => field(x)!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => total > 0 ? g.Sum(x => (double)Math.Max(0, x.ReportedTotalTokens)) / total : 0);
        }

        return new PredictedWorkload(
            origin,
            prediction.HorizonHours,
            prediction.ExpectedTokens,
            parts.Select(x => prediction.ExpectedTokens * x / components).ToArray(),
            Shares(x => x.Model),
            Shares(x => x.ReasoningEffort),
            rows.Length,
            rows.Max(x => x.ObservedAtUtc),
            availability,
            Version + ": scalar token forecast distributed by the preceding two hours' disjoint category composition. " +
            "Model/effort shares retain unknown mass; no future metadata or task completions. " +
            "Incomplete category evidence withholds composition; this is a prediction, not rewritten accounting.");
    }
}