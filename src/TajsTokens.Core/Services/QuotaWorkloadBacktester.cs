using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed record QuotaWorkloadTrial(QuotaForecastTrial Baseline, string Candidate,
    double PredictedRemaining, int TrainingSamples, bool UsedWorkloadModel,
    CodexForecastFeatures Features);

/// <summary>Authoritative-target walk-forward feature ablations. Future workloads never enter a training prefix.</summary>
public static class QuotaWorkloadBacktester
{
    public static readonly string[] Candidates = ["pace-ridge", "token-ridge", "activity-ridge", "model-effort-ridge"];
    public const int MinimumTrainingSamples = 12;

    public static IReadOnlyList<QuotaWorkloadTrial> Replay(CodexForecastDataset data, QuotaWindowKind kind,
        string candidate, double? horizonHours, ForecastReplayAvailability availability,
        CancellationToken cancellationToken = default)
    {
        if (!Candidates.Contains(candidate)) throw new ArgumentException("Unknown workload candidate.", nameof(candidate));
        var results = new List<QuotaWorkloadTrial>();
        foreach (var stream in data.Quota.Where(x => x.Kind == kind && x.Authority == QuotaObservationAuthority.ProviderAuthoritative)
                     .GroupBy(x => (x.Provider, x.Profile, x.Source)))
        {
            var baseline = QuotaForecastBacktester.Replay(stream.ToArray(), "legacy-ewma", horizonHours, cancellationToken);
            var featureRows = baseline.ToDictionary(x => x.OriginUtc,
                x => CodexForecastFeatureBuilder.Build(data, x.OriginUtc, 2, availability));
            var origins = stream.GroupBy(x => x.CapturedAtUtc).ToDictionary(x => x.Key, x => x.Last());
            foreach (var trial in baseline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool Supported(QuotaForecastTrial x) => candidate == "pace-ridge" ||
                    (featureRows[x.OriginUtc].ObservedTokenEvents > 0 &&
                     (candidate != "model-effort-ridge" ||
                      (featureRows[x.OriginUtc].ModelTokenShares.Count > 0 && featureRows[x.OriginUtc].EffortTokenShares.Count > 0)));
                var training = baseline.Where(x => x.OutcomeUtc <= trial.OriginUtc && x.OriginUtc < trial.OriginUtc && Supported(x))
                    .OrderByDescending(x => x.OutcomeUtc).Take(120).ToArray();
                var prediction = trial.PredictedRemaining;
                var used = false;
                if (training.Length >= MinimumTrainingSamples && Supported(trial))
                {
                    // The vocabulary is selected only from the training prefix, not from future models.
                    var models = training.SelectMany(x => featureRows[x.OriginUtc].ModelTokenShares.Keys).Distinct().Order().Take(8).ToArray();
                    var efforts = training.SelectMany(x => featureRows[x.OriginUtc].EffortTokenShares.Keys).Distinct().Order().Take(8).ToArray();
                    double[] Features(QuotaForecastTrial x) => Vector(x, origins[x.OriginUtc].RemainingPercent!.Value, featureRows[x.OriginUtc], candidate, models, efforts);
                    var fit = AccountLocalRidge.Fit(training.Select(Features).ToArray(),
                        training.Select(x => x.ObservedRemaining - x.PredictedRemaining).ToArray(), penalty: 10);
                    if (fit is not null)
                    {
                        prediction = Math.Clamp(trial.PredictedRemaining + fit.Predict(Features(trial)), 0, origins[trial.OriginUtc].RemainingPercent!.Value);
                        used = true;
                    }
                }
                results.Add(new QuotaWorkloadTrial(trial, candidate, prediction, training.Length, used, featureRows[trial.OriginUtc]));
            }
        }
        return results;
    }

    private static double[] Vector(QuotaForecastTrial trial, double anchorRemaining, CodexForecastFeatures features,
        string candidate, string[] models, string[] efforts)
    {
        var vector = new List<double> { trial.LeadHours, anchorRemaining, trial.PredictedRemaining };
        if (candidate != "pace-ridge")
            vector.AddRange([Math.Log(1 + features.Tokens), features.CacheReadShare ?? 0, features.ReasoningOutputShare ?? 0,
                features.CacheReadShare is null ? 1 : 0, features.ReasoningOutputShare is null ? 1 : 0, features.ObservedTokenEvents]);
        if (candidate is "activity-ridge" or "model-effort-ridge")
            vector.AddRange([features.TokenActiveRootSessions, features.TokenActiveSubagentSessions, features.TokenActiveUnknownSessions,
                features.ObservedOpenTurns, features.CompletedTurnWallHours, features.CompletedTurns,
                features.Compactions, features.LastInputWindowRatio ?? 0, features.LastInputWindowRatio is null ? 1 : 0,
                features.PeakObservedTurnOverlap, features.ObservedContextEvents]);
        if (candidate == "model-effort-ridge")
        {
            vector.AddRange(models.Select(x => features.ModelTokenShares.GetValueOrDefault(x)));
            vector.AddRange(efforts.Select(x => features.EffortTokenShares.GetValueOrDefault(x)));
        }
        return vector.ToArray();
    }
}
