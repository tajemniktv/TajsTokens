// Taj's Tokens | TokenBreakdownTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class TokenBreakdownTests
{
    [Fact]
    public void Total_UsesDisjointBucketsWithoutDoubleCounting()
    {
        var breakdown = new TokenBreakdown(
            1_000,
            4_000,
            500,
            800,
            200);

        Assert.Equal(6_500, breakdown.Total);
    }

    [Fact]
    public void Total_PrefersProviderReportedTotalWhenPresent()
    {
        var breakdown = new TokenBreakdown(1_000, 4_000, 500, 800, 200, 6_400);
        Assert.Equal(6_400, breakdown.Total);
        Assert.Equal(6_500, breakdown.ComputedTotal);
    }
}