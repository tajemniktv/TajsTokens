namespace TajsTokens.Core.Models;

public sealed record ComposedQuotaTrial(DateTimeOffset OriginUtc, DateTimeOffset OutcomeUtc, DateTimeOffset ResetUtc,
    double PredictedDelta, double ActualWorkloadCost, double ObservedDelta,
    double IntervalLoss, double CostOnlyIntervalLoss, double PaceIntervalLoss,
    double PredictedRemaining, PredictedWorkload Workload)
{
    public double? IncumbentIntervalLoss { get; init; }
}

public sealed record ComposedQuotaScore(QuotaHistoryCohort Cohort, double HorizonHours, string CostModel,
    int TrainingIntervals, int HeldOutIntervals, int ResetGenerations, int MissingComposition,
    double? IntervalLoss, double? CostOnlyIntervalLoss, double? PaceIntervalLoss,
    double? DisplayedDeltaMae, IReadOnlyList<ComposedQuotaTrial> Trials)
{
    public double? IncumbentIntervalLoss { get; init; }
}

public sealed record ComposedQuotaEvaluation(string Version, string Methodology, IReadOnlyList<ComposedQuotaScore> Scores);
