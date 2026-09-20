// Taj's Tokens | SessionQuotaEvaluatorTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class SessionQuotaEvaluatorTests
{
    [Fact]
    public void HourlySessionComparisonsRecoverOriginsMissedByOrdinaryReplaySampling()
    {
        CodexForecastDataset original = ComposedQuotaEvaluatorTests.TimelyData();
        DateTimeOffset start = original.Quota[0].CapturedAtUtc;

        DateTimeOffset At(int i)
        {
            return start.AddMinutes(i * 15 + (i % 11 == 0 ? 2 : 0));
        }

        CodexForecastDataset data = original with
        {
            CapturedAtUtc = start.AddDays(3),
            Quota =
            Enumerable.Range(0, 150)
                .Select(i => original.Quota[0] with { CapturedAtUtc = At(i), CollectedAtUtc = At(i), UsedPercent = i / 3d }).ToArray(),
            Tokens = Enumerable.Range(0, 150).Select(i => original.Tokens[0] with { ObservedAtUtc = At(i), CapturedAtUtc = At(i) })
                .ToArray(),
        };
        SessionQuotaScore score = SessionQuotaEvaluator.Evaluate(data).Scores.Single(x => x.HorizonHours == 1);
        Assert.NotEmpty(score.Trials);
        IReadOnlyList<QuotaPredictionTrial> sampled = QuotaPredictionService.Replay(
            data,
            1,
            ForecastReplayAvailability.ReconstructedEventTime);
        Assert.Contains(score.Trials, x => sampled.All(y => y.Observation.OriginUtc != x.OriginUtc));
        Assert.Equal(score.Trials.Count, score.IncumbentPairedOrigins);
        Assert.All(
            score.Trials,
            x =>
            {
                Assert.NotNull(x.IncumbentError);
                Assert.NotNull(x.IncumbentIntervalLoss);
            });
        Assert.Equal(score.Trials.Average(x => x.ExpectedError), score.PairedExpectedMae);
        Assert.Equal(score.Trials.Average(x => x.IncumbentError!.Value), score.IncumbentMae);
    }

    [Fact]
    public void IncumbentComparisonUsesOnlyIdenticalOriginAndOutcomePairs()
    {
        CodexForecastDataset data = ComposedQuotaEvaluatorTests.TimelyData();
        SessionQuotaScore score = SessionQuotaEvaluator.Evaluate(data).Scores.Single(x => x.HorizonHours == .5);
        Dictionary<(DateTimeOffset OriginUtc, DateTimeOffset OutcomeUtc), QuotaPredictionTrial> baseline = QuotaPredictionService.Replay(
                data,
                .5,
                ForecastReplayAvailability.ReconstructedEventTime)
            .ToDictionary(x => (x.Observation.OriginUtc, x.Observation.OutcomeUtc));
        SessionQuotaTrial[] paired = score.Trials.Where(x => baseline.ContainsKey((x.OriginUtc, x.EndUtc))).ToArray();
        Assert.NotEmpty(paired);
        Assert.Equal(paired.Length, score.IncumbentPairedOrigins);
        Assert.Equal(paired.Average(x => x.ExpectedError), score.PairedExpectedMae);
        foreach (SessionQuotaTrial trial in score.Trials)
        {
            if (!baseline.TryGetValue((trial.OriginUtc, trial.EndUtc), out QuotaPredictionTrial? match))
            {
                Assert.Null(trial.IncumbentError);
                Assert.Null(trial.IncumbentIntervalLoss);
                continue;
            }
            double endRemaining = 100 - data.Quota.Single(x => x.CapturedAtUtc == trial.EndUtc).UsedPercent!.Value;
            Assert.Equal(Math.Abs(match.Prediction.RemainingPercent - endRemaining), trial.IncumbentError);
        }
        Assert.Equal(paired.Average(x => x.IncumbentError!.Value), score.IncumbentMae);
    }

    [Fact]
    public void ChainUsesOriginWorkloadAndKeepsCohortsAndTargetsSeparate()
    {
        CodexForecastDataset data = ComposedQuotaEvaluatorTests.TimelyData();
        SessionQuotaEvaluation before = SessionQuotaEvaluator.Evaluate(data);
        SessionQuotaScore score = before.Scores.Single(x => x.HorizonHours == .5);
        Assert.NotEmpty(score.Trials);
        Assert.Equal(20, score.TrainingIntervals);
        Assert.All(
            score.Trials,
            x =>
            {
                Assert.Equal(x.ActivityProbability * x.ConditionalQuota, x.ExpectedQuota);
                Assert.Null(x.LowerExpectedQuota);
                Assert.Null(x.UpperExpectedQuota);
                Assert.Equal(TimeSpan.FromMinutes(30), x.EndUtc - x.OriginUtc);
            });
        SessionQuotaTrial first = score.Trials[0];
        CodexPredictiveTokenEvent extra = data.Tokens[0] with
        {
            ObservedAtUtc = first.OriginUtc.AddMinutes(1), ReportedTotalTokens = 1_000_000,
        };
        SessionQuotaEvaluation after = SessionQuotaEvaluator.Evaluate(data with { Tokens = data.Tokens.Append(extra).ToArray() });
        SessionQuotaTrial amended = after.Scores.Single(x => x.HorizonHours == .5).Trials[0];
        Assert.Equal(first.ConditionalQuota, amended.ConditionalQuota);
        Assert.Equal(first.ExpectedQuota, amended.ExpectedQuota);
        Assert.Empty(
            SessionQuotaEvaluator.Evaluate(data with { Quota = data.Quota.Select(x => x with { AccountKey = null }).ToArray() }).Scores);
        QuotaSnapshot[] other = data.Quota.Select(x => x with { AccountKey = "other", UsedPercent = x.UsedPercent / 2 }).ToArray();
        SessionQuotaEvaluation mixed = SessionQuotaEvaluator.Evaluate(data with { Quota = data.Quota.Concat(other).ToArray() });
        Assert.Equal(
            score.Trials.ToArray(),
            mixed.Scores.Single(x => x.Cohort.AccountKey == "account" && x.HorizonHours == .5).Trials.ToArray());
    }

    [Fact]
    public void PollingJitterUsesActualElapsedHorizonRatherThanDiscardingAllTargets()
    {
        CodexForecastDataset data = ComposedQuotaEvaluatorTests.TimelyData();
        CodexForecastDataset jittered = data with
        {
            Quota = data.Quota.Select((x, i) => x with
            {
                CapturedAtUtc = x.CapturedAtUtc.AddSeconds(i), CollectedAtUtc = x.CollectedAtUtc?.AddSeconds(i),
            }).ToArray(),
        };
        SessionQuotaScore score = SessionQuotaEvaluator.Evaluate(jittered).Scores.Single(x => x.HorizonHours == .5);
        Assert.NotEmpty(score.Trials);
        Assert.All(score.Trials, x => Assert.Equal(TimeSpan.FromMinutes(30).Add(TimeSpan.FromSeconds(2)), x.EndUtc - x.OriginUtc));
    }

    [Fact]
    public void QuietOutcomesRemainInExpectedErrorsButNotConditionalErrors()
    {
        CodexForecastDataset data = ComposedQuotaEvaluatorTests.TimelyData();
        SessionQuotaTrial first = SessionQuotaEvaluator.Evaluate(data).Scores.Single(x => x.HorizonHours == .5).Trials[0];
        CodexForecastDataset quiet = data with
        {
            Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= first.OriginUtc || x.ObservedAtUtc > first.EndUtc).ToArray(),
        };
        SessionQuotaScore score = SessionQuotaEvaluator.Evaluate(quiet).Scores.Single(x => x.HorizonHours == .5);
        SessionQuotaTrial trial = score.Trials.Single(x => x.OriginUtc == first.OriginUtc);
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
        SessionQuotaEvaluation report = SessionQuotaEvaluator.Evaluate(ComposedQuotaEvaluatorTests.TimelyData());
        SessionQuotaScore hourly = report.Scores.Single(x => x.HorizonHours == 1);
        Assert.True(hourly.TrainingIntervals < 20);
        Assert.Empty(hourly.Trials);
        Assert.Null(hourly.ExpectedMae);
        Assert.Equal(0, hourly.IncumbentPairedOrigins);
        Assert.Null(hourly.PairedExpectedMae);
        Assert.Null(hourly.IncumbentMae);
    }
}