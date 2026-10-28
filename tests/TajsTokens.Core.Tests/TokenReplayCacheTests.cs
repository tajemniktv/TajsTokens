using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TokenReplayCacheTests
{
    [Fact]
    public void PreparationReusesEarlierOriginsButRebuildsForSameCountReplacement()
    {
        CodexForecastDataset data = ComposedQuotaEvaluatorTests.TimelyData();
        var cache = new TokenWorkloadPredictionService.ReplayCache();
        foreach (int i in new[] { 50, 55, 60, 40 })
        {
            DateTimeOffset origin = data.Tokens[i].ObservedAtUtc;
            Assert.Equal(
                JsonSerializer.Serialize(TokenWorkloadPredictionService.PredictHorizon(data, origin, .5,
                    ForecastReplayAvailability.CollectedByOrigin)),
                JsonSerializer.Serialize(TokenWorkloadPredictionService.PredictHorizonCached(data, origin, .5,
                    ForecastReplayAvailability.CollectedByOrigin, CancellationToken.None, cache)));
        }
        Assert.Equal(1, cache.PreparationBuilds);
        int built = cache.FeatureBuilds;
        TokenWorkloadPredictionService.PredictHorizonCached(data, data.Tokens[50].ObservedAtUtc, .5,
            ForecastReplayAvailability.CollectedByOrigin, CancellationToken.None, cache);
        Assert.Equal(built, cache.FeatureBuilds);
        CodexForecastDataset changed = data with
        {
            Tokens = data.Tokens.Select(x => x with { Model = "changed", ReportedTotalTokens = 20000,
                UncachedInputTokens = 20000 }).ToArray(),
        };
        DateTimeOffset at = changed.Tokens[60].ObservedAtUtc;
        Assert.Equal(
            JsonSerializer.Serialize(TokenWorkloadPredictionService.PredictHorizon(changed, at, .5,
                ForecastReplayAvailability.CollectedByOrigin)),
            JsonSerializer.Serialize(TokenWorkloadPredictionService.PredictHorizonCached(changed, at, .5,
                ForecastReplayAvailability.CollectedByOrigin, CancellationToken.None, cache)));
        Assert.Equal(2, cache.PreparationBuilds);
    }

    [Fact]
    public void SharedReplayMatchesIndependentOriginsWhenLateBackfillsChangeTraining()
    {
        CodexForecastDataset original = ComposedQuotaEvaluatorTests.TimelyData();
        CodexForecastDataset data = original with
        {
            Tokens = original.Tokens.Select((x, i) => x with
            {
                CapturedAtUtc = i % 7 == 0 ? x.ObservedAtUtc.AddHours(6) : i % 11 == 0 ? null : x.CapturedAtUtc,
                ReportedTotalTokens = 10000 + i % 5 * 500,
                UncachedInputTokens = 10000 + i % 5 * 500,
            }).Reverse().ToArray(),
            Workload =
            [
                new("meta", "fixture", "fixture", 0, 1, "s", "session_meta", original.Tokens[0].ObservedAtUtc,
                    original.Tokens[45].ObservedAtUtc, null, null, null, "m", "high", null, SessionSourceKind: "cli"),
            ],
            Context =
            [
                new("context", "s", null, original.Tokens[30].ObservedAtUtc, "m", 80, 100, true,
                    CapturedAtUtc: original.Tokens[50].ObservedAtUtc),
            ],
        };
        var cache = new TokenWorkloadPredictionService.ReplayCache();
        foreach (ForecastReplayAvailability availability in Enum.GetValues<ForecastReplayAvailability>())
        foreach (int i in new[] { 35, 40, 45, 50, 55, 60, 40 })
        foreach (double horizon in new[] { .5, 2d })
        {
            DateTimeOffset origin = data.Tokens[i].ObservedAtUtc;
            TokenHorizonPrediction? expected = TokenWorkloadPredictionService.PredictHorizon(data, origin, horizon, availability);
            TokenHorizonPrediction? actual = TokenWorkloadPredictionService.PredictHorizonCached(data, origin, horizon,
                availability, CancellationToken.None, cache);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        }
    }
}
