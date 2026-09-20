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
public sealed record CodexNumericInference(string Contract, double Intercept,
    IReadOnlyList<double> Inputs, IReadOnlyList<double> Weights, double Minimum, double Maximum)
{
    public double Reconstruct() => Math.Clamp(Intercept + Inputs.Select((value, i) => value * Weights[i]).Sum(), Minimum, Maximum);
    public bool Equals(CodexNumericInference? other) => other is not null && Contract == other.Contract &&
        Intercept.Equals(other.Intercept) && Minimum.Equals(other.Minimum) && Maximum.Equals(other.Maximum) &&
        Inputs.SequenceEqual(other.Inputs) && Weights.SequenceEqual(other.Weights);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Contract); hash.Add(Intercept); hash.Add(Minimum); hash.Add(Maximum);
        foreach (var value in Inputs) hash.Add(value);
        foreach (var value in Weights) hash.Add(value);
        return hash.ToHashCode();
    }
}
