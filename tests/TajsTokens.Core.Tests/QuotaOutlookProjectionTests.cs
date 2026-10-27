using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaOutlookProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");

    private static CurrentQuotaForecast Reading(QuotaWindowKind kind, double pressure, double? etaHours = null,
        double remaining = 50, string? account = "account")
    {
        var q = new QuotaSnapshot(kind, Now, 100 - remaining, kind == QuotaWindowKind.Weekly ? 10080 : 300,
            Now.AddHours(kind == QuotaWindowKind.Weekly ? 80 : 3), "codex", "default", "provider", AccountKey: account);
        return new(q, TelemetryHealthState.Live,
            new Forecast(kind, Now, 5, etaHours is { } eta ? Now.AddHours(eta) : null, etaHours is null,
                10, 0, etaHours is null ? ForecastState.SafeUntilReset : ForecastState.ExhaustionLikelyBeforeReset,
                pressure, 37), "");
    }

    private static CodexIntelligenceSnapshot Snapshot(params CurrentQuotaForecast[] readings) =>
        CodexIntelligenceProjection.Live(TelemetrySnapshot.Empty) with { Current = readings };

    [Fact]
    public void LimiterUsesPressureAndEarliestExhaustionRegardlessOfInputOrder()
    {
        CurrentQuotaForecast five = Reading(QuotaWindowKind.FiveHour, .7), week = Reading(QuotaWindowKind.Weekly, .9);
        Assert.Equal(week, QuotaOutlookProjection.Build(Snapshot(five, week), Now).Limiting!.Reading);
        Assert.Equal(week, QuotaOutlookProjection.Build(Snapshot(week, five), Now).Limiting!.Reading);
        five = Reading(QuotaWindowKind.FiveHour, 4, 2);
        week = Reading(QuotaWindowKind.Weekly, 2, 1);
        Assert.Equal(week, QuotaOutlookProjection.Build(Snapshot(five, week), Now).Limiting!.Reading);
        week = Reading(QuotaWindowKind.Weekly, 100, 2);
        Assert.Equal(five, QuotaOutlookProjection.Build(Snapshot(week, five), Now).Limiting!.Reading);
        Assert.True(QuotaOutlookProjection.Build(Snapshot(five with { Current = five.Current with { UsedPercent = 100 } }, week), Now)
            .Limiting!.Exhausted);
    }

    [Fact]
    public void MissingOrStaleWindowCannotProduceAnOverallSafeAnswer()
    {
        CurrentQuotaForecast five = Reading(QuotaWindowKind.FiveHour, .5), week = Reading(QuotaWindowKind.Weekly, .8);
        AccountOutlook partial = QuotaOutlookProjection.Build(Snapshot(five), Now);
        Assert.DoesNotContain("both allowances last", partial.Summary);
        Assert.Contains("Weekly: Meter unavailable", partial.Summary);
        Assert.False(QuotaOutlookProjection.Build(Snapshot(five, week with { State = TelemetryHealthState.Stale }), Now).Windows[1].Supported);
        Assert.False(QuotaOutlookProjection.Window(five.Current.Kind, five with { Current = five.Current with { AccountKey = null } }, false, Now).Supported);
        Assert.Null(QuotaOutlookProjection.Build(Snapshot(five, week with { Current = week.Current with { AccountKey = "other" } }), Now).Limiting);
        Assert.Equal("Meter unavailable", QuotaOutlookProjection.Window(five.Current.Kind,
            five with { Current = five.Current with { UsedPercent = 110 } }, false, Now).Conclusion);
    }

    [Fact]
    public void PointEstimateRemainsVisibleWithoutBandAndFlatMeterDoesNotClaimSafety()
    {
        CurrentQuotaForecast five = Reading(QuotaWindowKind.FiveHour, .5);
        AllowanceOutlook point = QuotaOutlookProjection.Window(five.Current.Kind, five, false, Now);
        Assert.True(point.Supported);
        Assert.Contains("37%", point.Conclusion);
        Assert.Contains("range not yet supported", point.Detail);
        AllowanceOutlook flat = QuotaOutlookProjection.Window(five.Current.Kind, five with
        { Forecast = five.Forecast! with { State = ForecastState.IdleWithinMeterPrecision, IsQuantizedFlat = true } }, false, Now);
        Assert.False(flat.Supported);
        Assert.Contains("Zero burn", flat.Detail);
        Assert.Equal("Not reported", QuotaOutlookProjection.Window(five.Current.Kind, five, true, Now).Conclusion);
    }

    [Fact]
    public void LiveContextIgnoresHistoricalWorkFiltersButNeverSubstitutesAnotherAccount()
    {
        CodexIntelligenceSnapshot live = Snapshot(Reading(QuotaWindowKind.FiveHour, .5));
        CodexIntelligenceSnapshot selected = live with { Selection = new(Now.AddDays(-20), Now.AddDays(-19), Model: "m", Project: "p") };
        Assert.Single(CodexIntelligenceProjection.WithLiveContext(selected, live, null).Current);
        Assert.Empty(CodexIntelligenceProjection.WithLiveContext(selected, live, "other").Current);
        Assert.Equal(selected.Selection, CodexIntelligenceProjection.WithLiveContext(selected, live, null).Selection);
    }

    [Fact]
    public void OptionalReasonsRoundTripWithoutInventingReasonsForOldDiagnostics()
    {
        var evidence = new ForecastEvidence("p", "pace", "s", 2, 1, 0, null, null, null, null, "old");
        string old = JsonSerializer.Serialize(evidence).Replace("\"SupportReasons\":0,", "");
        Assert.Equal(ForecastSupportReason.None, JsonSerializer.Deserialize<ForecastEvidence>(old)!.SupportReasons);
        evidence = evidence with { SupportReasons = ForecastSupportReason.ChallengerNotBetter | ForecastSupportReason.InsufficientIntervalEvidence };
        Assert.Equal(evidence, JsonSerializer.Deserialize<ForecastEvidence>(JsonSerializer.Serialize(evidence)));
        Assert.Contains("workload model has not improved", string.Join(" ", QuotaOutlookProjection.Describe(evidence.SupportReasons)));
    }
}
