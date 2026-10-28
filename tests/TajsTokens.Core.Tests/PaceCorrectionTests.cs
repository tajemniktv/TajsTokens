using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class PaceCorrectionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static QuotaSnapshot Point(double hours, double? used) => new(QuotaWindowKind.Weekly, Start.AddHours(hours), used,
        10080, Start.AddDays(7), "codex", "default", "codex-app-server:codex", "account")
    { CollectedAtUtc = Start.AddHours(hours), HasSourceTimestamp = true, LimitId = "codex", PlanType = "A" };

    [Fact]
    public void FreshFlatPollDensityAndSameClockCachedCopiesCannotManufactureDecay()
    {
        QuotaSnapshot[] sparse = [Point(0, 10), Point(1, 12), Point(2, 12)];
        QuotaSnapshot[] dense = sparse.Concat(Enumerable.Range(1, 59).Select(i => Point(1 + i / 60d, 12))).OrderBy(x => x.CapturedAtUtc).ToArray();
        Forecast a = QuotaPredictionService.BuildResetOutlook(sparse, sparse[^1].CapturedAtUtc);
        Forecast b = QuotaPredictionService.BuildResetOutlook(dense, dense[^1].CapturedAtUtc);
        Forecast cached = QuotaPredictionService.BuildResetOutlook(dense.Concat(dense).ToArray(), dense[^1].CapturedAtUtc);
        Assert.Equal(a.BurnRatePercentPerHour!.Value, b.BurnRatePercentPerHour!.Value, 10);
        Assert.Equal(b.BurnRatePercentPerHour, cached.BurnRatePercentPerHour);
        Assert.Equal(a.ProjectedRemainingAtResetPercent!.Value, b.ProjectedRemainingAtResetPercent!.Value, 10);
        Assert.Equal(b.Evidence!.ObservationCount, cached.Evidence!.ObservationCount);
        Assert.Equal(a.SurvivesUntilReset, b.SurvivesUntilReset);
        Assert.Equal(0, b.Confidence);
        Assert.Null(b.Evidence.NominalIntervalCoverage);
    }

    [Fact]
    public void StartupNeedsElapsedHistoryAndAllFlatNeverBecomesMeasuredZero()
    {
        QuotaSnapshot[] startup = Enumerable.Range(0, 100).Select(i => Point(i / 600d, 10 + i / 100d)).ToArray();
        Assert.Equal(ForecastState.Learning, QuotaPredictionService.BuildResetOutlook(startup, startup[^1].CapturedAtUtc).State);
        Forecast flat = QuotaPredictionService.BuildResetOutlook([Point(0, 10), Point(1, 10), Point(2, 10)], Start.AddHours(2));
        Assert.Equal(ForecastState.IdleWithinMeterPrecision, flat.State);
        Assert.Null(flat.BurnRatePercentPerHour);
        Assert.Null(flat.SurvivesUntilReset);
        Assert.False(flat.Evidence!.PaceReceipt!.CalculationSupported);
        Assert.Null(flat.Evidence.PaceReceipt.RatePercentPerHour);
        Assert.Contains("flat within meter precision", flat.Evidence.PaceReceipt.SelectionReason);
        Forecast invalid = QuotaPredictionService.BuildResetOutlook([Point(0, -1)], Start);
        Assert.Null(invalid.Evidence!.PaceReceipt!.ReportedRemainingPercent);
        Assert.Null(invalid.Evidence.PaceReceipt.SustainablePercentPerHour);
    }

    [Fact]
    public void StaleMeasurementCannotBecomeFreshWhenStrictReplayMovesItsAvailabilityClock()
    {
        QuotaSnapshot[] measured = [Point(0, 10), Point(1, 12) with { CollectedAtUtc = Start.AddHours(9) }];
        QuotaSnapshot[] replay = QuotaHistoryPolicy.AvailableRows(measured, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(Start.AddHours(1), replay[^1].ReplayMeasuredAtUtc);
        Assert.Equal(Start.AddHours(9), replay[^1].CapturedAtUtc);
        Forecast forecast = QuotaPredictionService.BuildResetOutlook(replay, Start.AddHours(9));
        Assert.True(forecast.Evidence!.SupportReasons.HasFlag(ForecastSupportReason.StaleMeter));
        Assert.Null(forecast.BurnRatePercentPerHour);
        Assert.Equal(Start.AddHours(1), forecast.Evidence.PaceReceipt!.AnchorMeasuredAtUtc);
        Assert.Equal(Start.AddHours(9), forecast.Evidence.PaceReceipt.AnchorCollectedAtUtc);
        Assert.Equal(1, forecast.Evidence.PaceReceipt.LaterCollectedObservations);
    }

    [Fact]
    public void RegimeReturnAndResetDoNotResurrectOldSlopesAndIndependentBucketsStayIndependent()
    {
        QuotaSnapshot[] before = [Point(0, 10), Point(1, 20), Point(2, 30)];
        QuotaSnapshot back = Point(4, 40);
        Forecast regime = QuotaPredictionService.BuildResetOutlook(before.Concat([Point(3, 35) with { PlanType = "B" }, back]).ToArray(), back.CapturedAtUtc);
        Assert.Null(regime.BurnRatePercentPerHour);
        Assert.Equal(1, regime.Evidence!.ObservationCount);
        Forecast bucket = QuotaPredictionService.BuildResetOutlook(before.Concat([Point(3, 90) with { LimitId = "other" }, back]).ToArray(), back.CapturedAtUtc);
        Assert.NotNull(bucket.BurnRatePercentPerHour);
        Assert.Equal(4, bucket.Evidence!.ObservationCount);
        Forecast reset = QuotaPredictionService.BuildResetOutlook(before.Append(Point(3, 1) with { ResetsAtUtc = Start.AddDays(14) }).ToArray(), Start.AddHours(3));
        Assert.Null(reset.BurnRatePercentPerHour);
        Assert.Equal(1, reset.Evidence!.ObservationCount);
        Forecast invalid = QuotaPredictionService.BuildResetOutlook([Point(0, 0), Point(1, 50), Point(2, null), Point(3, 60), Point(4, 61)], Start.AddHours(4));
        Assert.Equal(1, invalid.BurnRatePercentPerHour);
        Assert.Equal(Start.AddHours(3), invalid.Evidence!.PaceReceipt!.FromUtc);
    }

    [Fact]
    public void LegacyCannotWinProductionSelectionEvenWhenItsHistoricalErrorsAreSmaller()
    {
        QuotaForecastTrial[] Trials(string model, double error) => Enumerable.Range(1, 9).Select(i =>
        {
            DateTimeOffset reset = Start.AddDays(-i);
            return new QuotaForecastTrial(model, "near-reset-proxy", reset.AddHours(-2), reset, reset, 2,
                error, 0, null, null, 0, null, false, null);
        }).OrderBy(x => x.OriginUtc).ToArray();
        var candidates = new Dictionary<string, IReadOnlyList<QuotaForecastTrial>>
        {
            ["legacy-ewma"] = Trials("legacy-ewma", 0), ["time-ewma-2h"] = Trials("time-ewma-2h", 5),
        };
        Assert.Equal("time-ewma-2h", QuotaForecastCalibration.SelectModel(candidates, Start, Start.AddHours(2), 2, "legacy-ewma"));
        Assert.All(QuotaForecastCalibration.ReplayAdaptive(candidates, "legacy-ewma"), x => Assert.DoesNotContain("legacy", x.Model));
        Assert.Empty(QuotaForecastCalibration.ReplayAdaptive(new Dictionary<string, IReadOnlyList<QuotaForecastTrial>>
        { ["legacy-ewma"] = candidates["legacy-ewma"] }, "legacy-ewma"));
        Assert.DoesNotContain("legacy-ewma", QuotaPaceModels.ProductionCandidates);
        Assert.Contains("legacy-ewma", QuotaPaceModels.Candidates);
    }

    [Fact]
    public void ReceiptReconstructsPaceAndKeepsReportedBudgetIndependentOfConsumptionProjection()
    {
        QuotaSnapshot[] readings = [Point(0, 10), Point(1, 12), Point(2, 13)];
        DateTimeOffset generated = Start.AddHours(2).AddMinutes(3);
        Forecast forecast = QuotaPredictionService.BuildResetOutlook(readings, generated);
        QuotaPaceReceipt receipt = forecast.Evidence!.PaceReceipt!;
        Assert.Equal(receipt.NumeratorPercent!.Value / receipt.DenominatorHours!.Value, forecast.BurnRatePercentPerHour!.Value, 12);
        Assert.Equal(87 / (readings[^1].ResetsAtUtc!.Value - generated).TotalHours, forecast.SustainablePercentPerHour);
        Assert.Equal(forecast.SustainablePercentPerHour, receipt.SustainablePercentPerHour);
        Assert.Equal(readings[^1].CapturedAtUtc, receipt.AnchorMeasuredAtUtc);
        Assert.Equal(readings[^1].CollectedAtUtc, receipt.AnchorCollectedAtUtc);
        Assert.Equal(receipt, JsonSerializer.Deserialize<QuotaPaceReceipt>(JsonSerializer.Serialize(receipt)));
        Assert.Contains("no eligible candidate", receipt.SelectionReason);
        Assert.Throws<ArgumentException>(() => QuotaPaceModels.Receipt(readings, readings[^1], generated, "legacy-ewma", "test", true));
    }
}
