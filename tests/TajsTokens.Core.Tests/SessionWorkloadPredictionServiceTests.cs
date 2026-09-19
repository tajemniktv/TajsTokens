using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class SessionWorkloadPredictionServiceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    [Fact]
    public void ConditionalMeanIsSeparatedFromActivityAndDoesNotScaleRangeEndpoints()
    {
        var data = History(4000, bursty: true);
        var report = SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc);
        Assert.NotEmpty(report.Trials);
        Assert.Contains(report.Trials, x => x.ObservedTokens == 0);
        Assert.Contains(report.Trials, x => x.ObservedTokens > 0);
        foreach (var trial in report.Trials.Where(x => x.Prediction.ConditionalMeanTokens is not null))
        {
            var p = trial.Prediction;
            Assert.Equal(p.RecordedActivityProbability * p.ConditionalMeanTokens, p.ExpectedTokens);
            Assert.InRange(p.RecordedActivityProbability, 0, 1);
            Assert.True(p.ConditionalLowTokens > 0);
            Assert.True(p.ConditionalHighTokens >= p.ConditionalLowTokens);
            Assert.True(p.ActiveTrainingOrigins >= 8);
            Assert.True(p.ActiveTrainingOrigins <= p.TrainingOrigins);
        }
        Assert.All(report.Scores, x =>
        {
            Assert.True(x.ConditionalOrigins <= x.ActiveOrigins);
            Assert.Equal(report.Trials.Count(t => t.Prediction.HorizonHours == x.HorizonHours && t.Prediction.ExpectedTokens is not null), x.ExpectedOrigins);
        });
    }

    [Fact]
    public void FutureOutcomesCannotChangeEarlierTrialsOrCurrentPrediction()
    {
        var first = History(2000, true);
        var later = History(3000, true);
        var before = SessionWorkloadPredictionService.Evaluate(first, first.CapturedAtUtc);
        var after = SessionWorkloadPredictionService.Evaluate(later, later.CapturedAtUtc);
        foreach (var trial in before.Trials)
            Assert.Equal(JsonSerializer.Serialize(trial), JsonSerializer.Serialize(after.Trials.Single(x =>
                x.OriginUtc == trial.OriginUtc && x.Prediction.HorizonHours == trial.Prediction.HorizonHours)));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(
            SessionWorkloadPredictionService.Evaluate(later, first.CapturedAtUtc)));
    }

    [Fact]
    public void SparseLateCollectedAndIdleEvidenceCannotPublishCurrentOutlooks()
    {
        var data = History(4000, false);
        Assert.Empty(SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc.AddMinutes(10)).Current);
        var late = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray() };
        Assert.Empty(SessionWorkloadPredictionService.Evaluate(late, data.CapturedAtUtc).Current);
        var sparse = History(300, false);
        Assert.Empty(SessionWorkloadPredictionService.Evaluate(sparse, sparse.CapturedAtUtc).Current);
    }

    [Fact]
    public void ActivityEstimateNeedsEarlierHeldOutEvidenceInTheSameAgeGroup()
    {
        var data = History(4000, false);
        var report = SessionWorkloadPredictionService.Evaluate(data, data.CapturedAtUtc);
        Assert.Contains(report.Trials, x => !x.Prediction.ActivityEstimateSupported);
        Assert.Contains(report.Trials, x => x.Prediction.ActivityEstimateSupported);
        foreach (var trial in report.Trials.Where(x => x.Prediction.ActivityEstimateSupported))
            Assert.True(report.Trials.Count(x => x.EndUtc <= trial.OriginUtc && x.AgeGroup == trial.AgeGroup &&
                x.Prediction.HorizonHours == trial.Prediction.HorizonHours) >= 64);
        Assert.All(report.Trials, x => Assert.True(x.EndUtc > x.OriginUtc));
        foreach (var group in report.Trials.GroupBy(x => x.Prediction.HorizonHours))
        {
            var rows = group.OrderBy(x => x.OriginUtc).ToArray();
            for (var i = 1; i < rows.Length; i++) Assert.True(rows[i - 1].EndUtc <= rows[i].OriginUtc);
        }
    }

    [Fact]
    public void CompletedQuietTailIsScoredWithoutWaitingForAnotherToken()
    {
        var active = History(4000, false);
        var lastToken = active.Tokens[^1].ObservedAtUtc;
        var captured = lastToken.AddHours(3);
        var stopped = active with { CapturedAtUtc = captured };
        var report = SessionWorkloadPredictionService.Evaluate(stopped, captured);
        foreach (var horizon in new[] { .5, 1d })
        {
            var tail = report.Trials.Where(x => x.OriginUtc >= lastToken && x.Prediction.HorizonHours == horizon).ToArray();
            Assert.NotEmpty(tail);
            Assert.All(tail, x =>
            {
                Assert.Equal(0, x.ObservedTokens);
                Assert.True(x.EndUtc <= captured);
                Assert.True(x.OriginUtc < lastToken.AddHours(2));
            });
        }
        Assert.Empty(report.Current);
        // Wall-clock passage cannot turn an old snapshot into new negative labels.
        var stale = SessionWorkloadPredictionService.Evaluate(stopped, captured.AddDays(1));
        Assert.Equal(JsonSerializer.Serialize(report.Trials), JsonSerializer.Serialize(stale.Trials));
        // Neither an incomplete target nor a token beyond the snapshot is evidence.
        var future = active.Tokens[^1] with { ObservedAtUtc = captured.AddMinutes(5), CapturedAtUtc = captured.AddMinutes(5) };
        var appended = SessionWorkloadPredictionService.Evaluate(stopped with { Tokens = stopped.Tokens.Append(future).ToArray() }, captured.AddHours(1));
        Assert.Equal(JsonSerializer.Serialize(report.Trials), JsonSerializer.Serialize(appended.Trials));
        Assert.All(report.Trials, x => Assert.True(x.EndUtc <= captured));
    }

    private static CodexForecastDataset History(int minutes, bool bursty)
    {
        var tokens = Enumerable.Range(0, minutes / 5 + 1).Where(i => !bursty || i % 20 < 10)
            .Select(i => new CodexPredictiveTokenEvent("session", Start.AddMinutes(i * 5), Start.AddMinutes(i * 5),
                "model", "effort", 100, 0, 0, 0, 0, 100)).ToArray();
        return new([], [], tokens, [], Start.AddMinutes(minutes), "Synthetic recorded activity; not real-world calibration proof.");
    }
}
