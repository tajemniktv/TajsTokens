// Taj's Tokens | ComposedQuotaEvaluatorTests.cs
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

public sealed class ComposedQuotaEvaluatorTests
{
    [Fact]
    public void ShortQuotaHorizonsHaveIndependentStrictTargetsAndDoNotChangeLongerTrials()
    {
        CodexForecastDataset original = TimelyData();
        DateTimeOffset start = original.Quota[0].CapturedAtUtc;
        CodexForecastDataset data = original with
        {
            Quota = Enumerable.Range(0, 200).Select(i => original.Quota[0] with
            {
                UsedPercent = i / 3d, CapturedAtUtc = start.AddMinutes(i * 5), CollectedAtUtc = start.AddMinutes(i * 5),
            }).ToArray(),
            Tokens = Enumerable.Range(0, 200).Select(i => original.Tokens[0] with
            {
                ObservedAtUtc = start.AddMinutes(i * 5), CapturedAtUtc = start.AddMinutes(i * 5),
            }).ToArray(),
        };
        ComposedQuotaEvaluation all = ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(ComposedQuotaEvaluator.EvaluationHorizons, all.Scores.Select(x => x.HorizonHours).Distinct().Order().ToArray());
        foreach (double horizon in new[] { 5d / 60, .25 })
        {
            ComposedQuotaScore score = Assert.Single(all.Scores, x => x.HorizonHours == horizon && x.CostModel == "total");
            Assert.NotEmpty(score.Trials);
            Assert.All(
                score.Trials,
                trial =>
                {
                    Assert.Equal(TimeSpan.FromHours(horizon), trial.OutcomeUtc - trial.OriginUtc);
                    Assert.NotNull(trial.IncumbentIntervalLoss);
                    Assert.NotNull(trial.ZeroUseIntervalLoss);
                    Assert.Null(trial.LowerRemainingPercent); // One reset is still insufficient.
                });
            Assert.True(score.Trials.Zip(score.Trials.Skip(1)).All(pair => pair.First.OutcomeUtc <= pair.Second.OriginUtc));
        }
        ComposedQuotaEvaluation legacy = ComposedQuotaEvaluator.Evaluate(
            data,
            availability: ForecastReplayAvailability.CollectedByOrigin,
            horizons: [.5, 2d]);
        foreach (ComposedQuotaScore score in legacy.Scores)
        {
            Assert.Equal(
                JsonSerializer.Serialize(score.Trials),
                JsonSerializer.Serialize(
                    all.Scores.Single(x =>
                        x.Cohort == score.Cohort && x.HorizonHours == score.HorizonHours && x.CostModel == score.CostModel).Trials));
        }
    }

    [Fact]
    public void ShortTargetsRejectCoarsePollingRatherThanRelabelLongerOutcomes()
    {
        CodexForecastDataset data = TimelyData();
        DateTimeOffset start = data.Quota[0].CapturedAtUtc;
        QuotaCostObservationBuild coarse = QuotaCostObservationBuilder.BuildDetailed(data, horizons: [5d / 60]);
        Assert.Empty(coarse.Observations); // A 15-minute polling stream cannot validate five minutes.
        Assert.Contains(coarse.Coverage, x => x.RejectedStarts.GetValueOrDefault("outcome-beyond-poll-tolerance") > 0);
        foreach (int lateMinutes in new[] { 1, 2 })
        {
            CodexForecastDataset jittered = data with
            {
                Quota = data.Quota.Select((x, i) => x with
                {
                    CapturedAtUtc = start.AddMinutes(i * 5 + (i == 4 ? lateMinutes : 0)),
                    CollectedAtUtc = start.AddMinutes(i * 5 + (i == 4 ? lateMinutes : 0)),
                }).ToArray(),
            };
            IReadOnlyList<QuotaCostObservation> rows = QuotaCostObservationBuilder.Build(jittered, horizons: [5d / 60]);
            QuotaCostObservation? row = rows.SingleOrDefault(x => x.StartUtc == start.AddMinutes(15));
            if (lateMinutes == 1) Assert.Equal(TimeSpan.FromMinutes(6), row!.EndUtc - row.StartUtc);
            else Assert.Null(row);
        }
    }

    internal static CodexForecastDataset TimelyData()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        return new CodexForecastDataset(
            Enumerable.Range(0, 65).Select(i => new QuotaSnapshot(
                    QuotaWindowKind.Weekly,
                    start.AddMinutes(i * 15),
                    i,
                    10080,
                    start.AddDays(7),
                    "codex",
                    "default",
                    "codex-app-server:codex",
                    "account")
                {
                    HasSourceTimestamp = true, CollectedAtUtc = start.AddMinutes(i * 15), PlanType = "pro", LimitId = "codex",
                })
                .ToArray(),
            [],
            Enumerable.Range(0, 65).Select(i => new CodexPredictiveTokenEvent(
                "s",
                start.AddMinutes(i * 15),
                start.AddMinutes(i * 15),
                "m",
                "high",
                10000,
                0,
                0,
                0,
                0,
                10000)).ToArray(),
            [],
            start.AddDays(1),
            "fixture");
    }

    [Fact]
    public void UnusedLateMetadataDoesNotInvalidateTokenCostTraining()
    {
        CodexForecastDataset data = TimelyData();
        var late = new CodexWorkloadObservation(
            "late-tier",
            "source",
            "file",
            0,
            1,
            "s",
            "thread_settings_applied",
            data.Quota[0].CapturedAtUtc,
            data.CapturedAtUtc,
            null,
            null,
            null,
            null,
            null,
            null,
            ServiceTier: "priority");
        CodexForecastDataset amended = data with { Workload = [late] };
        IReadOnlyList<QuotaCostObservation> costRows = QuotaCostObservationBuilder.Build(amended);
        Assert.All(
            costRows,
            row =>
            {
                Assert.Equal(data.CapturedAtUtc, row.EvidenceAvailableAtUtc);
                Assert.Equal(row.EndUtc, row.TokenCostEvidenceAvailableAtUtc);
            });
        ComposedQuotaEvaluation before = ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin);
        ComposedQuotaEvaluation after = ComposedQuotaEvaluator.Evaluate(
            amended,
            availability: ForecastReplayAvailability.CollectedByOrigin);
        foreach (ComposedQuotaScore score in before.Scores)
        {
            ComposedQuotaScore updated = after.Scores.Single(x =>
                x.Cohort == score.Cohort && x.HorizonHours == score.HorizonHours && x.CostModel == score.CostModel);
            Assert.Equal(
                score.Trials.Select(x => (x.OriginUtc, x.PredictedDelta)),
                updated.Trials.Select(x => (x.OriginUtc, x.PredictedDelta)));
            Assert.Equal(score.WithheldReasons, updated.WithheldReasons);
        }
        Assert.Contains(after.Scores, x => x.HeldOutIntervals > 0);
    }

    [Fact]
    public void StrictEvaluationRequiresTrainingAvailableAtOriginAndTimelyMeters()
    {
        CodexForecastDataset data = TimelyData();
        ComposedQuotaEvaluation strict = ComposedQuotaEvaluator.Evaluate(data, availability: ForecastReplayAvailability.CollectedByOrigin);
        IReadOnlyList<ComposedQuotaTrial> trials = strict.Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total").Trials;
        Assert.NotEmpty(trials);
        Assert.All(
            trials,
            x =>
            {
                Assert.Equal(ForecastReplayAvailability.CollectedByOrigin, x.Availability);
                Assert.Equal(ForecastReplayAvailability.CollectedByOrigin, x.Workload.Availability);
                Assert.Equal(x.OutcomeUtc, x.CalibrationAvailableAtUtc);
                Assert.Null(x.LowerRemainingPercent); // One reset cannot establish joint uncertainty.
                Assert.Null(x.UpperRemainingPercent);
            });
        CodexForecastDataset delayedTraining = data with
        {
            Tokens = data.Tokens.Select((x, i) => i < 40 ? x with { CapturedAtUtc = data.CapturedAtUtc } : x).ToArray(),
        };
        CodexForecastDataset unknownTraining = data with
        {
            Tokens = data.Tokens.Select((x, i) => i == 2 ? x with { CapturedAtUtc = null } : x).ToArray(),
        };
        CodexForecastDataset delayedMeters =
            data with { Quota = data.Quota.Select(x => x with { CollectedAtUtc = data.CapturedAtUtc }).ToArray() };
        foreach ((CodexForecastDataset unavailable, string reason) in new[]
                 {
                     (delayedTraining, "training-collected-after-origin"),
                     (unknownTraining, "training-collection-unknown"),
                     (delayedMeters, "origin-meter-collected-after-origin"),
                 })
        {
            Assert.NotEmpty(
                ComposedQuotaEvaluator.Evaluate(unavailable).Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total").Trials);
            ComposedQuotaScore score = ComposedQuotaEvaluator
                .Evaluate(unavailable, availability: ForecastReplayAvailability.CollectedByOrigin)
                .Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total");
            Assert.Empty(score.Trials);
            Assert.Equal(score.MissingComposition, score.WithheldReasons[reason]);
        }
    }

    [Fact]
    public void ForecastAndCostOnlyErrorsAreSeparateAndFutureWorkCannotChangePrediction()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        QuotaSnapshot[] quota = Enumerable.Range(0, 55).Select(i => new QuotaSnapshot(
            QuotaWindowKind.Weekly,
            start.AddMinutes(i * 15),
            i,
            10080,
            start.AddDays(7),
            "codex",
            "default",
            "codex-app-server:codex",
            "account") { HasSourceTimestamp = true, CollectedAtUtc = start.AddMinutes(i * 15) }).ToArray();
        CodexPredictiveTokenEvent[] tokens = Enumerable.Range(0, 55).Select(i => new CodexPredictiveTokenEvent(
            "s",
            start.AddMinutes(i * 15),
            start.AddMinutes(i * 15),
            "m",
            "high",
            10000,
            0,
            0,
            0,
            0,
            10000)).ToArray();
        var data = new CodexForecastDataset(quota, [], tokens, [], start.AddDays(1), "fixture");
        ComposedQuotaScore before = ComposedQuotaEvaluator.Evaluate(data).Scores
            .Single(x => x.HorizonHours == 0.5 && x.CostModel == "total");
        Assert.NotEmpty(before.Trials);
        DateTimeOffset origin = before.Trials[0].OriginUtc;
        CodexPredictiveTokenEvent extra = tokens[0] with
        {
            ObservedAtUtc = origin.AddMinutes(1), UncachedInputTokens = 1_000_000, ReportedTotalTokens = 1_000_000, Model = "future",
        };
        ComposedQuotaScore after = ComposedQuotaEvaluator.Evaluate(data with { Tokens = tokens.Append(extra).ToArray() })
            .Scores.Single(x => x.HorizonHours == 0.5 && x.CostModel == "total");
        Assert.Equal(before.Trials[0].PredictedDelta, after.Trials[0].PredictedDelta);
        Assert.NotEqual(before.Trials[0].ActualWorkloadCost, after.Trials[0].ActualWorkloadCost);
        Assert.DoesNotContain("future", after.Trials[0].Workload.ModelShares.Keys);
        Assert.All(after.Trials, x => Assert.InRange(x.PredictedRemaining, 0, 100));
    }
}