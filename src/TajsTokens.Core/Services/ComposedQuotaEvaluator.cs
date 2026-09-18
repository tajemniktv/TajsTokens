using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Actual-cost and origin-only forecast errors on identical disjoint outcomes.</summary>
public static class ComposedQuotaEvaluator
{
    public const string Version = "composed-quota/v2";

    public static ComposedQuotaEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime)
    {
        var rows = QuotaCostObservationBuilder.Build(data, cancellationToken);
        var predictions = new Dictionary<(DateTimeOffset, double), PredictedWorkload?>();
        var scores = new List<ComposedQuotaScore>();
        foreach (var group in rows.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            var ordered = group.OrderBy(x => x.StartUtc).ToArray();
            var nativeTraining = ordered.Take(20).ToArray();
            var quota = QuotaHistoryPolicy.ReplayRows(QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(
                QuotaHistoryPolicy.AvailableRows(data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == group.Key.Cohort), availability), data.CapturedAtUtc)).SelectMany(x => x));
            var incumbent = nativeTraining.Length == 20 ? QuotaPredictionService.Replay(data with { Quota = quota },
                    group.Key.HorizonHours, availability, cancellationToken)
                .ToDictionary(x => x.Observation.OriginUtc) : [];
            var asserted = QuotaCostTrainingPolicy.Select(rows, group.Key.Cohort, group.Key.HorizonHours, true);
            var candidates = new[] { "total", "categories", "model-effort" };
            foreach (var candidate in asserted.Length > nativeTraining.Length ? candidates.Concat(candidates.Select(x => "asserted-" + x)) : candidates)
            {
                var training = candidate.StartsWith("asserted-", StringComparison.Ordinal) ? asserted : nativeTraining;
                var trials = new List<ComposedQuotaTrial>();
                var missing = 0;
                if (nativeTraining.Length == 20)
                {
                    var fit = QuotaCostEvaluation.FitFrozen(training, candidate.Replace("asserted-", "", StringComparison.Ordinal), cancellationToken);
                    foreach (var row in ordered.Skip(20).Where(x => x.StartUtc >= training[^1].EndUtc))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (availability == ForecastReplayAvailability.CollectedByOrigin &&
                            (training.Any(x => x.EvidenceAvailableAtUtc is null || x.EvidenceAvailableAtUtc > row.StartUtc) ||
                             row.OriginCollectedAtUtc is null || row.OriginCollectedAtUtc > row.StartUtc ||
                             row.OriginEvidenceAvailableAtUtc is null || row.OriginEvidenceAvailableAtUtc > row.StartUtc ||
                             row.OutcomeCollectedAtUtc is null || row.OutcomeCollectedAtUtc > row.EndUtc.AddMinutes(5)))
                        { missing++; continue; }
                        // Use the actual elapsed target horizon, never the target's workload.
                        var hours = (row.EndUtc - row.StartUtc).TotalHours;
                        var key = (row.StartUtc, hours);
                        if (!predictions.TryGetValue(key, out var workload))
                        {
                            workload = TokenWorkloadPredictionService.PredictHorizon(data, row.StartUtc, hours,
                                availability, cancellationToken)?.Composition;
                            predictions.Add(key, workload);
                        }
                        if (workload is null) { missing++; continue; }
                        var projected = Project(row, workload);
                        var predicted = fit(projected);
                        var actualCost = fit(row);
                        trials.Add(new(row.StartUtc, row.EndUtc, row.ResetUtc, predicted, actualCost, row.ObservedDelta,
                            row.IntervalLoss(predicted), row.IntervalLoss(actualCost), row.IntervalLoss(row.PaceDelta),
                            Math.Clamp(100 - row.StartUsed - predicted, 0, 100), workload)
                        {
                            Availability = availability,
                            IncumbentIntervalLoss = incumbent.TryGetValue(row.StartUtc, out var baseline) &&
                                baseline.Observation.OutcomeUtc == row.EndUtc
                                ? row.IntervalLoss(100 - row.StartUsed - baseline.Prediction.RemainingPercent) : null
                        });
                    }
                }
                scores.Add(new(group.Key.Cohort, group.Key.HorizonHours, candidate, training.Length, trials.Count,
                    QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count, missing,
                    Mean(trials.Select(x => x.IntervalLoss)), Mean(trials.Select(x => x.CostOnlyIntervalLoss)),
                    Mean(trials.Select(x => x.PaceIntervalLoss)), Mean(trials.Select(x => Math.Abs(x.PredictedDelta - x.ObservedDelta))), trials)
                {
                    Availability = availability,
                    AssertedTrainingIntervals = training.Count(x => x.Attribution == QuotaAccountAttribution.UserAsserted),
                    IncumbentIntervalLoss = trials.All(x => x.IncumbentIntervalLoss is not null)
                        ? Mean(trials.Select(x => x.IncumbentIntervalLoss!.Value)) : null
                });
            }
        }
        return new(Version, $"{availability}: native token forecast at each exact origin, then preceding-two-hour " +
            "composition, then frozen first-20-interval cost weights. Forecast, actual-workload cost, and pace losses use identical held-out targets. " +
            "CollectedByOrigin requires all frozen cost inputs collected before the origin, the origin meter already collected, " +
            "and an outcome meter collected within five minutes of its event; token prediction uses origin-time availability. " +
            "Backfilled history alone cannot qualify. No future workload/model/context features enter predictions. " +
            "Missing/stale composition is withheld, not zero. No calibrated probability or live promotion is implied.", scores);
    }

    internal static QuotaCostObservation Project(QuotaCostObservation row, PredictedWorkload workload) => row with
    {
        TokenCategories = workload.TokenCategories,
        Features = row.Features with
        {
            Tokens = (long)Math.Clamp(workload.ExpectedTokens, 0, long.MaxValue),
            ModelTokenShares = workload.ModelShares, EffortTokenShares = workload.EffortShares
        }
    };

    private static double? Mean(IEnumerable<double> values)
    {
        var rows = values.ToArray();
        return rows.Length == 0 ? null : rows.Average();
    }
}
