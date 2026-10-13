// Taj's Tokens | CodexInputComposition.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Descriptive composition of recorded native input tokens, not a context-rebuild diagnosis.</summary>
public sealed record CodexInputComposition(double CacheReadPercent, double CacheWritePercent)
{
    public static CodexInputComposition? From(CodexNativeTokenTotals? totals)
    {
        if (totals is null || totals.UncachedInput < 0 || totals.CacheRead < 0 || totals.CacheWrite < 0)
            return null;

        decimal input = (decimal)totals.UncachedInput + totals.CacheRead + totals.CacheWrite;
        if (input == 0) return null;

        return new(
            (double)(totals.CacheRead * 100m / input),
            (double)(totals.CacheWrite * 100m / input));
    }
}
