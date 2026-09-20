// Taj's Tokens | ComposedQuotaEvaluation.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Models;

public sealed record ComposedQuotaTrial(
    DateTimeOffset OriginUtc,
    DateTimeOffset OutcomeUtc,
    DateTimeOffset ResetUtc,
    double PredictedDelta,
    double ActualWorkloadCost,
    double ObservedDelta,
    double IntervalLoss,
    double CostOnlyIntervalLoss,
    double PaceIntervalLoss,
    double PredictedRemaining,
    PredictedWorkload Workload)
{
    public double? IncumbentIntervalLoss { get; init; }
    public ForecastReplayAvailability Availability { get; init; }
    public DateTimeOffset? CalibrationAvailableAtUtc { get; init; }
    public double? ObservedRemainingPercent { get; init; }
    public double? LowerRemainingPercent { get; init; }
    public double? UpperRemainingPercent { get; init; }
    public int CalibrationBlocks { get; init; }
    public string OriginActivity { get; init; } = "unrecorded";
    public long? RecordedOutcomeTokens { get; init; }
    public double? ZeroUseIntervalLoss { get; init; }

    /// <summary>Later outcome diagnostics, never origin-time predictors or completeness guarantees.</summary>
    public IReadOnlyList<string> OutcomeQualityFlags { get; init; } = [];

    public bool? CompleteOutcomeTokenCategories { get; init; }
    public double? LowerObservedDelta { get; init; }
    public double? UpperObservedDelta { get; init; }
}

public sealed record ComposedQuotaScore(
    QuotaHistoryCohort Cohort,
    double HorizonHours,
    string CostModel,
    int TrainingIntervals,
    int HeldOutIntervals,
    int ResetGenerations,
    int MissingComposition,
    double? IntervalLoss,
    double? CostOnlyIntervalLoss,
    double? PaceIntervalLoss,
    double? DisplayedDeltaMae,
    IReadOnlyList<ComposedQuotaTrial> Trials)
{
    public ChronologicalEvidence.Support EvidenceSupport => ChronologicalEvidence.Describe(
        Trials,
        x => x.OriginUtc,
        x => x.OutcomeUtc,
        x => x.ResetUtc,
        x => x.ObservedDelta - x.PredictedDelta);

    public double? IncumbentIntervalLoss { get; init; }
    public ForecastReplayAvailability Availability { get; init; }
    public int IntervalOrigins { get; init; }
    public double? IntervalCoverage { get; init; }
    public double? MeanIntervalWidth { get; init; }
    public int AssertedTrainingIntervals { get; init; }

    /// <summary>Overlapping diagnostic counts; their sum is not the number of withheld intervals.</summary>
    public IReadOnlyDictionary<string, int> WithheldReasons { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<ComposedQuotaBreakdown> Breakdowns { get; init; } = [];
}

public sealed record ComposedQuotaBreakdown(
    string Dimension,
    string Group,
    int Outcomes,
    int ResetGenerations,
    double ForecastIntervalLoss,
    double CostOnlyIntervalLoss,
    double PaceIntervalLoss,
    double SignedBias,
    int IncumbentPairs,
    double? PairedForecastIntervalLoss,
    double? IncumbentIntervalLoss,
    int ZeroUsePairs,
    double? ZeroPairedForecastIntervalLoss,
    double? ZeroUseIntervalLoss,
    int BandOutcomes,
    double? BandCoverage,
    double? MeanBandWidth)
{
    public int MeteredOutcomes { get; init; }
    public int UnderpredictedOutcomes { get; init; }

    /// <summary>Mean max(0, lower observed delta - prediction), including zero misses in eligible outcomes.</summary>
    public double? MeanUnderprediction { get; init; }
}

public sealed record ComposedQuotaEvaluation(string Version, string Methodology, IReadOnlyList<ComposedQuotaScore> Scores);