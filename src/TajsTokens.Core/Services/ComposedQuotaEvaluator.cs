using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Actual-cost and origin-only forecast errors on identical disjoint outcomes.</summary>
public static class ComposedQuotaEvaluator
{
    public const string Version = "composed-quota/v1";

    public static ComposedQuotaEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default)
    {
        var rows = QuotaCostObservationBuilder.Build(data, cancellationToken);
        var predictions = new Dictionary<(DateTimeOffset, double), PredictedWorkload?>();
        var scores = new List<ComposedQuotaScore>();
        foreach (var group in rows.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            var ordered = group.OrderBy(x => x.StartUtc).ToArray();
            var training = ordered.Take(20).ToArray();
            var quota = QuotaHistoryPolicy.ReplayRows(QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(
                data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == group.Key.Cohort), data.CapturedAtUtc)).SelectMany(x => x));
            var incumbent = training.Length == 20 ? QuotaPredictionService.Replay(data with { Quota = quota },
                    group.Key.HorizonHours, ForecastReplayAvailability.ReconstructedEventTime, cancellationToken)
                .ToDictionary(x => x.Observation.OriginUtc) : [];
            foreach (var candidate in new[] { "total", "categories", "model-effort" })
            {
                var trials = new List<ComposedQuotaTrial>();
                var missing = 0;
                if (training.Length == 20)
                {
                    var fit = QuotaCostEvaluation.FitFrozen(training, candidate, cancellationToken);
                    foreach (var row in ordered.Skip(20).Where(x => x.StartUtc >= training[^1].EndUtc))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Use the actual elapsed target horizon, never the target's workload.
                        var hours = (row.EndUtc - row.StartUtc).TotalHours;
                        var key = (row.StartUtc, hours);
                        if (!predictions.TryGetValue(key, out var workload))
                        {
                            workload = TokenWorkloadPredictionService.PredictHorizon(data, row.StartUtc, hours,
                                cancellationToken: cancellationToken)?.Composition;
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
                    IncumbentIntervalLoss = trials.All(x => x.IncumbentIntervalLoss is not null)
                        ? Mean(trials.Select(x => x.IncumbentIntervalLoss!.Value)) : null
                });
            }
        }
        return new(Version, "Retrospective event-time replay: native token forecast at each exact origin, then preceding-two-hour " +
            "composition, then frozen first-20-interval cost weights. Forecast, actual-workload cost, and pace losses use identical held-out targets. " +
            "Backfilled history is not a claim of historical collection availability. No future workload/model/context features enter predictions. " +
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
