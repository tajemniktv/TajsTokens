namespace TajsTokens.Core.Models;

public sealed record ComposedQuotaTrial(DateTimeOffset OriginUtc, DateTimeOffset OutcomeUtc, DateTimeOffset ResetUtc,
    double PredictedDelta, double ActualWorkloadCost, double ObservedDelta,
    double IntervalLoss, double CostOnlyIntervalLoss, double PaceIntervalLoss,
    double PredictedRemaining, PredictedWorkload Workload)
{
    public double? IncumbentIntervalLoss { get; init; }
    public ForecastReplayAvailability Availability { get; init; }
    public DateTimeOffset? CalibrationAvailableAtUtc { get; init; }
    public double? ObservedRemainingPercent { get; init; }
    public double? LowerRemainingPercent { get; init; }
    public double? UpperRemainingPercent { get; init; }
    public int CalibrationGenerations { get; init; }
}

public sealed record ComposedQuotaScore(QuotaHistoryCohort Cohort, double HorizonHours, string CostModel,
    int TrainingIntervals, int HeldOutIntervals, int ResetGenerations, int MissingComposition,
    double? IntervalLoss, double? CostOnlyIntervalLoss, double? PaceIntervalLoss,
    double? DisplayedDeltaMae, IReadOnlyList<ComposedQuotaTrial> Trials)
{
    public double? IncumbentIntervalLoss { get; init; }
    public ForecastReplayAvailability Availability { get; init; }
    public int IntervalOrigins { get; init; }
    public double? IntervalCoverage { get; init; }
    public double? MeanIntervalWidth { get; init; }
    public int AssertedTrainingIntervals { get; init; }
    /// <summary>Overlapping diagnostic counts; their sum is not the number of withheld intervals.</summary>
    public IReadOnlyDictionary<string, int> WithheldReasons { get; init; } = new Dictionary<string, int>();
}

public sealed record ComposedQuotaEvaluation(string Version, string Methodology, IReadOnlyList<ComposedQuotaScore> Scores);
