// Taj's Tokens | ForecastEvidence.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>Versioned, rebuildable forecast diagnostics, not provider evidence or calibrated probability.</summary>
public sealed record ForecastEvidence(
    string PolicyVersion,
    string Model,
    string HistorySource,
    int ObservationCount,
    double ObservedHours,
    int CalibrationEpochs,
    double? HistoricalAbsoluteErrorPercent,
    double? RemainingAtResetLowerPercent,
    double? RemainingAtResetUpperPercent,
    double? NominalIntervalCoverage,
    string UncertaintyDescription,
    IReadOnlyList<QuotaHorizonPrediction>? HorizonPredictions = null,
    string? WorkloadStatus = null)
{
    public string? AnchorLimitId { get; init; }
    public string? AnchorPlanType { get; init; }
    public string? HistoryPolicy { get; init; }
    public CodexInferenceManifest? InferenceManifest { get; init; }
}

/// <summary>Conditional meter-level prediction. Bands describe held-out errors, not exhaustion probability.</summary>
public sealed record QuotaHorizonPrediction(
    double HorizonHours,
    DateTimeOffset TargetUtc,
    double RemainingPercent,
    double ExpectedUsagePercent,
    string Model,
    bool UsesWorkload,
    int TrainingSamples,
    int ValidationSamples,
    double? ValidationMeanAbsoluteError,
    double? LowerRemainingPercent,
    double? UpperRemainingPercent,
    int IntervalSamples,
    string Explanation)
{
    public CodexNumericInference? Inference { get; init; }
}

/// <summary>Exact selected numeric calculation, not training data or a claim of causal feature weights.</summary>
public sealed record CodexNumericInference(
    string Contract,
    double Intercept,
    IReadOnlyList<double> Inputs,
    IReadOnlyList<double> Weights,
    double Minimum,
    double Maximum)
{

    public bool Equals(CodexNumericInference? other)
    {
        return other is not null && Contract == other.Contract &&
               Intercept.Equals(other.Intercept) && Minimum.Equals(other.Minimum) && Maximum.Equals(other.Maximum) &&
               Inputs.SequenceEqual(other.Inputs) && Weights.SequenceEqual(other.Weights);
    }

    public double Reconstruct()
    {
        return Math.Clamp(Intercept + Inputs.Select((value, i) => value * Weights[i]).Sum(), Minimum, Maximum);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Contract);
        hash.Add(Intercept);
        hash.Add(Minimum);
        hash.Add(Maximum);
        foreach (double value in Inputs) hash.Add(value);
        foreach (double value in Weights) hash.Add(value);
        return hash.ToHashCode();
    }
}