using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Actual-cost and origin-only forecast errors on identical disjoint outcomes.</summary>
public static class ComposedQuotaEvaluator
{
    public const string Version = "composed-quota/v6";

    public static ComposedQuotaEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime)
    {
        var rows = QuotaCostObservationBuilder.Build(data, cancellationToken);
        var predictions = new Dictionary<(DateTimeOffset, double), PredictedWorkload?>();
        var activities = new Dictionary<DateTimeOffset, string>();
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
                var withheldReasons = new Dictionary<string, int>();
                void Withheld(string reason) => withheldReasons[reason] = withheldReasons.GetValueOrDefault(reason) + 1;
                var needsCategories = candidate.Replace("asserted-", "", StringComparison.Ordinal) != "total";
                var completeTraining = !needsCategories || training.All(x => x.HasCompleteTokenCategories);
                if (!completeTraining) withheldReasons["incomplete-category-training"] = training.Count(x => !x.HasCompleteTokenCategories);
                if (nativeTraining.Length == 20 && completeTraining)
                {
                    var fit = QuotaCostEvaluation.FitFrozen(training, candidate.Replace("asserted-", "", StringComparison.Ordinal), cancellationToken);
                    foreach (var row in ordered.Skip(20).Where(x => x.StartUtc >= training[^1].EndUtc))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (needsCategories && !row.HasCompleteTokenCategories) { Withheld("incomplete-category-outcome"); continue; }
                        if (availability == ForecastReplayAvailability.CollectedByOrigin)
                        {
                            var unavailable = false;
                            void Check(bool failed, string reason)
                            {
                                if (!failed) return;
                                unavailable = true;
                                Withheld(reason);
                            }
                            // All supported composed cost candidates use only token amounts/model/effort.
                            // Oracle context/activity/runtime ablations are not composed candidates.
                            Check(training.Any(x => x.TokenCostEvidenceAvailableAtUtc is null), "training-collection-unknown");
                            Check(training.Any(x => x.TokenCostEvidenceAvailableAtUtc > row.StartUtc), "training-collected-after-origin");
                            Check(row.OriginCollectedAtUtc is null, "origin-meter-collection-unknown");
                            Check(row.OriginCollectedAtUtc > row.StartUtc, "origin-meter-collected-after-origin");
                            Check(row.OriginEvidenceAvailableAtUtc is null, "origin-prefix-collection-unknown");
                            Check(row.OriginEvidenceAvailableAtUtc > row.StartUtc, "origin-prefix-collected-after-origin");
                            Check(row.OutcomeCollectedAtUtc is null, "outcome-meter-collection-unknown");
                            Check(row.OutcomeCollectedAtUtc > row.EndUtc.AddMinutes(5), "outcome-meter-collected-too-late");
                            if (unavailable) { missing++; continue; }
                        }
                        // Use the actual elapsed target horizon, never the target's workload.
                        var hours = (row.EndUtc - row.StartUtc).TotalHours;
                        var key = (row.StartUtc, hours);
                        if (!predictions.TryGetValue(key, out var workload))
                        {
                            workload = TokenWorkloadPredictionService.PredictHorizon(data, row.StartUtc, hours,
                                availability, cancellationToken)?.Composition;
                            predictions.Add(key, workload);
                        }
                        if (workload is null) { missing++; Withheld("missing-or-stale-composition"); continue; }
                        var projected = Project(row, workload);
                        var predicted = fit(projected);
                        var actualCost = fit(row);
                        if (!activities.TryGetValue(row.StartUtc, out var activity))
                        {
                            activity = CodexNowcastActivity.Evaluate(data, row.StartUtc, availability).State.ToString();
                            activities.Add(row.StartUtc, activity);
                        }
                        var calibration = ComposedQuotaPolicy.CalibrateUncertainty(trials, row.StartUtc);
                        var remaining = Math.Clamp(100 - row.StartUsed - predicted, 0, 100 - row.StartUsed);
                        trials.Add(new(row.StartUtc, row.EndUtc, row.ResetUtc, predicted, actualCost, row.ObservedDelta,
                            row.IntervalLoss(predicted), row.IntervalLoss(actualCost), row.IntervalLoss(row.PaceDelta),
                            Math.Clamp(100 - row.StartUsed - predicted, 0, 100), workload)
                        {
                            Availability = availability,
                            OriginActivity = activity,
                            RecordedOutcomeTokens = row.Features.Tokens,
                            ZeroUseIntervalLoss = row.IntervalLoss(0),
                            CalibrationAvailableAtUtc = availability == ForecastReplayAvailability.CollectedByOrigin
                                ? row.OutcomeCollectedAtUtc : row.EndUtc,
                            ObservedRemainingPercent = 100 - row.EndUsed,
                            LowerRemainingPercent = calibration.Radius is { } low ? Math.Max(0, remaining - low) : null,
                            UpperRemainingPercent = calibration.Radius is { } high ? Math.Min(100 - row.StartUsed, remaining + high) : null,
                            CalibrationGenerations = calibration.Generations,
                            IncumbentIntervalLoss = incumbent.TryGetValue(row.StartUtc, out var baseline) &&
                                baseline.Observation.OutcomeUtc == row.EndUtc
                                ? row.IntervalLoss(100 - row.StartUsed - baseline.Prediction.RemainingPercent) : null
                        });
                    }
                }
                var bands = trials.Where(x => x.LowerRemainingPercent is not null && x.UpperRemainingPercent is not null).ToArray();
                scores.Add(new(group.Key.Cohort, group.Key.HorizonHours, candidate, training.Length, trials.Count,
                    QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count, missing,
                    Mean(trials.Select(x => x.IntervalLoss)), Mean(trials.Select(x => x.CostOnlyIntervalLoss)),
                    Mean(trials.Select(x => x.PaceIntervalLoss)), Mean(trials.Select(x => Math.Abs(x.PredictedDelta - x.ObservedDelta))), trials)
                {
                    Availability = availability,
                    Breakdowns = ComposedQuotaBreakdowns.Build(trials),
                    IntervalOrigins = bands.Length,
                    IntervalCoverage = Mean(bands.Select(x => x.ObservedRemainingPercent >= x.LowerRemainingPercent &&
                        x.ObservedRemainingPercent <= x.UpperRemainingPercent ? 1d : 0d)),
                    MeanIntervalWidth = Mean(bands.Select(x => x.UpperRemainingPercent!.Value - x.LowerRemainingPercent!.Value)),
                    AssertedTrainingIntervals = training.Count(x => x.Attribution == QuotaAccountAttribution.UserAsserted),
                    WithheldReasons = withheldReasons,
                    IncumbentIntervalLoss = trials.All(x => x.IncumbentIntervalLoss is not null)
                        ? Mean(trials.Select(x => x.IncumbentIntervalLoss!.Value)) : null
                });
            }
        }
        return new(Version, $"{availability}: native token forecast at each exact origin, then preceding-two-hour " +
            "composition, then frozen first-20-interval cost weights. Forecast, actual-workload cost, and pace losses use identical held-out targets. " +
            "CollectedByOrigin requires frozen token-cost inputs (quota labels, token amounts/model/effort and any ownership assertions) collected before the origin, the origin meter already collected, " +
            "and an outcome meter collected within five minutes of its event; token prediction uses origin-time availability. " +
            "Unused activity/context/tier backfills do not change token-cost training availability. Withheld reason counts can overlap. " +
            "Backfilled history alone cannot qualify. No future workload/model/context features enter predictions. " +
            "Missing/stale composition is withheld, not zero. Joint workload/cost ranges use the live policy's completed-reset maximum " +
            "absolute displayed-delta errors, with eight earlier generations, an empirical 80% target and a one-point minimum radius. " +
            "Calibration labels must already be available at the origin (event time for reconstruction, actual collection for strict replay). " +
            "Coverage measures reported remaining quota, not latent true usage or exhaustion probability. Sparse ranges remain absent. " +
            "No activity-conditioned quota distribution or live promotion is implied. " + ComposedQuotaBreakdowns.Methodology, scores);
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
