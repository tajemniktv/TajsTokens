// Taj's Tokens | WorkloadCompositionPrediction.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Shared scalar-to-vector projection; retrospective targets never enter composition.</summary>
public static partial class WorkloadCompositionPrediction
{
    public const string Version = "recent-workload-composition/v3-joint";
    public const string ContinuationVersion = "recent-joint-continuation/v1";
    public const string ContinuationMethodology = "Installation-local recorded joint model/effort/category amounts continued at their observed elapsed-time intensity. " +
        "Observation interval is (origin-context,origin]; horizon is (origin,origin+horizon]. " +
        "Elapsed context includes quiet time and tool waits; no future sleep or extra effort multiplier is assumed. " +
        "No fitting or training span for this deterministic continuation; quota calibration, if used, has a separate preceding training span. " +
        "Native pairings and unknown/overflow mass remain together. Missing context is unavailable, not zero; explicit zero observations remain distinct. " +
        "Collection-qualified mode excludes unknown/late collection and all future events. Staleness/coverage remain inspectable; absence of recorded tokens does not establish idleness or account completeness. " +
        "This is a conditional workload projection, not expected habitual activity, a task-equivalent changed-model prediction or calibrated distribution.";

    public static WorkloadContinuationResult Continue(CodexForecastDataset data, DateTimeOffset origin,
        double contextHours, double horizonHours, ForecastReplayAvailability availability = ForecastReplayAvailability.CollectedByOrigin)
    {
        Validate(contextHours, horizonHours, availability);
        var rows = data.Tokens.Where(x => x.ObservedAtUtc > origin.AddHours(-contextHours) && x.ObservedAtUtc <= origin &&
            (availability == ForecastReplayAvailability.ReconstructedEventTime || x.CapturedAtUtc <= origin)).ToArray();
        return ContinueWindow(rows, origin, contextHours, horizonHours, availability);
    }

    internal static WorkloadContinuationResult Continue(CodexForecastFeatureIndex index, DateTimeOffset origin,
        double contextHours, double horizonHours, ForecastReplayAvailability availability)
    {
        Validate(contextHours, horizonHours, availability);
        return ContinueWindow(index.TokenWindow(origin, contextHours, availability), origin, contextHours, horizonHours, availability);
    }

    private static void Validate(double contextHours, double horizonHours, ForecastReplayAvailability availability)
    {
        if (!double.IsFinite(contextHours) || contextHours <= 0 || contextHours > 168 ||
            !double.IsFinite(horizonHours) || horizonHours <= 0 || horizonHours > 168 || !Enum.IsDefined(availability))
            throw new ArgumentOutOfRangeException(nameof(contextHours));
    }

    private static WorkloadContinuationResult ContinueWindow(CodexPredictiveTokenEvent[] rows, DateTimeOffset origin,
        double contextHours, double horizonHours, ForecastReplayAvailability availability)
    {
        decimal? recorded = rows.Length > 0 && rows.All(x => x.ReportedTotalTokens >= 0) ? rows.Sum(x => (decimal)x.ReportedTotalTokens) : null;
        double? amount = recorded is { } total ? (double)total * horizonHours / contextHours : null;
        DateTimeOffset? latest = rows.Length > 0 ? rows.Max(x => x.ObservedAtUtc) : null;
        DateTimeOffset? positive = rows.Where(x => x.ReportedTotalTokens > 0).Select(x => (DateTimeOffset?)x.ObservedAtUtc).Max();
        var receipt = new WorkloadContinuationReceipt(ContinuationVersion, origin.AddHours(-contextHours), origin,
            contextHours, horizonHours, null, null, latest, positive, rows.Length, recorded, amount, availability, true,
            rows.Length == 0 ? "missing-context" : "incomplete-categories", ContinuationMethodology);
        if (rows.Length == 0 || rows.Any(x => !QuotaEvaluationCoverageBuilder.HasCompleteCategories(x))) return new(null, receipt);
        CodexJointWorkload observed = CodexJointWorkloadBuilder.Build(rows);
        CodexJointWorkload? projected = observed.ReportedTokens == 0
            ? new(CodexJointWorkload.Version, CodexJointQuantityKind.ConditionalProjection, observed.Cells, observed.Overflow, 0, 0)
            : CodexJointWorkloadBuilder.Project(observed, amount!.Value);
        if (projected is null) return new(null, receipt with { Status = "projection-unavailable" });
        receipt = receipt with { Status = "conditional-continuation" };
        double[] categories = Enumerable.Range(0, 5).Select(c => (double)(projected.Cells.Sum(x => TtJointWorkloadBasis.Amount(x, (TtTokenCategory)c)) +
            (projected.Overflow is { } overflow ? TtJointWorkloadBasis.Amount(overflow, (TtTokenCategory)c) : 0))).ToArray();
        IReadOnlyDictionary<string, double> Shares(Func<CodexPredictiveTokenEvent, string?> field) => rows
            .Where(x => !string.IsNullOrWhiteSpace(field(x))).GroupBy(x => field(x)!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => recorded > 0 ? (double)(x.Sum(r => (decimal)r.ReportedTotalTokens) / recorded.Value) : 0);
        return new(new PredictedWorkload(origin, horizonHours, amount!.Value, categories, Shares(x => x.Model), Shares(x => x.ReasoningEffort),
            rows.Length, latest!.Value, availability, "Continue the observed joint mixture and elapsed-time intensity; conditional on this context continuing.")
            { JointWorkload = projected, Continuation = receipt }, receipt);
    }

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
            "Observed joint model/effort/category cells continue together, including unknown and vocabulary-overflow mass; marginal shares never invent their pairing. " +
            "No future metadata or task completions. Incomplete category evidence withholds composition; this is a prediction, not rewritten accounting.")
        { JointWorkload = CodexJointWorkloadBuilder.Project(CodexJointWorkloadBuilder.Build(rows), prediction.ExpectedTokens) };
    }
}
