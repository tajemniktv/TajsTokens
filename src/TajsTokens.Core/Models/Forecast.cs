using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record Forecast(
    QuotaWindowKind Kind,
    DateTimeOffset GeneratedAtUtc,
    double? BurnRatePercentPerHour,
    DateTimeOffset? EstimatedExhaustionAtUtc,
    bool? SurvivesUntilReset,
    double? SustainablePercentPerHour,
    double Confidence,
    ForecastState State = ForecastState.Learning,
    double? BurnPressure = null,
    double? ProjectedRemainingAtResetPercent = null,
    string? Trend = null,
    bool IsQuantizedFlat = false,
    ForecastEvidence? Evidence = null);
