using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class QuotaForecastEvaluation
{
    public static ForecastEvaluationReport Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        bool includeTokenEvaluation = true)
    {
        var scores = new List<ForecastEvaluationScore>();
        var history = QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc);
        foreach (var stream in QuotaHistoryPolicy.Streams(history))
        {
            var rows = QuotaHistoryPolicy.ReplayRows(stream);
            var local = data with { Quota = rows };
            var strictRows = QuotaHistoryPolicy.ReplayRows(QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(
                QuotaHistoryPolicy.AvailableRows(data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == stream.Key),
                    ForecastReplayAvailability.CollectedByOrigin), data.CapturedAtUtc)).SelectMany(x => x));
            foreach (var horizon in new double?[] { 0.5, 2, 24, null })
            {
                if (horizon == 24 && stream.Key.Kind == QuotaWindowKind.FiveHour) continue;
                var target = horizon is null ? "near-reset-proxy" : FormattableString.Invariant($"{horizon:g}h");
                foreach (var model in QuotaPaceModels.Candidates.Append("adaptive"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var trials = model == "adaptive" ? QuotaForecastBacktester.ReplayAdaptive(rows, horizon, cancellationToken)
                        : QuotaForecastBacktester.Replay(rows, model, horizon, cancellationToken);
                    scores.Add(Score(stream.Key.Kind, stream.Key.Source, model, target, "ReconstructedEventTime", trials) with { AccountKey = stream.Key.AccountKey, HistoryCohort = stream.Key });
                }
                foreach (var availability in Enum.GetValues<ForecastReplayAvailability>())
                {
                    var available = local with { Quota = availability == ForecastReplayAvailability.CollectedByOrigin ? strictRows : rows };
                    if (horizon is { } hours)
                    {
                        var adaptive = QuotaPredictionService.Replay(available, hours, availability, cancellationToken);
                        var points = adaptive.Select(x => x.Observation with
                        {
                            PredictedRemaining = x.Prediction.RemainingPercent,
                            LowerRemaining = x.Prediction.LowerRemainingPercent,
                            UpperRemaining = x.Prediction.UpperRemainingPercent,
                            ObservedExhaustion = null, ExhaustionEtaBracketErrorHours = null
                        }).ToArray();
                        scores.Add(Score(stream.Key.Kind, stream.Key.Source, QuotaPredictionService.PolicyVersion,
                            target, availability.ToString(), points, adaptive.Count(x => x.Prediction.UsesWorkload))
                            with { AccountKey = stream.Key.AccountKey, HistoryCohort = stream.Key });
                    }
                    foreach (var candidate in QuotaWorkloadBacktester.Candidates)
                    {
                        var trials = QuotaWorkloadBacktester.Replay(available, stream.Key.Kind, candidate, horizon, availability, cancellationToken);
                        // The advanced models produce a point residual correction, not a calibrated
                        // class probability/ETA. Do not inherit baseline risk/interval claims.
                        var points = trials.Select(x => x.Baseline with
                        {
                            PredictedRemaining = x.PredictedRemaining, LowerRemaining = null, UpperRemaining = null,
                            ObservedExhaustion = null, ExhaustionEtaBracketErrorHours = null
                        }).ToArray();
                        scores.Add(Score(stream.Key.Kind, stream.Key.Source, candidate, target, availability.ToString(), points,
                            trials.Count(x => x.UsedWorkloadModel)) with { AccountKey = stream.Key.AccountKey, HistoryCohort = stream.Key });
                    }
                }
            }
        }
        return new ForecastEvaluationReport(data.CapturedAtUtc, data.Quota.Count(x => x.Authority == QuotaObservationAuthority.ProviderAuthoritative),
            data.Workload.Count, data.Tokens.Count, data.Tokens.Count(x => x.ReasoningEffort is not null),
            data.Tokens.Count(x => x.CapturedAtUtc is not null), scores,
            "Historical quota targets use separate source/account/bucket/plan cohorts; unknown-account rollout sessions are never assigned to the current account. Potentially cached rollout repeats are withheld, not zero-burn targets. App-server integer rounding and rollout numeric representation are retained, not forced into equality. Event-time replay reconstructs history; collection-time replay cannot use backfilled quota until actually collected. Origins are at least 30 minutes apart after 15 minutes of history; outcomes must be within 5 minutes of the requested horizon. Near-reset targets are proxies and missing survival labels stay unknown. Bands target 80% from earlier compatible errors, not calibrated exhaustion probabilities. Workload is local co-observation, not account attribution. " + data.Coverage)
        {
            TokenScores = includeTokenEvaluation ? TokenWorkloadPredictionService.EvaluateScores(data, cancellationToken) : [],
            QuotaHistorySummary = QuotaHistoryPolicy.Summarize(history),
            HistoricalQuotaObservations = history.Count(x => x.Eligible),
            QuotaCost = QuotaCostEvaluation.Evaluate(data, cancellationToken),
            ComposedQuota = ComposedQuotaEvaluator.Evaluate(data, cancellationToken),
            ComposedQuotaStrict = ComposedQuotaEvaluator.Evaluate(data, cancellationToken, ForecastReplayAvailability.CollectedByOrigin),
            QuotaTransfer = QuotaTransferEvaluator.Evaluate(data, cancellationToken)
        };
    }

    private static ForecastEvaluationScore Score(QuotaWindowKind kind, string source, string model, string target,
        string availability, IReadOnlyList<QuotaForecastTrial> trials, int fitted = 0)
    {
        double? Mean(IEnumerable<double> values)
        {
            var array = values.ToArray();
            return array.Length > 0 ? array.Average() : null;
        }
        var bands = trials.Where(x => x.LowerRemaining is not null && x.UpperRemaining is not null).ToArray();
        var labels = trials.Where(x => x.ObservedExhaustion is not null).ToArray();
        var etas = trials.Where(x => x.ExhaustionEtaBracketErrorHours is not null).ToArray();
        var mse = Mean(trials.Select(x => Math.Pow(x.PredictedRemaining - x.ObservedRemaining, 2)));
        var independent = 0;
        DateTimeOffset? previousEnd = null;
        foreach (var trial in trials.OrderBy(x => x.OriginUtc))
        {
            if (previousEnd is not null && trial.OriginUtc < previousEnd) continue;
            independent++;
            previousEnd = trial.OutcomeUtc;
        }
        return new ForecastEvaluationScore(kind, source, model, target, availability, trials.Count,
            QuotaForecastBacktester.ResetGenerations(trials).Count, Mean(trials.Select(x => Math.Abs(x.PredictedRemaining - x.ObservedRemaining))),
            mse is double value ? Math.Sqrt(value) : null, bands.Length,
            Mean(bands.Select(x => x.ObservedRemaining >= x.LowerRemaining && x.ObservedRemaining <= x.UpperRemaining ? 1d : 0d)),
            Mean(bands.Select(x => x.UpperRemaining!.Value - x.LowerRemaining!.Value)), labels.Length,
            labels.Count(x => x.ObservedExhaustion == true), Mean(labels.Select(x => x.ObservedExhaustion == x.PredictedExhaustion ? 1d : 0d)),
            etas.Length, Mean(etas.Select(x => x.ExhaustionEtaBracketErrorHours!.Value)), fitted)
        { NonOverlappingOrigins = independent };
    }
}
