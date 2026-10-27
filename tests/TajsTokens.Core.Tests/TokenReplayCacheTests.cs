using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TokenReplayCacheTests
{
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
            }).ToArray(),
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
