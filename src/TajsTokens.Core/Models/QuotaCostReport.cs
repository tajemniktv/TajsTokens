namespace TajsTokens.Core.Models;

public sealed record QuotaCostTrial(DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset ResetUtc,
    double ObservedDelta, double LowerDelta, double UpperDelta, double Prediction,
    double IntervalLoss, double Residual, double? LowerPrediction, double? UpperPrediction);

public sealed record QuotaCostScore(QuotaHistoryCohort Cohort, double HorizonHours, string Candidate,
    int Observations, int TrainingSamples, int TrainingGenerations, int HeldOutSamples, int HeldOutGenerations,
    double? IntervalLoss, double? DisplayedDeltaMae, double? GenerationMeanIntervalLoss,
    double? ResidualP10, double? ResidualMedian, double? ResidualP90, double? UnexplainedPositiveMovement,
    int BandSamples, double? BandIntersectsTargetRate, bool MaterialWin, string Status,
    IReadOnlyDictionary<string, double> Coefficients, IReadOnlyList<QuotaCostTrial> Trials,
    IReadOnlyList<DateTimeOffset> CandidateShiftResets)
{
    public string? RateCardVersion { get; init; }
    public int UnpricedTrainingIntervals { get; init; }
    public int UnpricedHeldOutIntervals { get; init; }
    public decimal UnpricedReportedTokens { get; init; }
    public double? PairedPaceIntervalLoss { get; init; }
    public double? PairedTotalIntervalLoss { get; init; }
}

public sealed record QuotaCostReport(string Version, string Methodology, string Coverage,
    int Observations, IReadOnlyDictionary<string, int> QualityCounts, IReadOnlyList<QuotaCostScore> Scores)
{
    public DateTimeOffset? DatasetCapturedAtUtc { get; init; }
    public QuotaEvaluationCoverage? EvidenceCoverage { get; init; }
    public IReadOnlyList<QuotaCostCohortCoverage> CohortCoverage { get; init; } = [];
}
