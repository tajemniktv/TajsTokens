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
    string Explanation);
