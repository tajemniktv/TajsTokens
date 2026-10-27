using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

/// <summary>
/// PACE-001: characterize the retained legacy fallback defect, not its correctness.
/// Keep its research replay reproducible without making its rate a production floor.
/// </summary>
public sealed class WeeklyPaceRegressionTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "weekly-pace-flat-rounded-2026-10-27.json")));

    private static QuotaSnapshot[] Readings(JsonDocument fixture)
    {
        return fixture.RootElement.GetProperty("readings").EnumerateArray().Select(x => new QuotaSnapshot(
            QuotaWindowKind.Weekly, x.GetProperty("capturedAtUtc").GetDateTimeOffset(), x.GetProperty("usedPercent").GetDouble(),
            10080, x.GetProperty("resetsAtUtc").GetDateTimeOffset(), "codex", "default", "codex-app-server:codex", "fixture-account")
        { LimitId = "codex", PlanType = "fixture-plan", CollectedAtUtc = x.GetProperty("capturedAtUtc").GetDateTimeOffset(), HasSourceTimestamp = true }).ToArray();
    }

    [Fact]
    public void RetainedAnchorsReplayTheSameFallbackWithoutAnEstimatorSwitch()
    {
        using JsonDocument fixture = Fixture();
        QuotaSnapshot[] quota = Readings(fixture);
        foreach (JsonElement expected in fixture.RootElement.GetProperty("forecasts").EnumerateArray())
        {
            DateTimeOffset at = expected.GetProperty("capturedAtUtc").GetDateTimeOffset();
            Forecast forecast = QuotaPredictionService.BuildResetOutlook(quota.Where(x => x.CapturedAtUtc <= at).ToArray(),
                expected.GetProperty("generatedAtUtc").GetDateTimeOffset());
            Assert.Equal(expected.GetProperty("model").GetString(), forecast.Evidence!.Model);
            Assert.Equal(expected.GetProperty("policy").GetString(), forecast.Evidence.PolicyVersion);
            Assert.Equal(expected.GetProperty("burnRatePercentPerHour").GetDouble(), forecast.BurnRatePercentPerHour!.Value, 9);
            Assert.Equal(expected.GetProperty("sustainablePercentPerHour").GetDouble(), forecast.SustainablePercentPerHour!.Value, 9);
            Assert.Equal(expected.GetProperty("remainingAtResetPercent").GetDouble(), forecast.ProjectedRemainingAtResetPercent!.Value, 9);
            Assert.Equal(expected.GetProperty("observationCount").GetInt32(), forecast.Evidence.ObservationCount);
            Assert.Equal(expected.GetProperty("observedHours").GetDouble(), forecast.Evidence.ObservedHours, 9);
            Assert.Equal(expected.GetProperty("survivesUntilReset").GetBoolean(), forecast.SurvivesUntilReset);
            Assert.Equal(0, forecast.Evidence.CalibrationEpochs);
            Assert.Null(forecast.Evidence.RemainingAtResetLowerPercent);
            Assert.Null(forecast.Evidence.RemainingAtResetUpperPercent);
        }
    }

    [Fact]
    public void FlatRoundedPollDensityChangesLegacyRateButNotElapsedTimeWeightedPace()
    {
        using JsonDocument fixture = Fixture();
        QuotaSnapshot[] dense = Readings(fixture);
        DateTimeOffset jump = fixture.RootElement.GetProperty("forecasts")[0].GetProperty("capturedAtUtc").GetDateTimeOffset();
        QuotaSnapshot[] sparse = dense.Where(x => x.CapturedAtUtc <= jump).Append(dense[^1]).ToArray();
        Assert.Equal(dense[^1], sparse[^1]);
        Assert.All(dense.Where(x => x.CapturedAtUtc >= jump), x => Assert.Equal(45, x.UsedPercent));
        // Same endpoint, elapsed time and movement. Only extra unchanged polls differ.
        Assert.Equal(QuotaPaceModels.Estimate(sparse, "legacy-ewma") * Math.Pow(.75, dense.Length - sparse.Length),
            QuotaPaceModels.Estimate(dense, "legacy-ewma"), 10);
        Assert.True(QuotaPaceModels.Estimate(sparse, "legacy-ewma") > 50 * QuotaPaceModels.Estimate(dense, "legacy-ewma"));
        Assert.Equal(QuotaPaceModels.Estimate(sparse, "time-ewma-2h"), QuotaPaceModels.Estimate(dense, "time-ewma-2h"), 10);
        Assert.Equal(QuotaPaceModels.Estimate(sparse, "time-ewma-6h"), QuotaPaceModels.Estimate(dense, "time-ewma-6h"), 10);
        Assert.InRange(QuotaPaceModels.Estimate(dense, "time-ewma-2h"), 2.61, 2.62);
        Assert.InRange(QuotaPaceModels.Estimate(dense, "recent-2h"), 3.48, 3.49);
    }

    [Fact]
    public async Task OngoingLocalWorkWithFlatRoundedQuotaDoesNotMakeTheFallbackAnActivityOrCalibratedSurvivalEstimate()
    {
        using JsonDocument fixture = Fixture();
        QuotaSnapshot[] quota = Readings(fixture);
        DateTimeOffset jump = fixture.RootElement.GetProperty("forecasts")[0].GetProperty("capturedAtUtc").GetDateTimeOffset();
        // Locate the repository rather than keeping generated databases outside its temporary area.
        var parent = new DirectoryInfo(AppContext.BaseDirectory);
        while (parent is not null && !File.Exists(Path.Combine(parent.FullName, "TajsTokens.slnx"))) parent = parent.Parent;
        Assert.NotNull(parent);
        string directory = Path.Combine(parent.FullName, ".codex", "temp", "weekly-pace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            using var store = new SqliteCodexObservatoryStore(database);
            await store.InitializeAsync(default);
            foreach (QuotaSnapshot row in quota) await repository.UpsertQuotaSnapshotAsync(row, default);
            await store.UpsertSessionAsync(new CodexSession("ongoing", null, "fixture-project", jump, quota[^1].CapturedAtUtc, "active"), default);
            int count = 0;
            foreach (QuotaSnapshot row in quota.Where(x => x.CapturedAtUtc >= jump))
            {
                count++;
                await store.ApplyCumulativeTokenObservationAsync(new CodexCumulativeTokenObservation(
                    "work-" + count, "fixture.jsonl", "ongoing", null, row.CapturedAtUtc, "fixture-model", "high",
                    count * 2000, count * 500, 0, count * 200, count * 100, count * 2200), default);
            }
            var service = new SqliteIntelligenceService(database, repository);
            IntelligenceDashboard work = await service.QueryAsync(new IntelligenceQuery(jump, quota[^1].CapturedAtUtc.AddSeconds(1))
            { UsageOnly = true }, default);
            Assert.True(work.UsageHistory.Sum(x => x.NativeTokens) > 0);
            CurrentQuotaForecast forecast = Assert.Single(await service.BuildBackgroundForecastsAsync(
                [new QuotaLaneState(QuotaWindowKind.Weekly, "codex", "default", quota[^1], TelemetryHealthState.Live)],
                quota[^1].CapturedAtUtc, default));
            // This records the defect: a conditional legacy arithmetic result can look safe
            // despite ongoing work. It is not a validated idle or survival conclusion.
            Assert.Equal("legacy-ewma", forecast.Forecast!.Evidence!.Model);
            Assert.InRange(forecast.Forecast.BurnRatePercentPerHour!.Value, .10, .11);
            Assert.True(forecast.Forecast.SurvivesUntilReset);
            Assert.False(forecast.Forecast.IsQuantizedFlat);
            Assert.Equal(0, forecast.Forecast.Confidence);
            Assert.Equal(0, forecast.Forecast.Evidence.CalibrationEpochs);
            Assert.Null(forecast.Forecast.Evidence.HorizonPredictions);
            Assert.Null(forecast.Forecast.Evidence.NominalIntervalCoverage);

            Forecast entirelyFlat = QuotaPredictionService.BuildResetOutlook(
                quota.Select(x => x with { UsedPercent = 45 }).ToArray(), quota[^1].CapturedAtUtc);
            Assert.Equal(ForecastState.IdleWithinMeterPrecision, entirelyFlat.State);
            Assert.Null(entirelyFlat.BurnRatePercentPerHour);
            Assert.Null(entirelyFlat.SurvivesUntilReset);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
