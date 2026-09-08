using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record ForecastEvaluationScore(
    QuotaWindowKind Kind, string Source, string Model, string Target, string Availability,
    int Origins, int ResetGenerations, double? MeanAbsoluteError, double? RootMeanSquaredError,
    int IntervalOrigins, double? IntervalCoverage, double? MeanIntervalWidth,
    int ExhaustionLabels, int ExhaustionPositiveLabels, double? ExhaustionClassificationAccuracy,
    int EtaOrigins, double? EtaBracketMeanAbsoluteHours, int FittedOrigins = 0);

public sealed record ForecastEvaluationReport(
    DateTimeOffset EvaluatedAtUtc, int AuthoritativeQuotaObservations, int WorkloadObservations,
    int TokenObservations, int TokensWithEffort, int TokensWithCollectionTime,
    IReadOnlyList<ForecastEvaluationScore> Scores, string Methodology);
