using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record WorkloadContinuationContextScore(QuotaHistoryCohort Cohort, double HorizonHours, double ContextHours,
    ForecastReplayAvailability Availability, int TargetIntervals, int AvailableContexts, int MatchedTargets, int WithheldTargets,
    int QuietOrOldPositiveContexts, int PositiveCompositionTargets, double? TokenMae, double? TokenWape,
    double SignedAccumulatedTokenError, double? MeanJointShareL1, ChronologicalEvidence.Support EvidenceSupport);

public sealed record WorkloadContinuationComparison(string Version, string Methodology, IReadOnlyList<WorkloadContinuationContextScore> Scores);

/// <summary>Small fixed context comparison. No live selection or lookback default change.</summary>
public static class WorkloadContinuationEvaluation
{
    public const string Version = "continuation-context-evaluation/v1";
    public static IReadOnlyList<double> ContextHours { get; } = Array.AsReadOnly(new[] { .5, 2d });
    public const string Methodology = "Compare existing 30-minute and two-hour elapsed contexts on matched later local-work targets, separately by compatible meter cohort and 5/15/30/120-minute horizon. " +
        "First-20 prefix remains reserved for later conversion comparison; this continuation itself fits no parameters. " +
        "Report availability separately and score only origins with both contexts; fewer recorded events is not established idleness. " +
        "CollectedByOrigin requires origin and native token-cost evidence availability plus outcome collection within five minutes; reconstruction is separate. " +
        "Volume MAE/WAPE and signed accumulated error use recorded native tokens; zero targets mean no recorded local work, not zero account activity. " +
        "Joint share L1 uses only positive actual/projected totals and complete category evidence, preserving model/effort/category coordinates. " +
        "Intervals across horizons/cohorts overlap; never pool them as independent outcomes. No quota conversion, calibrated interval or default selection follows.";

    public static WorkloadContinuationComparison Evaluate(CodexForecastDataset data, ForecastReplayAvailability availability,
        CancellationToken token = default)
    {
        var observations = QuotaCostObservationBuilder.BuildDetailed(data, token, horizons: ComposedQuotaEvaluator.EvaluationHorizons).Observations;
        var index = new CodexForecastFeatureIndex(data);
        var scores = new List<WorkloadContinuationContextScore>();
        foreach (var group in observations.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            var targets = group.OrderBy(x => x.StartUtc).Skip(20).ToArray();
            var pairs = new List<(QuotaCostObservation Row, WorkloadContinuationResult[] Results)>();
            int withheld = 0;
            foreach (var row in targets)
            {
                token.ThrowIfCancellationRequested();
                bool timely = availability == ForecastReplayAvailability.ReconstructedEventTime ||
                    row.OriginCollectedAtUtc <= row.StartUtc && row.OriginEvidenceAvailableAtUtc <= row.StartUtc &&
                    row.TokenCostEvidenceAvailableAtUtc <= row.EndUtc.AddMinutes(5) && row.OutcomeCollectedAtUtc <= row.EndUtc.AddMinutes(5);
                if (!timely || !QuotaCostIdentification.Valid(row)) { withheld++; continue; }
                pairs.Add((row, ContextHours.Select(context => WorkloadCompositionPrediction.Continue(index, row.StartUtc, context,
                    (row.EndUtc - row.StartUtc).TotalHours, availability)).ToArray()));
            }
            var matched = pairs.Where(x => x.Results.All(r => r.Workload is not null)).ToArray();
            for (int i = 0; i < ContextHours.Count; i++)
            {
                double[] errors = matched.Select(x => x.Results[i].Workload!.ExpectedTokens - x.Row.Features.Tokens).ToArray();
                double denominator = matched.Sum(x => (double)x.Row.Features.Tokens);
                double[] composition = matched.Where(x => x.Row.Features.Tokens > 0 && x.Results[i].Workload!.ExpectedTokens > 0)
                    .Select(x => ShareDistance(x.Row.Features.JointWorkload!, x.Results[i].Workload!.JointWorkload!)).ToArray();
                scores.Add(new(group.Key.Cohort, group.Key.HorizonHours, ContextHours[i], availability, targets.Length,
                    pairs.Count(x => x.Results[i].Workload is not null), matched.Length, withheld,
                    matched.Count(x => x.Results[i].Receipt.LatestPositiveWorkUtc is null || x.Results[i].Receipt.LatestPositiveWorkUtc < x.Row.StartUtc.AddMinutes(-10)),
                    composition.Length, errors.Length == 0 ? null : errors.Average(Math.Abs), denominator > 0 ? errors.Sum(Math.Abs) / denominator : null,
                    errors.Sum(), composition.Length == 0 ? null : composition.Average(),
                    ChronologicalEvidence.Describe(matched, x => x.Row.StartUtc, x => x.Row.EndUtc, x => x.Row.ResetUtc, x => x.Results[i].Workload!.ExpectedTokens - x.Row.Features.Tokens)));
            }
        }
        return new(Version, Methodology, scores);
    }

    private static double ShareDistance(CodexJointWorkload actual, CodexJointWorkload projected)
    {
        Dictionary<(CodexWorkloadCoordinate?, int), decimal> Shares(CodexJointWorkload x) =>
            x.Cells.Concat(x.Overflow is { } overflow ? [overflow] : []).SelectMany(cell => Enumerable.Range(0, 5)
                .Select(c => (Key: (cell.Coordinate, c), Value: TtJointWorkloadBasis.Amount(cell, (TtTokenCategory)c) / x.ReportedTokens)))
                .ToDictionary(x => x.Key, x => x.Value);
        var a = Shares(actual); var p = Shares(projected);
        return (double)a.Keys.Union(p.Keys).Sum(k => Math.Abs(a.GetValueOrDefault(k) - p.GetValueOrDefault(k)));
    }
}
