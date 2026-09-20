// Taj's Tokens | QuotaPredictionServiceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class QuotaPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

    [Fact]
    public void ExactTargetsRecoverIrregularPollingComparisonsWithoutChangingTrainingOrReadingFutureWork()
    {
        CodexForecastDataset original = ComposedQuotaEvaluatorTests.TimelyData();
        DateTimeOffset start = original.Quota[0].CapturedAtUtc;

        DateTimeOffset At(int i)
        {
            return start.AddMinutes(i * 5 + (i % 11 == 0 ? 2 : 0));
        }

        CodexForecastDataset data = original with
        {
            Quota =
            Enumerable.Range(0, 200)
                .Select(i => original.Quota[0] with { CapturedAtUtc = At(i), CollectedAtUtc = At(i), UsedPercent = i / 3d }).ToArray(),
            Tokens = Enumerable.Range(0, 200).Select(i => original.Tokens[0] with { ObservedAtUtc = At(i), CapturedAtUtc = At(i) })
                .ToArray(),
        };
        const double horizon = 5d / 60;
        const ForecastReplayAvailability strict = ForecastReplayAvailability.CollectedByOrigin;
        (DateTimeOffset Origin, DateTimeOffset Outcome)[] targets = QuotaCostObservationBuilder.Build(data, horizons: [horizon]).Skip(20)
            .Select(x => (Origin: x.StartUtc, Outcome: x.EndUtc)).ToArray();
        IReadOnlyList<QuotaPredictionTrial> ordinary = QuotaPredictionService.Replay(data, horizon, strict);
        (DateTimeOffset Origin, DateTimeOffset Outcome) extra = targets.First(x => ordinary.All(y => y.Observation.OriginUtc != x.Origin));
        IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> matched = QuotaPredictionService.ReplayAtTargets(
            data,
            horizon,
            strict,
            targets);
        Assert.Equal(targets.Length, matched.Count);
        Assert.All(targets, target => Assert.Equal(target.Outcome, matched[target.Origin].TargetUtc));
        IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> alone = QuotaPredictionService.ReplayAtTargets(
            data,
            horizon,
            strict,
            [extra]);
        Assert.Equal(matched[extra.Origin], alone[extra.Origin]); // Other requested targets never become training points.
        foreach (QuotaPredictionTrial sampled in ordinary.Where(x => matched.ContainsKey(x.Observation.OriginUtc)))
            Assert.Equal(sampled.Prediction with { TargetUtc = sampled.Observation.OutcomeUtc }, matched[sampled.Observation.OriginUtc]);
        CodexForecastDataset futureChanged = data with
        {
            Tokens = data.Tokens.Select(x =>
                x.ObservedAtUtc > extra.Origin ? x with { Model = "future-model", ReportedTotalTokens = 999999999 } : x).ToArray(),
        };
        Assert.Equal(alone[extra.Origin], QuotaPredictionService.ReplayAtTargets(futureChanged, horizon, strict, [extra])[extra.Origin]);
        Assert.Empty(
            QuotaPredictionService.ReplayAtTargets(
                data,
                horizon,
                strict,
                [(extra.Origin, extra.Outcome.AddDays(7)), (start.AddSeconds(1), extra.Outcome)]));
        ComposedQuotaEvaluation composed = ComposedQuotaEvaluator.Evaluate(data, availability: strict, horizons: [horizon]);
        Assert.All(composed.Scores.Single(x => x.CostModel == "total").Trials, x => Assert.NotNull(x.IncumbentIntervalLoss));
    }

    [Fact]
    public void TimeWeightedRatePreservesLinearSlopeAndDoesNotDecayPerFlatPoll()
    {
        QuotaSnapshot[] sparse = new[] { Point(0, 0), Point(1, 2), Point(2, 4) };
        QuotaSnapshot[] dense = Enumerable.Range(0, 121).Select(i => Point(i / 60d, i / 30d)).ToArray();
        Assert.Equal(2, QuotaPaceModels.Estimate(sparse, "time-ewma-2h"), 8);
        Assert.Equal(2, QuotaPaceModels.Estimate(dense, "time-ewma-2h"), 8);
        QuotaSnapshot[] flat = new[] { Point(0, 0), Point(1, 2), Point(2, 2) };
        QuotaSnapshot[] extraPolling = flat.Concat(Enumerable.Range(1, 59).Select(i => Point(1 + i / 60d, 2)))
            .OrderBy(x => x.CapturedAtUtc).ToArray();
        Assert.Equal(
            QuotaPaceModels.Estimate(flat, "time-ewma-2h"),
            QuotaPaceModels.Estimate(extraPolling, "time-ewma-2h"),
            8);
    }

    [Fact]
    public void SparseFlatHistoryReportsLearningNotCalibratedZeroBurn()
    {
        CodexForecastDataset data = Data([Point(0, 20), Point(0.5, 20)]);
        IReadOnlyList<QuotaHorizonPrediction> prediction = QuotaPredictionService.Predict(
            data,
            data.Quota[^1],
            data.Quota[^1].CapturedAtUtc);
        Assert.NotEmpty(prediction);
        Assert.All(
            prediction,
            x =>
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
        CodexForecastDataset data = Episodes(32);
        QuotaSnapshot anchor = data.Quota[^2];
        IReadOnlyList<QuotaHorizonPrediction> before = QuotaPredictionService.Predict(data, anchor, anchor.CapturedAtUtc);
        Assert.All(before, x => Assert.Equal(x.RemainingPercent, x.Inference!.Reconstruct(), 8));
        CodexForecastDataset polluted = data with
        {
            Quota = data.Quota.Append(Point(0.1, 90) with { AccountKey = "other" })
                .Append(Point(500, 90)).ToArray(),
            Tokens = data.Tokens.Append(
                data.Tokens[^1] with
                {
                    ObservedAtUtc = anchor.CapturedAtUtc.AddMinutes(1),
                    CapturedAtUtc = anchor.CapturedAtUtc.AddMinutes(1),
                    Model = "future-model",
                    ReportedTotalTokens = 999999999,
                }).ToArray(),
        };
        Assert.Equal(
            JsonSerializer.Serialize(before),
            JsonSerializer.Serialize(
                QuotaPredictionService.Predict(polluted, anchor, anchor.CapturedAtUtc)));
        Assert.Throws<ArgumentException>(() => QuotaPredictionService.Replay(polluted, 0.5, ForecastReplayAvailability.CollectedByOrigin));
    }

    [Fact]
    public void WorkloadCorrectionEarnsSelectionAndLiveMatchesReplay()
    {
        CodexForecastDataset data = Episodes(70);
        IReadOnlyList<QuotaPredictionTrial> trials = QuotaPredictionService.Replay(data, 0.5, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Contains(trials, x => x.Prediction.UsesWorkload);
        QuotaPredictionTrial last = trials[^1];
        QuotaSnapshot anchor = data.Quota.Single(x => x.CapturedAtUtc == last.Observation.OriginUtc);
        QuotaHorizonPrediction live = QuotaPredictionService.Predict(data, anchor, anchor.CapturedAtUtc).Single(x => x.HorizonHours == 0.5);
        Assert.Equal(last.Prediction.RemainingPercent, live.RemainingPercent, 8);
        Assert.Equal(last.Prediction.Model, live.Model);
        Assert.True(live.UsesWorkload);
        Assert.True(Math.Abs(live.RemainingPercent - last.Observation.ObservedRemaining) < 1);
        CodexForecastDataset unseen = data with
        {
            Tokens = data.Tokens.Select(x => x == data.Tokens[^1] ? x with { Model = "unseen" } : x).ToArray(),
        };
        Assert.False(QuotaPredictionService.Predict(unseen, anchor, anchor.CapturedAtUtc).First().UsesWorkload);
    }

    [Fact]
    public void BackfilledEvidenceCannotTrainStrictHistoricalPredictions()
    {
        CodexForecastDataset data = Episodes(40);
        data = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = Start.AddDays(100) }).ToArray() };
        Assert.All(
            QuotaPredictionService.Replay(data, 0.5, ForecastReplayAvailability.CollectedByOrigin),
            x => Assert.False(x.Prediction.UsesWorkload));
        Assert.Contains(
            QuotaPredictionService.Replay(data, 0.5, ForecastReplayAvailability.ReconstructedEventTime),
            x => x.Prediction.UsesWorkload);
    }

    [Fact]
    public void WorkloadTrainingRequiresNonOverlappingOutcomes()
    {
        QuotaSnapshot[] quota = Enumerable.Range(0, 120).Select(i => Point(i * 0.5, i * 0.5)).ToArray();
        IReadOnlyList<QuotaForecastTrial> trials = QuotaForecastCalibration.Replay(quota, "time-ewma-2h", 24);
        Assert.True(trials.Count > 12);
        IReadOnlyList<QuotaWorkloadTrial> workload = QuotaWorkloadBacktester.Replay(
            Data(quota),
            QuotaWindowKind.Weekly,
            "pace-ridge",
            24,
            ForecastReplayAvailability.ReconstructedEventTime,
            baselineModel: "time-ewma-2h");
        Assert.NotEmpty(workload);
        Assert.All(
            workload,
            trial =>
            {
                Assert.True(trial.TrainingSamples < 12);
                Assert.False(trial.UsedWorkloadModel);
            });
    }

    [Fact]
    public void LegacyEvidenceJsonRemainsReadable()
    {
        var evidence = JsonSerializer.Deserialize<ForecastEvidence>(
            """
            {"PolicyVersion":"old","Model":"legacy-ewma","HistorySource":"fixture","ObservationCount":2,
            "ObservedHours":1,"CalibrationEpochs":0,"UncertaintyDescription":"learning"}
            """);
        Assert.NotNull(evidence);
        Assert.Null(evidence.HorizonPredictions);
    }

    private static QuotaSnapshot Point(double hours, double used)
    {
        return new QuotaSnapshot(
            QuotaWindowKind.Weekly,
            Start.AddHours(hours),
            used,
            10080,
            Start.AddDays(7),
            "codex",
            "default",
            "codex-app-server:account/rateLimits",
            "fixture-account") { HasSourceTimestamp = true };
    }

    private static CodexForecastDataset Data(IReadOnlyList<QuotaSnapshot> quota)
    {
        return new CodexForecastDataset(quota, [], [], [], Start, "synthetic");
    }

    private static CodexForecastDataset Episodes(int count)
    {
        var quota = new List<QuotaSnapshot>();
        var tokens = new List<CodexPredictiveTokenEvent>();
        for (int i = 0; i < count; i++)
        {
            int hours = i * 4;
            bool high = i % 2 == 0;
            DateTimeOffset reset = Start.AddHours(hours + 3);
            quota.Add(Point(hours, 0) with { ResetsAtUtc = reset });
            quota.Add(Point(hours + 0.5, 10) with { ResetsAtUtc = reset });
            quota.Add(Point(hours + 1, high ? 18 : 11) with { ResetsAtUtc = reset });
            DateTimeOffset at = Start.AddHours(hours + 0.25);
            tokens.Add(
                new CodexPredictiveTokenEvent(
                    "session",
                    at,
                    at,
                    high ? "model-b" : "model-a",
                    high ? "high" : "low",
                    high ? 10000 : 100,
                    0,
                    0,
                    0,
                    0,
                    high ? 10000 : 100));
        }
        return new CodexForecastDataset(
            quota,
            [],
            tokens,
            [],
            quota[^1].CapturedAtUtc,
            "synthetic known workload relationship, not empirical accuracy");
    }
}