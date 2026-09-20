// Taj's Tokens | ForecastState.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Enums;

public enum ForecastState
{
    Learning,
    SafeUntilReset,
    NearSustainablePace,
    ExhaustionLikelyBeforeReset,
    IdleWithinMeterPrecision,
}