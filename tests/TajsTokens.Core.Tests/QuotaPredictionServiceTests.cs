using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void TimeWeightedRatePreservesLinearSlopeAndDoesNotDecayPerFlatPoll()
    {
        var sparse = new[] { Point(0, 0), Point(1, 2), Point(2, 4) };
        var dense = Enumerable.Range(0, 121).Select(i => Point(i / 60d, i / 30d)).ToArray();
        Assert.Equal(2, QuotaPaceModels.Estimate(sparse, "time-ewma-2h"), 8);
        Assert.Equal(2, QuotaPaceModels.Estimate(dense, "time-ewma-2h"), 8);
        var flat = new[] { Point(0, 0), Point(1, 2), Point(2, 2) };
        var extraPolling = flat.Concat(Enumerable.Range(1, 59).Select(i => Point(1 + i / 60d, 2)))
            .OrderBy(x => x.CapturedAtUtc).ToArray();
        Assert.Equal(QuotaPaceModels.Estimate(flat, "time-ewma-2h"),
            QuotaPaceModels.Estimate(extraPolling, "time-ewma-2h"), 8);
    }

    [Fact]
    public void SparseFlatHistoryReportsLearningNotCalibratedZeroBurn()
    {
        var data = Data([Point(0, 20), Point(0.5, 20)]);
        var prediction = QuotaPredictionService.Predict(data, data.Quota[^1], data.Quota[^1].CapturedAtUtc);
        Assert.NotEmpty(prediction);
        Assert.All(prediction, x =>
        {
            Assert.False(x.UsesWorkload);
            Assert.Null(x.LowerRemainingPercent);
            Assert.Contains("Uncertainty is learning", x.Explanation);
        });
        Assert.Empty(QuotaPredictionService.Predict(data, data.Quota[^1], Start.AddDays(1)));
    }

    [Fact]
    public void FutureDataAndOtherAccountsCannotChangeLivePrediction()
    {
        var data = Episodes(32);
        var anchor = data.Quota[^2];
        var before = QuotaPredictionService.Predict(data, anchor, anchor.CapturedAtUtc);
        var polluted = data with
        {
            Quota = data.Quota.Append(Point(0.1, 90) with { AccountKey = "other" })
                .Append(Point(500, 90)).ToArray(),
            Tokens = data.Tokens.Append(data.Tokens[^1] with
            { ObservedAtUtc = anchor.CapturedAtUtc.AddMinutes(1), CapturedAtUtc = anchor.CapturedAtUtc.AddMinutes(1),
                Model = "future-model", ReportedTotalTokens = 999999999 }).ToArray()
        };
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(
            QuotaPredictionService.Predict(polluted, anchor, anchor.CapturedAtUtc)));
        Assert.Throws<ArgumentException>(() => QuotaPredictionService.Replay(polluted, 0.5, ForecastReplayAvailability.CollectedByOrigin));
    }

    [Fact]
    public void WorkloadCorrectionEarnsSelectionAndLiveMatchesReplay()
    {
        var data = Episodes(70);
        var trials = QuotaPredictionService.Replay(data, 0.5, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Contains(trials, x => x.Prediction.UsesWorkload);
        var last = trials[^1];
        var anchor = data.Quota.Single(x => x.CapturedAtUtc == last.Observation.OriginUtc);
        var live = QuotaPredictionService.Predict(data, anchor, anchor.CapturedAtUtc).Single(x => x.HorizonHours == 0.5);
        Assert.Equal(last.Prediction.RemainingPercent, live.RemainingPercent, 8);
        Assert.Equal(last.Prediction.Model, live.Model);
        Assert.True(live.UsesWorkload);
        Assert.True(Math.Abs(live.RemainingPercent - last.Observation.ObservedRemaining) < 1);
        var unseen = data with { Tokens = data.Tokens.Select(x => x == data.Tokens[^1] ? x with { Model = "unseen" } : x).ToArray() };
        Assert.False(QuotaPredictionService.Predict(unseen, anchor, anchor.CapturedAtUtc).First().UsesWorkload);
    }

    [Fact]
    public void BackfilledEvidenceCannotTrainStrictHistoricalPredictions()
    {
        var data = Episodes(40);
        data = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = Start.AddDays(100) }).ToArray() };
        Assert.All(QuotaPredictionService.Replay(data, 0.5, ForecastReplayAvailability.CollectedByOrigin),
            x => Assert.False(x.Prediction.UsesWorkload));
        Assert.Contains(QuotaPredictionService.Replay(data, 0.5, ForecastReplayAvailability.ReconstructedEventTime),
            x => x.Prediction.UsesWorkload);
    }

    [Fact]
    public void LegacyEvidenceJsonRemainsReadable()
    {
        var evidence = JsonSerializer.Deserialize<ForecastEvidence>("""
            {"PolicyVersion":"old","Model":"legacy-ewma","HistorySource":"fixture","ObservationCount":2,
            "ObservedHours":1,"CalibrationEpochs":0,"UncertaintyDescription":"learning"}
            """);
        Assert.NotNull(evidence);
        Assert.Null(evidence.HorizonPredictions);
    }

    private static QuotaSnapshot Point(double hours, double used) => new(QuotaWindowKind.Weekly,
        Start.AddHours(hours), used, 10080, Start.AddDays(7), "codex", "default", "codex-app-server:account/rateLimits", "fixture-account");

    private static CodexForecastDataset Data(IReadOnlyList<QuotaSnapshot> quota) => new(quota, [], [], [], Start, "synthetic");

    private static CodexForecastDataset Episodes(int count)
    {
        var quota = new List<QuotaSnapshot>();
        var tokens = new List<CodexPredictiveTokenEvent>();
        for (var i = 0; i < count; i++)
        {
            var hours = i * 4;
            var high = i % 2 == 0;
            var reset = Start.AddHours(hours + 3);
            quota.Add(Point(hours, 0) with { ResetsAtUtc = reset });
            quota.Add(Point(hours + 0.5, 10) with { ResetsAtUtc = reset });
            quota.Add(Point(hours + 1, high ? 18 : 11) with { ResetsAtUtc = reset });
            var at = Start.AddHours(hours + 0.25);
            tokens.Add(new("session", at, at, high ? "model-b" : "model-a", high ? "high" : "low",
                high ? 10000 : 100, 0, 0, 0, 0, high ? 10000 : 100));
        }
        return new(quota, [], tokens, [], quota[^1].CapturedAtUtc, "synthetic known workload relationship, not empirical accuracy");
    }
}
