// Taj's Tokens | QuotaWorkloadCorrection.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public sealed record QuotaWorkloadTrial(
    QuotaForecastTrial Baseline,
    string Candidate,
    double PredictedRemaining,
    int TrainingSamples,
    bool UsedWorkloadModel,
    CodexForecastFeatures Features);

public sealed record QuotaWorkloadPrediction(
    double Remaining,
    int TrainingSamples,
    bool Fitted,
    CodexForecastFeatures Features)
{
    public CodexNumericInference? Inference { get; init; }
}

/// <summary>Forecast owner's account-local correction, fitted only to matured outcomes.</summary>
public static class QuotaWorkloadCorrection
{
    public const int MinimumTrainingSamples = 12;

    internal static IEnumerable<QuotaForecastTrial> Independent(IEnumerable<QuotaForecastTrial> trials)
    {
        DateTimeOffset? previousEnd = null;
        foreach (QuotaForecastTrial trial in trials.OrderBy(x => x.OriginUtc))
        {
            if (previousEnd is not null && trial.OriginUtc < previousEnd) continue;
            previousEnd = trial.OutcomeUtc;
            yield return trial;
        }
    }

    /// <summary>The live counterpart of the full model/effort ablation, fitted only to matured outcomes.</summary>
    public static QuotaWorkloadPrediction Predict(
        CodexForecastDataset data,
        QuotaSnapshot anchor,
        double horizonHours,
        double baselinePrediction,
        IReadOnlyList<QuotaForecastTrial> baseline,
        IReadOnlyDictionary<DateTimeOffset, CodexForecastFeatures> featureRows,
        ForecastReplayAvailability availability)
    {
        CodexForecastFeatures features = CodexForecastFeatureBuilder.Build(data, anchor.CapturedAtUtc, 2, availability);
        Dictionary<DateTimeOffset, QuotaSnapshot>
            origins = data.Quota.GroupBy(x => x.CapturedAtUtc).ToDictionary(x => x.Key, x => x.Last());
        QuotaForecastTrial[] training = Independent(
                baseline.Where(x => x.OutcomeUtc <= anchor.CapturedAtUtc && x.OriginUtc < anchor.CapturedAtUtc &&
                                    featureRows[x.OriginUtc].ObservedTokenEvents > 0 &&
                                    featureRows[x.OriginUtc].ModelTokenShares.Count > 0 &&
                                    featureRows[x.OriginUtc].EffortTokenShares.Count > 0))
            .TakeLast(120).ToArray();

        QuotaWorkloadPrediction Fallback()
        {
            return new QuotaWorkloadPrediction(baselinePrediction, training.Length, false, features);
        }

        if (training.Length < MinimumTrainingSamples || features.ObservedTokenEvents == 0 ||
            features.ModelTokenShares.Count == 0 || features.EffortTokenShares.Count == 0) return Fallback();
        string[] models = training.SelectMany(x => featureRows[x.OriginUtc].ModelTokenShares.Keys).Distinct().Order().Take(8).ToArray();
        string[] efforts = training.SelectMany(x => featureRows[x.OriginUtc].EffortTokenShares.Keys).Distinct().Order().Take(8).ToArray();
        // An unseen model/effort is not silently treated as the training cohort's average.
        if (features.ModelTokenShares.Keys.Except(models).Any() || features.EffortTokenShares.Keys.Except(efforts).Any()) return Fallback();

        double[] Row(QuotaForecastTrial trial)
        {
            return Vector(
                trial.LeadHours,
                origins[trial.OriginUtc].RemainingPercent!.Value,
                trial.PredictedRemaining,
                featureRows[trial.OriginUtc],
                "model-effort-ridge",
                models,
                efforts);
        }

        AccountLocalRidge? fit = AccountLocalRidge.Fit(
            training.Select(Row).ToArray(),
            training.Select(x => x.ObservedRemaining - x.PredictedRemaining).ToArray(),
            10);
        if (fit is null) return Fallback();
        double[] input = Vector(
            horizonHours,
            anchor.RemainingPercent!.Value,
            baselinePrediction,
            features,
            "model-effort-ridge",
            models,
            efforts);
        double prediction = baselinePrediction + fit.Predict(input);
        double[] normalized = input.Select((x, i) => (x - fit.Means[i]) / fit.Scales[i]).ToArray();
        return new QuotaWorkloadPrediction(Math.Clamp(prediction, 0, anchor.RemainingPercent.Value), training.Length, true, features)
        {
            Inference = new CodexNumericInference(
                "remaining=pace+standardized-ridge/v1",
                baselinePrediction + fit.Coefficients[0],
                normalized,
                fit.Coefficients.Skip(1).ToArray(),
                0,
                anchor.RemainingPercent.Value),
        };
    }

    internal static double[] Vector(
        QuotaForecastTrial trial,
        double anchorRemaining,
        CodexForecastFeatures features,
        string candidate,
        string[] models,
        string[] efforts)
    {
        return Vector(trial.LeadHours, anchorRemaining, trial.PredictedRemaining, features, candidate, models, efforts);
    }

    internal static double[] Vector(
        double leadHours,
        double anchorRemaining,
        double baselinePrediction,
        CodexForecastFeatures features,
        string candidate,
        string[] models,
        string[] efforts)
    {
        var vector = new List<double> { leadHours, anchorRemaining, baselinePrediction };
        if (candidate != "pace-ridge")
            vector.AddRange(
            [
                Math.Log(1 + features.Tokens), features.CacheReadShare ?? 0, features.ReasoningOutputShare ?? 0,
                features.CacheReadShare is null ? 1 : 0, features.ReasoningOutputShare is null ? 1 : 0, features.ObservedTokenEvents,
            ]);
        if (candidate is "activity-ridge" or "model-effort-ridge")
            vector.AddRange(
            [
                features.TokenActiveRootSessions, features.TokenActiveSubagentSessions, features.TokenActiveUnknownSessions,
                features.ObservedOpenTurns, features.CompletedTurnWallHours, features.CompletedTurns,
                features.Compactions, features.LastInputWindowRatio ?? 0, features.LastInputWindowRatio is null ? 1 : 0,
                features.PeakObservedTurnOverlap, features.ObservedContextEvents,
            ]);
        if (candidate == "model-effort-ridge")
        {
            vector.AddRange(models.Select(x => features.ModelTokenShares.GetValueOrDefault(x)));
            vector.AddRange(efforts.Select(x => features.EffortTokenShares.GetValueOrDefault(x)));
        }
        return vector.ToArray();
    }
}