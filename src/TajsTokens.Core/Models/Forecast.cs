// Taj's Tokens | Forecast.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

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