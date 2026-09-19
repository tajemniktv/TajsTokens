using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class SessionQuotaEvaluatorTests
{
    [Fact]
    public void IncumbentComparisonUsesOnlyIdenticalOriginAndOutcomePairs()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var score = SessionQuotaEvaluator.Evaluate(data).Scores.Single(x => x.HorizonHours == .5);
        var baseline = QuotaPredictionService.Replay(data, .5,
            TajsTokens.Core.Models.ForecastReplayAvailability.ReconstructedEventTime)
            .ToDictionary(x => (x.Observation.OriginUtc, x.Observation.OutcomeUtc));
        var paired = score.Trials.Where(x => baseline.ContainsKey((x.OriginUtc, x.EndUtc))).ToArray();
        Assert.NotEmpty(paired);
        Assert.Equal(paired.Length, score.IncumbentPairedOrigins);
        Assert.Equal(paired.Average(x => x.ExpectedError), score.PairedExpectedMae);
        foreach (var trial in score.Trials)
        {
            if (!baseline.TryGetValue((trial.OriginUtc, trial.EndUtc), out var match))
            {
                Assert.Null(trial.IncumbentError);
                Assert.Null(trial.IncumbentIntervalLoss);
                continue;
            }
            var endRemaining = 100 - data.Quota.Single(x => x.CapturedAtUtc == trial.EndUtc).UsedPercent!.Value;
            Assert.Equal(Math.Abs(match.Prediction.RemainingPercent - endRemaining), trial.IncumbentError);
        }
        Assert.Equal(paired.Average(x => x.IncumbentError!.Value), score.IncumbentMae);
    }

    [Fact]
    public void ChainUsesOriginWorkloadAndKeepsCohortsAndTargetsSeparate()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var before = SessionQuotaEvaluator.Evaluate(data);
        var score = before.Scores.Single(x => x.HorizonHours == .5);
        Assert.NotEmpty(score.Trials);
        Assert.Equal(20, score.TrainingIntervals);
        Assert.All(score.Trials, x =>
        {
            Assert.Equal(x.ActivityProbability * x.ConditionalQuota, x.ExpectedQuota);
            Assert.Null(x.LowerExpectedQuota);
            Assert.Null(x.UpperExpectedQuota);
            Assert.Equal(TimeSpan.FromMinutes(30), x.EndUtc - x.OriginUtc);
        });
        var first = score.Trials[0];
        var extra = data.Tokens[0] with { ObservedAtUtc = first.OriginUtc.AddMinutes(1), ReportedTotalTokens = 1_000_000 };
        var after = SessionQuotaEvaluator.Evaluate(data with { Tokens = data.Tokens.Append(extra).ToArray() });
        var amended = after.Scores.Single(x => x.HorizonHours == .5).Trials[0];
        Assert.Equal(first.ConditionalQuota, amended.ConditionalQuota);
        Assert.Equal(first.ExpectedQuota, amended.ExpectedQuota);
        Assert.Empty(SessionQuotaEvaluator.Evaluate(data with
        { Quota = data.Quota.Select(x => x with { AccountKey = null }).ToArray() }).Scores);
        var other = data.Quota.Select(x => x with { AccountKey = "other", UsedPercent = x.UsedPercent / 2 }).ToArray();
        var mixed = SessionQuotaEvaluator.Evaluate(data with { Quota = data.Quota.Concat(other).ToArray() });
        Assert.Equal(score.Trials.ToArray(), mixed.Scores.Single(x => x.Cohort.AccountKey == "account" && x.HorizonHours == .5).Trials.ToArray());
    }

    [Fact]
    public void PollingJitterUsesActualElapsedHorizonRatherThanDiscardingAllTargets()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var jittered = data with
        {
            Quota = data.Quota.Select((x, i) => x with
            { CapturedAtUtc = x.CapturedAtUtc.AddSeconds(i), CollectedAtUtc = x.CollectedAtUtc?.AddSeconds(i) }).ToArray()
        };
        var score = SessionQuotaEvaluator.Evaluate(jittered).Scores.Single(x => x.HorizonHours == .5);
        Assert.NotEmpty(score.Trials);
        Assert.All(score.Trials, x => Assert.Equal(TimeSpan.FromMinutes(30).Add(TimeSpan.FromSeconds(2)), x.EndUtc - x.OriginUtc));
    }

    [Fact]
    public void QuietOutcomesRemainInExpectedErrorsButNotConditionalErrors()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var first = SessionQuotaEvaluator.Evaluate(data).Scores.Single(x => x.HorizonHours == .5).Trials[0];
        var quiet = data with { Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= first.OriginUtc || x.ObservedAtUtc > first.EndUtc).ToArray() };
        var score = SessionQuotaEvaluator.Evaluate(quiet).Scores.Single(x => x.HorizonHours == .5);
        var trial = score.Trials.Single(x => x.OriginUtc == first.OriginUtc);
        Assert.False(trial.RecordedActivity);
        Assert.Equal(first.ExpectedQuota, trial.ExpectedQuota);
        Assert.Equal(score.Trials.Count(x => x.RecordedActivity), score.ActiveOutcomes);
        Assert.True(score.ActiveOutcomes < score.Trials.Count);
        Assert.Equal(score.Trials.Average(x => x.ExpectedError), score.ExpectedMae);
        Assert.Equal(score.Trials.Where(x => x.RecordedActivity).Average(x => x.ConditionalError), score.ConditionalMae);
    }

    [Fact]
    public void SparseHourlyTrainingCannotBorrowHalfHourIntervals()
    {
        var report = SessionQuotaEvaluator.Evaluate(ComposedQuotaEvaluatorTests.TimelyData());
        var hourly = report.Scores.Single(x => x.HorizonHours == 1);
        Assert.True(hourly.TrainingIntervals < 20);
        Assert.Empty(hourly.Trials);
        Assert.Null(hourly.ExpectedMae);
        Assert.Equal(0, hourly.IncumbentPairedOrigins);
        Assert.Null(hourly.PairedExpectedMae);
        Assert.Null(hourly.IncumbentMae);
    }
}
