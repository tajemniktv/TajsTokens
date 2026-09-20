using TajsTokens.Core.Research;
using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void ExactTargetsRecoverIrregularPollingComparisonsWithoutChangingTrainingOrReadingFutureWork()
    {
        var original = ComposedQuotaEvaluatorTests.TimelyData();
        var start = original.Quota[0].CapturedAtUtc;
        DateTimeOffset At(int i) => start.AddMinutes(i * 5 + (i % 11 == 0 ? 2 : 0));
        var data = original with {
            Quota = Enumerable.Range(0, 200).Select(i => original.Quota[0] with {
                CapturedAtUtc = At(i), CollectedAtUtc = At(i), UsedPercent = i / 3d }).ToArray(),
            Tokens = Enumerable.Range(0, 200).Select(i => original.Tokens[0] with {
                ObservedAtUtc = At(i), CapturedAtUtc = At(i) }).ToArray()
        };
        const double horizon = 5d / 60;
        const ForecastReplayAvailability strict = ForecastReplayAvailability.CollectedByOrigin;
        var targets = QuotaCostObservationBuilder.Build(data, horizons: [horizon]).Skip(20)
            .Select(x => (Origin: x.StartUtc, Outcome: x.EndUtc)).ToArray();
        var ordinary = QuotaPredictionService.Replay(data, horizon, strict);
        var extra = targets.First(x => ordinary.All(y => y.Observation.OriginUtc != x.Origin));
        var matched = QuotaPredictionService.ReplayAtTargets(data, horizon, strict, targets);
        Assert.Equal(targets.Length, matched.Count);
        Assert.All(targets, target => Assert.Equal(target.Outcome, matched[target.Origin].TargetUtc));
        var alone = QuotaPredictionService.ReplayAtTargets(data, horizon, strict, [extra]);
        Assert.Equal(matched[extra.Origin], alone[extra.Origin]); // Other requested targets never become training points.
        foreach (var sampled in ordinary.Where(x => matched.ContainsKey(x.Observation.OriginUtc)))
            Assert.Equal(sampled.Prediction with { TargetUtc = sampled.Observation.OutcomeUtc }, matched[sampled.Observation.OriginUtc]);
        var futureChanged = data with { Tokens = data.Tokens.Select(x => x.ObservedAtUtc > extra.Origin ? x with {
            Model = "future-model", ReportedTotalTokens = 999999999 } : x).ToArray() };
        Assert.Equal(alone[extra.Origin], QuotaPredictionService.ReplayAtTargets(futureChanged, horizon, strict, [extra])[extra.Origin]);
        Assert.Empty(QuotaPredictionService.ReplayAtTargets(data, horizon, strict,
            [(extra.Origin, extra.Outcome.AddDays(7)), (start.AddSeconds(1), extra.Outcome)]));
        var composed = ComposedQuotaEvaluator.Evaluate(data, availability: strict, horizons: [horizon]);
        Assert.All(composed.Scores.Single(x => x.CostModel == "total").Trials, x => Assert.NotNull(x.IncumbentIntervalLoss));
    }

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
            Assert.Equal(x.RemainingPercent, x.Inference!.Reconstruct(), 8);
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
        Assert.All(before, x => Assert.Equal(x.RemainingPercent, x.Inference!.Reconstruct(), 8));
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
    public void WorkloadTrainingRequiresNonOverlappingOutcomes()
    {
        var quota = Enumerable.Range(0, 120).Select(i => Point(i * 0.5, i * 0.5)).ToArray();
        var trials = QuotaForecastCalibration.Replay(quota, "time-ewma-2h", 24);
        Assert.True(trials.Count > 12);
        var workload = QuotaWorkloadBacktester.Replay(Data(quota), QuotaWindowKind.Weekly, "pace-ridge", 24,
            ForecastReplayAvailability.ReconstructedEventTime, baselineModel: "time-ewma-2h");
        Assert.NotEmpty(workload);
        Assert.All(workload, trial => { Assert.True(trial.TrainingSamples < 12); Assert.False(trial.UsedWorkloadModel); });
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
        Start.AddHours(hours), used, 10080, Start.AddDays(7), "codex", "default", "codex-app-server:account/rateLimits", "fixture-account") { HasSourceTimestamp = true };

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
