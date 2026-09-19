using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Exact-origin retrospective test of the hurdle-workload to quota-cost chain.</summary>
public static class SessionQuotaEvaluator
{
    public const string Version = "session-quota-evaluation/v4";
    public const string Methodology = "Research only, reconstructed event time; not deployment validation. " +
        "Separate known-account/source/plan/bucket/horizon cohorts, frozen first-20-interval total-token cost model. " +
        "Local recorded work is co-observed with account quota, not proven complete account attribution. " +
        "Session workload is reconstructed at each exact origin from earlier events and matured targets only. " +
        "Conditional quota assumes some positive recorded work; expected quota includes the activity estimate even when its live gate is unmet. " +
        "30/60-minute target buckets allow the existing five-minute meter tolerance; workload is fitted for each actual elapsed horizon, never scaled from a different duration. " +
        "Missing, quiet and sparse origins are withheld, not zero-filled. " +
        "Conditional MAE uses positive-work outcomes; unconditional errors and pace use all matched outcomes. " +
        "The existing quota policy is compared only on identical origin/outcome pairs in the same cohort; missing baseline predictions remain absent. " +
        "Joint expected-quota bands use maximum absolute errors from eight earlier completed reset generations; " +
        "80% empirical target, reported-value coverage, not latent coverage or exhaustion probability. " +
        "No multiplication of range endpoints, TT scale, native-credit conversion or automatic live promotion.";

    public static SessionQuotaEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default)
    {
        var rows = QuotaCostObservationBuilder.Build(data, cancellationToken, horizons: [.5, 1]);
        var predictions = new Dictionary<(DateTimeOffset, double), SessionWorkloadReport>();
        var scores = new List<SessionQuotaScore>();
        foreach (var group in rows.Where(x => x.Cohort.AccountKey is not null)
                     .GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            var ordered = group.OrderBy(x => x.StartUtc).ToArray();
            var training = ordered.Take(20).ToArray();
            var trials = new List<SessionQuotaTrial>();
            var withheld = 0;
            var trainingIssue = training.Length < 20 ? "insufficient-training-intervals" :
                !QuotaCostEvaluation.HasTrainingWork(training) ? "no-recorded-training-work" : null;
            if (trainingIssue is not null) withheld = ordered.Skip(20).Count(x => x.StartUtc >= training[^1].EndUtc);
            if (trainingIssue is null)
            {
                var quota = QuotaHistoryPolicy.ReplayRows(QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(
                    data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == group.Key.Cohort), data.CapturedAtUtc)).SelectMany(x => x));
                var incumbent = QuotaPredictionService.ReplayAtTargets(data with { Quota = quota }, group.Key.HorizonHours,
                    ForecastReplayAvailability.ReconstructedEventTime,
                    ordered.Skip(20).Select(x => (x.StartUtc, x.EndUtc)).ToArray(), cancellationToken);
                var fit = QuotaCostEvaluation.FitFrozen(training, "total", cancellationToken);
                foreach (var row in ordered.Skip(20).Where(x => x.StartUtc >= training[^1].EndUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var hours = (row.EndUtc - row.StartUtc).TotalHours;
                    var key = (row.StartUtc, hours);
                    if (!predictions.TryGetValue(key, out var session))
                    {
                        session = SessionWorkloadPredictionService.ReconstructAtOrigin(data, row.StartUtc, hours, cancellationToken);
                        predictions.Add(key, session);
                    }
                    var prediction = session.Current.SingleOrDefault();
                    if (prediction?.ConditionalMeanTokens is not { } tokens) { withheld++; continue; }
                    // The existing frozen total-token model is linear, nonnegative and has no intercept.
                    var conditional = fit(row with { Features = row.Features with { Tokens = (long)Math.Clamp(tokens, 0, long.MaxValue) } });
                    var expected = conditional * prediction.RecordedActivityProbability;
                    var errors = QuotaResetGenerationPolicy.Group(trials.Where(x =>
                            x.EndUtc <= row.StartUtc && x.ResetUtc < row.StartUtc - QuotaResetGenerationPolicy.Tolerance), x => x.ResetUtc)
                        .Select(g => g.Max(x => x.ExpectedError)).ToArray();
                    var radius = QuotaForecastBacktester.ErrorRadius(errors);
                    double? incumbentUsage = incumbent.TryGetValue(row.StartUtc, out var baseline) && baseline.TargetUtc == row.EndUtc
                        ? 100 - row.StartUsed - baseline.RemainingPercent : null;
                    trials.Add(new(row.StartUtc, row.EndUtc, row.ResetUtc, row.Features.Tokens > 0,
                        prediction.RecordedActivityProbability, conditional, expected, row.ObservedDelta,
                        Math.Abs(conditional - row.ObservedDelta), Math.Abs(expected - row.ObservedDelta),
                        Math.Abs(row.PaceDelta - row.ObservedDelta), row.IntervalLoss(expected), row.IntervalLoss(row.PaceDelta),
                        radius is { } low ? Math.Max(0, expected - low) : null,
                        radius is { } high ? expected + high : null)
                    {
                        IncumbentError = incumbentUsage is { } usage ? Math.Abs(usage - row.ObservedDelta) : null,
                        IncumbentIntervalLoss = incumbentUsage is { } delta ? row.IntervalLoss(delta) : null
                    });
                }
            }
            var active = trials.Where(x => x.RecordedActivity).ToArray();
            var bands = trials.Where(x => x.LowerExpectedQuota is not null).ToArray();
            var paired = trials.Where(x => x.IncumbentError is not null).ToArray();
            scores.Add(new(group.Key.Cohort, group.Key.HorizonHours, training.Length, withheld, trials,
                QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count, active.Length,
                Mean(active.Select(x => x.ConditionalError)), Mean(active.Select(x => x.PaceError)),
                Mean(trials.Select(x => x.ExpectedError)), Mean(trials.Select(x => x.PaceError)),
                Mean(trials.Select(x => x.ExpectedIntervalLoss)), Mean(trials.Select(x => x.PaceIntervalLoss)), bands.Length,
                Mean(bands.Select(x => x.ObservedQuota >= x.LowerExpectedQuota && x.ObservedQuota <= x.UpperExpectedQuota ? 1d : 0d)),
                Mean(bands.Select(x => x.UpperExpectedQuota!.Value - x.LowerExpectedQuota!.Value)))
            {
                TrainingIssue = trainingIssue,
                IncumbentPairedOrigins = paired.Length,
                PairedExpectedMae = Mean(paired.Select(x => x.ExpectedError)),
                IncumbentMae = Mean(paired.Select(x => x.IncumbentError!.Value)),
                PairedExpectedIntervalLoss = Mean(paired.Select(x => x.ExpectedIntervalLoss)),
                IncumbentIntervalLoss = Mean(paired.Select(x => x.IncumbentIntervalLoss!.Value))
            });
        }
        return new(Version, Methodology, scores);
    }

    private static double? Mean(IEnumerable<double> values)
    {
        var rows = values.ToArray();
        return rows.Length == 0 ? null : rows.Average();
    }
}
