namespace TajsTokens.Core.Models;

public sealed record SessionQuotaTrial(DateTimeOffset OriginUtc, DateTimeOffset EndUtc, DateTimeOffset ResetUtc,
    bool RecordedActivity, double ActivityProbability, double ConditionalQuota, double ExpectedQuota,
    double ObservedQuota, double ConditionalError, double ExpectedError, double PaceError,
    double ExpectedIntervalLoss, double PaceIntervalLoss, double? LowerExpectedQuota, double? UpperExpectedQuota)
{
    public double? IncumbentError { get; init; }
    public double? IncumbentIntervalLoss { get; init; }
}

public sealed record SessionQuotaScore(QuotaHistoryCohort Cohort, double HorizonHours,
    int TrainingIntervals, int WithheldIntervals, IReadOnlyList<SessionQuotaTrial> Trials,
    int ResetGenerations, int ActiveOutcomes, double? ConditionalMae, double? ConditionalPaceMae,
    double? ExpectedMae, double? PaceMae, double? IntervalLoss, double? PaceIntervalLoss,
    int BandOrigins, double? ReportedBandCoverage, double? MeanBandWidth)
{
    public string? TrainingIssue { get; init; }
    public int IncumbentPairedOrigins { get; init; }
    public double? PairedExpectedMae { get; init; }
    public double? IncumbentMae { get; init; }
    public double? PairedExpectedIntervalLoss { get; init; }
    public double? IncumbentIntervalLoss { get; init; }
}

public sealed record SessionQuotaEvaluation(string Version, string Methodology, IReadOnlyList<SessionQuotaScore> Scores);
