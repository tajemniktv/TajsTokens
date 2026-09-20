// Taj's Tokens | ForecastEvaluationReport.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record ForecastEvaluationScore(
    QuotaWindowKind Kind,
    string Source,
    string Model,
    string Target,
    string Availability,
    int Origins,
    int ResetGenerations,
    double? MeanAbsoluteError,
    double? RootMeanSquaredError,
    int IntervalOrigins,
    double? IntervalCoverage,
    double? MeanIntervalWidth,
    int ExhaustionLabels,
    int ExhaustionPositiveLabels,
    double? ExhaustionClassificationAccuracy,
    int EtaOrigins,
    double? EtaBracketMeanAbsoluteHours,
    int FittedOrigins = 0,
    string? AccountKey = null)
{
    public QuotaHistoryCohort? HistoryCohort { get; init; }
    public int NonOverlappingOrigins { get; init; }
}

public sealed record ForecastEvaluationReport(
    DateTimeOffset EvaluatedAtUtc,
    int AuthoritativeQuotaObservations,
    int WorkloadObservations,
    int TokenObservations,
    int TokensWithEffort,
    int TokensWithCollectionTime,
    IReadOnlyList<ForecastEvaluationScore> Scores,
    string Methodology)
{
    public IReadOnlyList<TokenForecastScore> TokenScores { get; init; } = [];
    public IReadOnlyList<SessionWorkloadScore> SessionScores { get; init; } = [];
    public string? QuotaHistorySummary { get; init; }
    public int HistoricalQuotaObservations { get; init; }
    public QuotaCostReport? QuotaCost { get; init; }
    public ComposedQuotaEvaluation? ComposedQuota { get; init; }
    public ComposedQuotaEvaluation? ComposedQuotaStrict { get; init; }
    public QuotaTransferEvaluation? QuotaTransfer { get; init; }
    public SessionQuotaEvaluation? SessionQuota { get; init; }
    public TtEvaluation? Tt { get; init; }
    public string? SavedTtSnapshotId { get; init; }
}