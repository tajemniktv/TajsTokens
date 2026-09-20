// Taj's Tokens | SessionWorkloadPredictionServiceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class SessionWorkloadPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    [Fact]
    public void ConditionalMeanIsSeparatedFromActivityAndDoesNotScaleRangeEndpoints()
    {
        CodexForecastDataset data = History(4000, true);
        SessionWorkloadReport report = SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc);
        Assert.NotEmpty(report.Trials);
        Assert.Contains(report.Trials, x => x.ObservedTokens == 0);
        Assert.Contains(report.Trials, x => x.ObservedTokens > 0);
        foreach (SessionWorkloadTrial trial in report.Trials.Where(x => x.Prediction.ConditionalMeanTokens is not null))
        {
            SessionWorkloadPrediction p = trial.Prediction;
            Assert.Equal(p.RecordedActivityProbability * p.ConditionalMeanTokens, p.ExpectedTokens);
            Assert.InRange(p.RecordedActivityProbability, 0, 1);
            Assert.True(p.ConditionalLowTokens > 0);
            Assert.True(p.ConditionalHighTokens >= p.ConditionalLowTokens);
            Assert.True(p.ActiveTrainingOrigins >= 8);
            Assert.True(p.ActiveTrainingOrigins <= p.TrainingOrigins);
        }
        Assert.All(
            report.Scores,
            x =>
            {
                Assert.True(x.ConditionalOrigins <= x.ActiveOrigins);
                Assert.Equal(
                    report.Trials.Count(t => t.Prediction.HorizonHours == x.HorizonHours && t.Prediction.ExpectedTokens is not null),
                    x.ExpectedOrigins);
            });
    }

    [Fact]
    public void FutureOutcomesCannotChangeEarlierTrialsOrCurrentPrediction()
    {
        CodexForecastDataset first = History(2000, true);
        CodexForecastDataset later = History(3000, true);
        SessionWorkloadReport before = SessionWorkloadPredictionService.Evaluate(first, first.CapturedAtUtc);
        SessionWorkloadReport after = SessionWorkloadPredictionService.Evaluate(later, later.CapturedAtUtc);
        foreach (SessionWorkloadTrial trial in before.Trials)
        {
            Assert.Equal(
                JsonSerializer.Serialize(trial),
                JsonSerializer.Serialize(
                    after.Trials.Single(x =>
                        x.OriginUtc == trial.OriginUtc && x.Prediction.HorizonHours == trial.Prediction.HorizonHours)));
        }
        Assert.Equal(
            JsonSerializer.Serialize(before),
            JsonSerializer.Serialize(
                SessionWorkloadPredictionService.Evaluate(later, first.CapturedAtUtc)));
    }

    [Fact]
    public void SparseLateCollectedAndIdleEvidenceCannotPublishCurrentOutlooks()
    {
        CodexForecastDataset data = History(4000, false);
        Assert.Empty(SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc.AddMinutes(10)).Current);
        CodexForecastDataset late = data with
        {
            Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray(),
        };
        Assert.Empty(SessionWorkloadPredictionService.Evaluate(late, data.CapturedAtUtc).Current);
        CodexForecastDataset sparse = History(300, false);
        Assert.Empty(SessionWorkloadPredictionService.Evaluate(sparse, sparse.CapturedAtUtc).Current);
    }

    [Fact]
    public void ActivityEstimateNeedsEarlierHeldOutEvidenceInTheSameAgeGroup()
    {
        CodexForecastDataset data = History(4000, false);
        SessionWorkloadReport report = SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc);
        Assert.Contains(report.Trials, x => !x.Prediction.ActivityEstimateSupported);
        Assert.Contains(report.Trials, x => x.Prediction.ActivityEstimateSupported);
        foreach (SessionWorkloadTrial trial in report.Trials.Where(x => x.Prediction.ActivityEstimateSupported))
        {
            Assert.True(
                report.Trials.Count(x => x.EndUtc <= trial.OriginUtc && x.AgeGroup == trial.AgeGroup &&
                                         x.Prediction.HorizonHours == trial.Prediction.HorizonHours) >= 64);
        }
        Assert.All(report.Trials, x => Assert.True(x.EndUtc > x.OriginUtc));
        foreach (IGrouping<double, SessionWorkloadTrial> group in report.Trials.GroupBy(x => x.Prediction.HorizonHours))
        {
            SessionWorkloadTrial[] rows = group.OrderBy(x => x.OriginUtc).ToArray();
            for (int i = 1; i < rows.Length; i++) Assert.True(rows[i - 1].EndUtc <= rows[i].OriginUtc);
        }
    }

    [Fact]
    public void CompletedQuietTailIsScoredWithoutWaitingForAnotherToken()
    {
        CodexForecastDataset active = History(4000, false);
        DateTimeOffset lastToken = active.Tokens[^1].ObservedAtUtc;
        DateTimeOffset captured = lastToken.AddHours(3);
        CodexForecastDataset stopped = active with { CapturedAtUtc = captured };
        SessionWorkloadReport report = SessionWorkloadPredictionService.Evaluate(stopped, captured);
        foreach (double horizon in new[] { .5, 1d })
        {
            SessionWorkloadTrial[] tail = report.Trials.Where(x => x.OriginUtc >= lastToken && x.Prediction.HorizonHours == horizon)
                .ToArray();
            Assert.NotEmpty(tail);
            Assert.All(
                tail,
                x =>
                {
                    Assert.Equal(0, x.ObservedTokens);
                    Assert.True(x.EndUtc <= captured);
                    Assert.True(x.OriginUtc < lastToken.AddHours(2));
                });
        }
        Assert.Empty(report.Current);
        // Wall-clock passage cannot turn an old snapshot into new negative labels.
        SessionWorkloadReport stale = SessionWorkloadPredictionService.Evaluate(stopped, captured.AddDays(1));
        Assert.Equal(JsonSerializer.Serialize(report.Trials), JsonSerializer.Serialize(stale.Trials));
        // Neither an incomplete target nor a token beyond the snapshot is evidence.
        CodexPredictiveTokenEvent future = active.Tokens[^1] with
        {
            ObservedAtUtc = captured.AddMinutes(5), CapturedAtUtc = captured.AddMinutes(5),
        };
        SessionWorkloadReport appended = SessionWorkloadPredictionService.Evaluate(
            stopped with { Tokens = stopped.Tokens.Append(future).ToArray() },
            captured.AddHours(1));
        Assert.Equal(JsonSerializer.Serialize(report.Trials), JsonSerializer.Serialize(appended.Trials));
        Assert.All(report.Trials, x => Assert.True(x.EndUtc <= captured));
    }

    private static CodexForecastDataset History(int minutes, bool bursty)
    {
        CodexPredictiveTokenEvent[] tokens = Enumerable.Range(0, minutes / 5 + 1).Where(i => !bursty || i % 20 < 10)
            .Select(i => new CodexPredictiveTokenEvent(
                "session",
                Start.AddMinutes(i * 5),
                Start.AddMinutes(i * 5),
                "model",
                "effort",
                100,
                0,
                0,
                0,
                0,
                100)).ToArray();
        return new CodexForecastDataset(
            [],
            [],
            tokens,
            [],
            Start.AddMinutes(minutes),
            "Synthetic recorded activity; not real-world calibration proof.");
    }
}