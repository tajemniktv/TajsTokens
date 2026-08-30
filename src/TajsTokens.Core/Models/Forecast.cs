using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record Forecast(
    QuotaWindowKind Kind,
    DateTimeOffset GeneratedAtUtc,
    double BurnRatePerHour,
    DateTimeOffset? EstimatedExhaustionAtUtc,
    bool SurvivesUntilReset,
    double SustainableTokensPerHour,
    double Confidence);
