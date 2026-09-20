// Taj's Tokens | SessionWorkloadOutlook.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public sealed record SessionWorkloadPrediction(
    double HorizonHours,
    int TrainingOrigins,
    int ActiveTrainingOrigins,
    double RecordedActivityProbability,
    double? ConditionalMeanTokens,
    double? ConditionalLowTokens,
    double? ConditionalHighTokens,
    double? ExpectedTokens,
    bool ActivityEstimateSupported,
    string Explanation);

public sealed record SessionWorkloadTrial(
    DateTimeOffset OriginUtc,
    DateTimeOffset EndUtc,
    int AgeGroup,
    double ObservedTokens,
    SessionWorkloadPrediction Prediction,
    double BaselineProbability,
    double PaceTokens);

public sealed record SessionWorkloadScore(
    double HorizonHours,
    int Origins,
    int ActiveOrigins,
    double? BrierScore,
    double? BaselineBrierScore,
    double? CalibrationError,
    int ConditionalOrigins,
    double? ConditionalMeanAbsoluteError,
    double? ConditionalPaceMeanAbsoluteError,
    double? ConditionalRangeCoverage,
    double? ExpectedMeanAbsoluteError,
    double? PaceMeanAbsoluteError)
{
    public int ExpectedOrigins { get; init; }
}

public sealed record SessionWorkloadReport(
    string Policy,
    string Methodology,
    IReadOnlyList<SessionWorkloadPrediction> Current,
    IReadOnlyList<SessionWorkloadScore> Scores,
    IReadOnlyList<SessionWorkloadTrial> Trials);