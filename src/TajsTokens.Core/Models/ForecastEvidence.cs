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
    string UncertaintyDescription);
