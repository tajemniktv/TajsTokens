using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TokenCurrentPredictionTests
{
    [Theory]
    [InlineData(.5, false)]
    [InlineData(.5, true)]
    [InlineData(.5013888888888889, false)]
    [InlineData(.5013888888888889, true)]
    [InlineData(.5000000000277778, false)]
    [InlineData(.5000000000277778, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(2.0125, false)]
    [InlineData(2.0125, true)]
    [InlineData(2.0000000000277778, false)]
    [InlineData(2.0000000000277778, true)]
    public void CurrentAndCachedPredictionMatchExhaustiveSelectionAndBands(double horizon, bool collected)
    {
        var data = Data(late: true);
        var availability = collected ? ForecastReplayAvailability.CollectedByOrigin : ForecastReplayAvailability.ReconstructedEventTime;
        var cache = new TokenWorkloadPredictionService.ReplayCache();
        foreach (int index in new[] { 3500, 4700, 4000 })
        {
            DateTimeOffset origin = data.Tokens[index].ObservedAtUtc.AddMinutes(2);
            var expected = TokenWorkloadPredictionService.PredictHorizonExhaustive(data, origin, horizon, availability);
            Assert.NotNull(expected);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(
                TokenWorkloadPredictionService.PredictHorizon(data, origin, horizon, availability)));
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(
                TokenWorkloadPredictionService.PredictHorizonCached(data, origin, horizon, availability, default, cache)));
        }
    }

    [Fact]
    public void SparseGapsAndShortHistoryKeepTheExhaustiveAnswer()
    {
        var data = Data(late: false);
        foreach (int count in new[] { 12, 300, 4801 })
        {
            var selected = data with { Tokens = data.Tokens.Take(count).Where((_, i) => i < 150 || i > count - 180).ToArray() };
            DateTimeOffset origin = selected.Tokens[^1].ObservedAtUtc.AddMinutes(2);
            foreach (double horizon in new[] { .5, 2.0125 })
                Assert.Equal(JsonSerializer.Serialize(TokenWorkloadPredictionService.PredictHorizonExhaustive(selected, origin, horizon,
                        ForecastReplayAvailability.ReconstructedEventTime)),
                    JsonSerializer.Serialize(TokenWorkloadPredictionService.PredictHorizon(selected, origin, horizon)));
        }
    }

    [Fact]
    public void OneCurrentPredictionFitsOnlyItsDependencyWindows()
    {
        var data = Data(late: false);
        DateTimeOffset origin = data.Tokens[^1].ObservedAtUtc.AddMinutes(2);
        var full = new FitCounter();
        TokenHorizonPrediction? expected;
        using (RidgeFitCacheScope.Use(full))
            expected = TokenWorkloadPredictionService.PredictHorizonExhaustive(data, origin, .5,
                ForecastReplayAvailability.ReconstructedEventTime);
        var bounded = new FitCounter();
        TokenHorizonPrediction? actual;
        using (RidgeFitCacheScope.Use(bounded))
            actual = TokenWorkloadPredictionService.PredictHorizon(data, origin, .5);
        Assert.NotNull(actual);
        Assert.NotNull(actual.LowerTokens);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.InRange(bounded.Fits, 1, 194); // Two candidates over two 48-point dependency windows plus current.
        Assert.True(full.Fits > bounded.Fits * 3, $"Exhaustive {full.Fits}; current {bounded.Fits}");
    }

    private sealed class FitCounter : IRidgeFitCache
    {
        public int Fits { get; private set; }
        public RidgeFitParameters? Read(string key) => null;
        public void Write(string key, RidgeFitParameters fit) => Fits++;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdjacentCurrentQueriesReuseCandidateEstimatesWithoutReusingWarmupSelections(bool unrelatedLateEvidence)
    {
        var data = Data(late: false);
        DateTimeOffset origin = data.Tokens[^1].ObservedAtUtc.AddMinutes(2);
        if (unrelatedLateEvidence)
            data = data with { Context = data.Context.Append(new CodexContextObservation("old-context", "s", null,
                data.Tokens[0].ObservedAtUtc, "m0", 10, 100, false, CapturedAtUtc: origin.AddMinutes(3))).ToArray() };
        var expected = TokenWorkloadPredictionService.PredictHorizonExhaustive(data, origin.AddMinutes(5), .5,
            ForecastReplayAvailability.CollectedByOrigin);
        var cache = new TokenWorkloadPredictionService.ReplayCache();
        var counter = new FitCounter();
        using (RidgeFitCacheScope.Use(counter))
        {
            TokenWorkloadPredictionService.PredictHorizonCached(data, origin, .5,
                ForecastReplayAvailability.CollectedByOrigin, default, cache);
            int first = counter.Fits;
            Assert.True(first > 100);
            var actual = TokenWorkloadPredictionService.PredictHorizonCached(data, origin.AddMinutes(5), .5,
                ForecastReplayAvailability.CollectedByOrigin, default, cache);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            Assert.InRange(counter.Fits - first, 0, 4);
        }
    }

    private static CodexForecastDataset Data(bool late)
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        var tokens = Enumerable.Range(0, 4801).Select(i =>
        {
            DateTimeOffset at = start.AddMinutes(i * 5);
            DateTimeOffset? captured = late && i % 19 == 0 ? null : late && i % 7 == 0 ? at.AddHours(6) : at;
            long amount = i % 43 < 4 ? 30000 : 1000 + i % 11 * 900;
            return new CodexPredictiveTokenEvent("s", at, captured, "m" + i / 600 % 3,
                i % 13 == 0 ? "low" : "high", amount, 0, 0, 0, 0, amount);
        }).ToArray();
        return new([], [new("meta", "fixture", "fixture", 0, 1, "s", "session_meta", start, start,
            null, null, null, "m0", "high", null, SessionSourceKind: "cli")], tokens,
            [new("context", "s", null, start.AddHours(4), "m0", 80, 100, true,
                CapturedAtUtc: start.AddHours(late ? 12 : 4))], start.AddDays(18), "fixture");
    }
}
