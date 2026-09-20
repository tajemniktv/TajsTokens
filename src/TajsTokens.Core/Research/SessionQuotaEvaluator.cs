// Taj's Tokens | SessionQuotaEvaluator.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Research;

/// <summary>Exact-origin retrospective test of the hurdle-workload to quota-cost chain.</summary>
public static class SessionQuotaEvaluator
{
    public const string Version = "session-quota-evaluation/v5-blocks";

    public const string Methodology = "Research only, reconstructed event time; not deployment validation. " +
                                      "Separate known-account/source/plan/bucket/horizon cohorts, frozen first-20-interval total-token cost model. " +
                                      "Local recorded work is co-observed with account quota, not proven complete account attribution. " +
                                      "Session workload is reconstructed at each exact origin from earlier events and matured targets only. " +
                                      "Conditional quota assumes some positive recorded work; expected quota includes the activity estimate even when its live gate is unmet. " +
                                      "30/60-minute target buckets allow the existing five-minute meter tolerance; workload is fitted for each actual elapsed horizon, never scaled from a different duration. " +
                                      "Missing, quiet and sparse origins are withheld, not zero-filled. " +
                                      "Conditional MAE uses positive-work outcomes; unconditional errors and pace use all matched outcomes. " +
                                      "The existing quota policy is compared only on identical origin/outcome pairs in the same cohort; missing baseline predictions remain absent. " +
                                      "Joint expected-quota bands use maximum absolute errors from eight earlier completed chronological blocks; " +
                                      "80% empirical target, reported-value coverage, not latent coverage or exhaustion probability. " +
                                      "No multiplication of range endpoints, TT scale, native-credit conversion or automatic live promotion.";

    public static SessionQuotaEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<QuotaCostObservation> rows = QuotaCostObservationBuilder.Build(data, cancellationToken, horizons: [.5, 1]);
        var predictions = new Dictionary<(DateTimeOffset, double), SessionWorkloadReport>();
        var scores = new List<SessionQuotaScore>();
        foreach (IGrouping<(QuotaHistoryCohort Cohort, double HorizonHours), QuotaCostObservation> group in rows
                     .Where(x => x.Cohort.AccountKey is not null)
                     .GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            QuotaCostObservation[] ordered = group.OrderBy(x => x.StartUtc).ToArray();
            QuotaCostObservation[] training = ordered.Take(20).ToArray();
            var trials = new List<SessionQuotaTrial>();
            int withheld = 0;
            string? trainingIssue = training.Length < 20 ? "insufficient-training-intervals" :
                !QuotaAccountingModel.HasTrainingWork(training) ? "no-recorded-training-work" : null;
            if (trainingIssue is not null) withheld = ordered.Skip(20).Count(x => x.StartUtc >= training[^1].EndUtc);
            if (trainingIssue is null)
            {
                QuotaSnapshot[] quota = QuotaHistoryPolicy.ReplayRows(
                    QuotaHistoryPolicy.Streams(
                        QuotaHistoryPolicy.Describe(
                            data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == group.Key.Cohort),
                            data.CapturedAtUtc)).SelectMany(x => x));
                IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> incumbent = QuotaPredictionService.ReplayAtTargets(
                    data with { Quota = quota },
                    group.Key.HorizonHours,
                    ForecastReplayAvailability.ReconstructedEventTime,
                    ordered.Skip(20).Select(x => (x.StartUtc, x.EndUtc)).ToArray(),
                    cancellationToken);
                Func<QuotaCostObservation, double> fit = QuotaAccountingModel.FitFrozen(training, "total", cancellationToken);
                foreach (QuotaCostObservation row in ordered.Skip(20).Where(x => x.StartUtc >= training[^1].EndUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double hours = (row.EndUtc - row.StartUtc).TotalHours;
                    (DateTimeOffset StartUtc, double hours) key = (row.StartUtc, hours);
                    if (!predictions.TryGetValue(key, out SessionWorkloadReport? session))
                    {
                        session = SessionWorkloadPredictionService.ReconstructAtOrigin(data, row.StartUtc, hours, cancellationToken);
                        predictions.Add(key, session);
                    }
                    SessionWorkloadPrediction? prediction = session.Current.SingleOrDefault();
                    if (prediction?.ConditionalMeanTokens is not { } tokens)
                    {
                        withheld++;
                        continue;
                    }
                    // The existing frozen total-token model is linear, nonnegative and has no intercept.
                    double conditional = fit(
                        row with { Features = row.Features with { Tokens = (long)Math.Clamp(tokens, 0, long.MaxValue) } });
                    double expected = conditional * prediction.RecordedActivityProbability;
                    double[] errors = ChronologicalEvidence.Blocks(
                            trials.Where(x => x.EndUtc <= row.StartUtc),
                            x => x.OriginUtc,
                            x => x.EndUtc,
                            x => x.ResetUtc)
                        .Select(g => g.Max(x => x.ExpectedError)).ToArray();
                    double? radius = QuotaForecastCalibration.ErrorRadius(errors);
                    double? incumbentUsage = incumbent.TryGetValue(row.StartUtc, out QuotaHorizonPrediction? baseline) &&
                                             baseline.TargetUtc == row.EndUtc
                        ? 100 - row.StartUsed - baseline.RemainingPercent
                        : null;
                    trials.Add(
                        new SessionQuotaTrial(
                            row.StartUtc,
                            row.EndUtc,
                            row.ResetUtc,
                            row.Features.Tokens > 0,
                            prediction.RecordedActivityProbability,
                            conditional,
                            expected,
                            row.ObservedDelta,
                            Math.Abs(conditional - row.ObservedDelta),
                            Math.Abs(expected - row.ObservedDelta),
                            Math.Abs(row.PaceDelta - row.ObservedDelta),
                            row.IntervalLoss(expected),
                            row.IntervalLoss(row.PaceDelta),
                            radius is { } low ? Math.Max(0, expected - low) : null,
                            radius is { } high ? expected + high : null)
                        {
                            IncumbentError = incumbentUsage is { } usage ? Math.Abs(usage - row.ObservedDelta) : null,
                            IncumbentIntervalLoss = incumbentUsage is { } delta ? row.IntervalLoss(delta) : null,
                        });
                }
            }
            SessionQuotaTrial[] active = trials.Where(x => x.RecordedActivity).ToArray();
            SessionQuotaTrial[] bands = trials.Where(x => x.LowerExpectedQuota is not null).ToArray();
            SessionQuotaTrial[] paired = trials.Where(x => x.IncumbentError is not null).ToArray();
            scores.Add(
                new SessionQuotaScore(
                    group.Key.Cohort,
                    group.Key.HorizonHours,
                    training.Length,
                    withheld,
                    trials,
                    QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count,
                    active.Length,
                    Mean(active.Select(x => x.ConditionalError)),
                    Mean(active.Select(x => x.PaceError)),
                    Mean(trials.Select(x => x.ExpectedError)),
                    Mean(trials.Select(x => x.PaceError)),
                    Mean(trials.Select(x => x.ExpectedIntervalLoss)),
                    Mean(trials.Select(x => x.PaceIntervalLoss)),
                    bands.Length,
                    Mean(bands.Select(x => x.ObservedQuota >= x.LowerExpectedQuota && x.ObservedQuota <= x.UpperExpectedQuota ? 1d : 0d)),
                    Mean(bands.Select(x => x.UpperExpectedQuota!.Value - x.LowerExpectedQuota!.Value)))
                {
                    TrainingIssue = trainingIssue,
                    IncumbentPairedOrigins = paired.Length,
                    PairedExpectedMae = Mean(paired.Select(x => x.ExpectedError)),
                    IncumbentMae = Mean(paired.Select(x => x.IncumbentError!.Value)),
                    PairedExpectedIntervalLoss = Mean(paired.Select(x => x.ExpectedIntervalLoss)),
                    IncumbentIntervalLoss = Mean(paired.Select(x => x.IncumbentIntervalLoss!.Value)),
                });
        }
        return new SessionQuotaEvaluation(Version, Methodology, scores);
    }

    private static double? Mean(IEnumerable<double> values)
    {
        double[] rows = values.ToArray();
        return rows.Length == 0 ? null : rows.Average();
    }
}