using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class QuotaForecastEvaluation
{
    public static ForecastEvaluationReport Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default)
    {
        var scores = new List<ForecastEvaluationScore>();
        foreach (var stream in data.Quota.Where(x => x.Authority == QuotaObservationAuthority.ProviderAuthoritative)
                     .GroupBy(x => (x.Provider, x.Profile, x.Kind, x.Source)))
        {
            var rows = stream.ToArray();
            var local = data with { Quota = rows };
            foreach (var horizon in new double?[] { 0.5, 2, 24, null })
            {
                if (horizon == 24 && stream.Key.Kind == QuotaWindowKind.FiveHour) continue;
                var target = horizon is null ? "near-reset-proxy" : FormattableString.Invariant($"{horizon:g}h");
                foreach (var model in QuotaPaceModels.Candidates.Append("adaptive"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var trials = model == "adaptive" ? QuotaForecastBacktester.ReplayAdaptive(rows, horizon, cancellationToken)
                        : QuotaForecastBacktester.Replay(rows, model, horizon, cancellationToken);
                    scores.Add(Score(stream.Key.Kind, stream.Key.Source, model, target, "quota-capture-time", trials));
                }
                foreach (var availability in Enum.GetValues<ForecastReplayAvailability>())
                foreach (var candidate in QuotaWorkloadBacktester.Candidates)
                {
                    var trials = QuotaWorkloadBacktester.Replay(local, stream.Key.Kind, candidate, horizon, availability, cancellationToken);
                    // The advanced models produce a point residual correction, not a calibrated
                    // class probability/ETA. Do not inherit baseline risk/interval claims.
                    var points = trials.Select(x => x.Baseline with
                    {
                        PredictedRemaining = x.PredictedRemaining, LowerRemaining = null, UpperRemaining = null,
                        ObservedExhaustion = null, ExhaustionEtaBracketErrorHours = null
                    }).ToArray();
                    scores.Add(Score(stream.Key.Kind, stream.Key.Source, candidate, target, availability.ToString(), points,
                        trials.Count(x => x.UsedWorkloadModel)));
                }
            }
        }
        return new ForecastEvaluationReport(data.CapturedAtUtc, data.Quota.Count,
            data.Workload.Count, data.Tokens.Count, data.Tokens.Count(x => x.ReasoningEffort is not null),
            data.Tokens.Count(x => x.CapturedAtUtc is not null), scores,
            "Authoritative app-server targets, separate source/window streams. Origins at least 30 minutes apart with 15 minutes of history; outcomes within 5 minutes of requested horizon. Near-reset readings are proxies, not exact reset truth. Missing survival labels stay unknown. ETA error is distance to the observed saturation bracket, conditional on predicted exhaustion. Bands target 80% using prior completed reset generations at comparable lead, one score per generation (minimum 8). Ridge ablations fit only matured prior outcomes, minimum 12, and learn scaling/vocabulary from the training prefix. Fitted-origin count distinguishes real fits from incumbent fallback. Reconstructed native event history is distinct from strict collection-time availability. No calibrated exhaustion probabilities are emitted. " + data.Coverage);
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
        return new ForecastEvaluationScore(kind, source, model, target, availability, trials.Count,
            trials.Select(x => x.ResetUtc).Distinct().Count(), Mean(trials.Select(x => Math.Abs(x.PredictedRemaining - x.ObservedRemaining))),
            mse is double value ? Math.Sqrt(value) : null, bands.Length,
            Mean(bands.Select(x => x.ObservedRemaining >= x.LowerRemaining && x.ObservedRemaining <= x.UpperRemaining ? 1d : 0d)),
            Mean(bands.Select(x => x.UpperRemaining!.Value - x.LowerRemaining!.Value)), labels.Length,
            labels.Count(x => x.ObservedExhaustion == true), Mean(labels.Select(x => x.ObservedExhaustion == x.PredictedExhaustion ? 1d : 0d)),
            etas.Length, Mean(etas.Select(x => x.ExhaustionEtaBracketErrorHours!.Value)), fitted);
    }
}
