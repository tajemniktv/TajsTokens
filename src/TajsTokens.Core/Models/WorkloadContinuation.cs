namespace TajsTokens.Core.Models;

/// <summary>Origin-only local-work assumption. Context exposure, fitted training and forecast horizon have separate clocks.</summary>
public sealed record WorkloadContinuationReceipt(
    string Version,
    DateTimeOffset ObservationFromUtc,
    DateTimeOffset ObservationToUtc,
    double ContextHours,
    double HorizonHours,
    DateTimeOffset? TrainingFromUtc,
    DateTimeOffset? TrainingToUtc,
    DateTimeOffset? LatestEvidenceUtc,
    DateTimeOffset? LatestPositiveWorkUtc,
    int Observations,
    decimal? RecordedTokens,
    double? ProjectedTokens,
    ForecastReplayAvailability Availability,
    bool IncludesElapsedQuietAndToolWaits,
    string Status,
    string Methodology);

public sealed record WorkloadContinuationResult(PredictedWorkload? Workload, WorkloadContinuationReceipt Receipt);
