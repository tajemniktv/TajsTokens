using System.Text.Json;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class CodexDailyReportTests
{
    private static TajsTokens.Core.Models.CodexDailyReport Parse(string json, bool relative = false) =>
        CodexDailyReportParser.Parse(json, relative, "test", "2026-09-01", "2026-09-18");

    [Fact]
    public void ZeroCreditsDoNotEraseNonzeroTokensOrBecomeMissing()
    {
        var report = Parse("""{"balance_unit":"credit","data":[{"date":"2026-09-17","totals":{"credits":0,"text_total_tokens":500,"cached_text_input_tokens":400},"prompt":"secret"}]}""");
        var day = Assert.Single(report.Days);
        Assert.Equal(0m, day.Credits);
        Assert.Equal(500, day.TotalTokens);
        Assert.Null(day.OnDemandCredits);
        Assert.Null(day.OutputTokens);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(report));
    }

    [Fact]
    public void RelativeAmountsKeepTheirUnitsAndNeverBecomeCredits()
    {
        var report = Parse("""{"units":"percent","data_freshness_ts":"2026-09-18T01:00:00Z","data":[{"date":"2026-09-17","product_surface_usage_values":{"cli":125.5},"models":[{"credits":125.5}]}]}""", true);
        Assert.Equal("percent", report.Units);
        Assert.Equal(125.5m, Assert.Single(report.Days).SurfaceUsage!["cli"]);
        Assert.Null(report.Days[0].Credits);
        Assert.Null(report.Days[0].TotalTokens);
    }

    [Theory]
    [InlineData("{\"data\":[{\"date\":\"2026-09-17\",\"totals\":{\"credits\":1,\"credits\":2}}]}")]
    [InlineData("{\"data\":[{\"date\":\"2026-09-17\",\"totals\":{\"text_total_tokens\":1.5}}]}")]
    [InlineData("{\"data\":[{\"date\":\"2026-09-17\",\"totals\":{\"credits\":-1}}]}")]
    [InlineData("{\"data\":[{\"date\":\"2026-08-31\",\"totals\":{}}]}")]
    [InlineData("{\"data\":[{\"date\":\"2026-09-17\",\"totals\":{}},{\"date\":\"2026-09-17\",\"totals\":{}}]}")]
    public void AmbiguousOrOutOfScopeReportsAreRejected(string json) => Assert.Throws<JsonException>(() => Parse(json));

    [Fact]
    public void UnknownUnitsArePreservedRatherThanAssumedPercent()
    {
        Assert.Equal("new-policy-unit", Parse("""{"units":"new-policy-unit","data":[]}""", true).Units);
        Assert.Null(Parse("""{"data":[]}""", true).Units);
    }

    [Fact]
    public async Task DisabledAdapterDoesNotAccessCredentialsOrNetwork()
    {
        var result = await new CodexBackendDailyEvidenceProvider(() => false).CollectAsync([], CancellationToken.None);
        Assert.Empty(result.Observations);
    }
}
