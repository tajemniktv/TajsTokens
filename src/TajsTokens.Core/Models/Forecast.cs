using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record Forecast(
    QuotaWindowKind Kind,
    DateTimeOffset GeneratedAtUtc,
    double? BurnRatePercentPerHour,
    DateTimeOffset? EstimatedExhaustionAtUtc,
    bool? SurvivesUntilReset,
    double? SustainablePercentPerHour,
    double Confidence);
