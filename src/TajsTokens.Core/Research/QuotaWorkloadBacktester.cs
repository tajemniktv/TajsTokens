// Taj's Tokens | QuotaWorkloadBacktester.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Research;

public static class QuotaWorkloadBacktester
{
    public static readonly string[] Candidates = ["pace-ridge", "token-ridge", "activity-ridge", "model-effort-ridge"];

    public static IReadOnlyList<QuotaWorkloadTrial> Replay(
        CodexForecastDataset data,
        QuotaWindowKind kind,
        string candidate,
        double? horizonHours,
        ForecastReplayAvailability availability,
        CancellationToken cancellationToken = default,
        string baselineModel = "legacy-ewma")
    {
        if (!Candidates.Contains(candidate)) throw new ArgumentException("Unknown workload candidate.", nameof(candidate));
        var results = new List<QuotaWorkloadTrial>();
        foreach (IGrouping<QuotaHistoryCohort, QuotaSnapshot> stream in data.Quota.Where(x => x.Kind == kind)
                     .GroupBy(QuotaHistoryPolicy.Cohort))
        {
            IReadOnlyList<QuotaForecastTrial> baseline = QuotaForecastCalibration.Replay(
                stream.ToArray(),
                baselineModel,
                horizonHours,
                cancellationToken);
            Dictionary<DateTimeOffset, CodexForecastFeatures> featureRows = baseline.ToDictionary(
                x => x.OriginUtc,
                x => CodexForecastFeatureBuilder.Build(data, x.OriginUtc, 2, availability));
            Dictionary<DateTimeOffset, QuotaSnapshot>
                origins = stream.GroupBy(x => x.CapturedAtUtc).ToDictionary(x => x.Key, x => x.Last());
            foreach (QuotaForecastTrial trial in baseline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool Supported(QuotaForecastTrial x)
                {
                    return candidate == "pace-ridge" ||
                           featureRows[x.OriginUtc].ObservedTokenEvents > 0 &&
                           (candidate != "model-effort-ridge" ||
                            featureRows[x.OriginUtc].ModelTokenShares.Count > 0 && featureRows[x.OriginUtc].EffortTokenShares.Count > 0);
                }

                QuotaForecastTrial[] training = QuotaWorkloadCorrection.Independent(
                        baseline.Where(x => x.OutcomeUtc <= trial.OriginUtc && x.OriginUtc < trial.OriginUtc && Supported(x)))
                    .TakeLast(120).ToArray();
                double prediction = trial.PredictedRemaining;
                bool used = false;
                if (training.Length >= QuotaWorkloadCorrection.MinimumTrainingSamples && Supported(trial))
                {
                    // The vocabulary is selected only from the training prefix, not from future models.
                    string[] models = training.SelectMany(x => featureRows[x.OriginUtc].ModelTokenShares.Keys).Distinct().Order().Take(8)
                        .ToArray();
                    string[] efforts = training.SelectMany(x => featureRows[x.OriginUtc].EffortTokenShares.Keys).Distinct().Order().Take(8)
                        .ToArray();

                    double[] Features(QuotaForecastTrial x)
                    {
                        return QuotaWorkloadCorrection.Vector(
                            x,
                            origins[x.OriginUtc].RemainingPercent!.Value,
                            featureRows[x.OriginUtc],
                            candidate,
                            models,
                            efforts);
                    }

                    AccountLocalRidge? fit = AccountLocalRidge.Fit(
                        training.Select(Features).ToArray(),
                        training.Select(x => x.ObservedRemaining - x.PredictedRemaining).ToArray(),
                        10);
                    if (fit is not null)
                    {
                        prediction = Math.Clamp(
                            trial.PredictedRemaining + fit.Predict(Features(trial)),
                            0,
                            origins[trial.OriginUtc].RemainingPercent!.Value);
                        used = true;
                    }
                }
                results.Add(new QuotaWorkloadTrial(trial, candidate, prediction, training.Length, used, featureRows[trial.OriginUtc]));
            }
        }
        return results;
    }
}