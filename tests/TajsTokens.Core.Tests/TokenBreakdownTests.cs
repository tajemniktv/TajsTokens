using TajsTokens.Core.Models;

namespace TajsTokens.Core.Tests;

public sealed class TokenBreakdownTests
{
    [Fact]
    public void Total_UsesDisjointBucketsWithoutDoubleCounting()
    {
        var breakdown = new TokenBreakdown(
            UncachedInput: 1_000,
            CacheRead: 4_000,
            CacheWrite: 500,
            NonReasoningOutput: 800,
            ReasoningOutput: 200);

        Assert.Equal(6_500, breakdown.Total);
    }

    [Fact]
    public void Total_PrefersProviderReportedTotalWhenPresent()
    {
        var breakdown = new TokenBreakdown(1_000, 4_000, 500, 800, 200, ReportedTotal: 6_400);
        Assert.Equal(6_400, breakdown.Total);
        Assert.Equal(6_500, breakdown.ComputedTotal);
    }
}
