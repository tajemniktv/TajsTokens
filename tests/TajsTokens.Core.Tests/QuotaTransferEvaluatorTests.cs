// Taj's Tokens | QuotaTransferEvaluatorTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class QuotaTransferEvaluatorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceAndDestinationBothNeedRecordedTrainingWork(bool emptySource)
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        QuotaCostObservation[] source = Rows(start, "plus", 1, 40);
        QuotaCostObservation[] destination = Rows(start.AddDays(10), "pro", .4, 60);

        QuotaCostObservation Empty(QuotaCostObservation x)
        {
            return x with { Features = x.Features with { Tokens = 0 }, TokenCategories = [0, 0, 0, 0, 0] };
        }

        if (emptySource) source = source.Select(Empty).ToArray();
        else destination = destination.Select((x, i) => i < 20 ? Empty(x) : x).ToArray();
        IEnumerable<QuotaTransferScore> forward = QuotaTransferEvaluator.Evaluate(source.Concat(destination).ToArray()).Scores
            .Where(x => x.Source.PlanType == "plus");
        Assert.All(
            forward,
            x =>
            {
                Assert.Equal("no-recorded-training-work", x.Status);
                Assert.Null(x.UnscaledLoss);
                Assert.Null(x.ScaledLoss);
                Assert.Null(x.LocalOnlyLoss);
            });
    }

    [Fact]
    public void TransferUsesOnlyEarlierSourceAndDestinationTrainingAndKeepsAccountsSeparate()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        QuotaCostObservation[] source = Rows(start, "plus", 1, 40);
        QuotaCostObservation[] destination = Rows(start.AddDays(10), "pro", 0.4, 60);
        QuotaCostObservation[] observations = source.Concat(destination).ToArray();
        IReadOnlyList<QuotaTransferScore> scores = QuotaTransferEvaluator.Evaluate(observations).Scores;
        QuotaTransferScore forward = scores.Single(x => x.Source.PlanType == "plus" && x.Model == "total");
        Assert.Equal(40, forward.SourceTraining);
        Assert.Equal(40, forward.HeldOut);
        Assert.True(forward.Scale is > 0 and < 1);
        Assert.True(forward.ScaledLoss < forward.UnscaledLoss);
        Assert.All(scores.Where(x => x.Source.PlanType == "pro"), x => Assert.Equal(0, x.SourceTraining));
        QuotaCostObservation[] changed = observations.Select(x => x.Cohort.PlanType == "pro" && x.StartUtc >= destination[20].StartUtc
            ? x with { LowerDelta = 50, UpperDelta = 51 }
            : x).ToArray();
        QuotaTransferScore after = QuotaTransferEvaluator.Evaluate(changed).Scores
            .Single(x => x.Source.PlanType == "plus" && x.Model == "total");
        Assert.Equal(forward.Scale, after.Scale);
        IEnumerable<QuotaCostObservation> anotherAccount =
            destination.Select(x => x with { Cohort = x.Cohort with { AccountKey = "different" } });
        Assert.Empty(QuotaTransferEvaluator.Evaluate(source.Concat(anotherAccount).ToArray()).Scores);
    }

    private static QuotaCostObservation[] Rows(DateTimeOffset start, string plan, double scale, int count)
    {
        var cohort = new QuotaHistoryCohort(
            "codex",
            "default",
            QuotaWindowKind.Weekly,
            "codex-app-server:codex",
            "account",
            "codex",
            plan,
            null,
            10080);
        return Enumerable.Range(0, count).Select(i =>
        {
            DateTimeOffset time = start.AddDays(i / 10).AddMinutes(i % 10 * 30);
            long tokens = (1 + i % 3) * 1_000_000L;
            var data = new CodexForecastDataset(
                [],
                [],
                [new CodexPredictiveTokenEvent("s", time, time, "model", "high", tokens, 0, 0, 0, 0, tokens)],
                [],
                time,
                "fixture");
            CodexForecastFeatures features = CodexForecastFeatureBuilder.Build(data, time);
            double delta = tokens / 1e6 * 4 * scale;
            return new QuotaCostObservation(
                cohort,
                start,
                start.AddDays(i / 10 + 1),
                time,
                time.AddMinutes(30),
                0.5,
                10,
                10 + delta,
                Math.Max(0, delta - 0.2),
                delta + 0.2,
                "fixture",
                10,
                [tokens, 0, 0, 0, 0],
                features,
                null,
                0,
                []);
        }).ToArray();
    }
}