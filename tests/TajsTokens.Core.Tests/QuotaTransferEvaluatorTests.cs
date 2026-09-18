using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaTransferEvaluatorTests
{
    [Fact]
    public void TransferUsesOnlyEarlierSourceAndDestinationTrainingAndKeepsAccountsSeparate()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var source = Rows(start, "plus", 1, 40);
        var destination = Rows(start.AddDays(10), "pro", 0.4, 60);
        var observations = source.Concat(destination).ToArray();
        var scores = QuotaTransferEvaluator.Evaluate(observations).Scores;
        var forward = scores.Single(x => x.Source.PlanType == "plus" && x.Model == "total");
        Assert.Equal(40, forward.SourceTraining);
        Assert.Equal(40, forward.HeldOut);
        Assert.True(forward.Scale is > 0 and < 1);
        Assert.True(forward.ScaledLoss < forward.UnscaledLoss);
        Assert.All(scores.Where(x => x.Source.PlanType == "pro"), x => Assert.Equal(0, x.SourceTraining));
        var changed = observations.Select(x => x.Cohort.PlanType == "pro" && x.StartUtc >= destination[20].StartUtc
            ? x with { LowerDelta = 50, UpperDelta = 51 } : x).ToArray();
        var after = QuotaTransferEvaluator.Evaluate(changed).Scores.Single(x => x.Source.PlanType == "plus" && x.Model == "total");
        Assert.Equal(forward.Scale, after.Scale);
        var anotherAccount = destination.Select(x => x with { Cohort = x.Cohort with { AccountKey = "different" } });
        Assert.Empty(QuotaTransferEvaluator.Evaluate(source.Concat(anotherAccount).ToArray()).Scores);
    }

    private static QuotaCostObservation[] Rows(DateTimeOffset start, string plan, double scale, int count)
    {
        var cohort = new QuotaHistoryCohort("codex", "default", QuotaWindowKind.Weekly,
            "codex-app-server:codex", "account", "codex", plan, null, 10080);
        return Enumerable.Range(0, count).Select(i =>
        {
            var time = start.AddDays(i / 10).AddMinutes(i % 10 * 30);
            var tokens = (1 + i % 3) * 1_000_000L;
            var data = new CodexForecastDataset([], [], [new("s", time, time, "model", "high", tokens, 0, 0, 0, 0, tokens)], [], time, "fixture");
            var features = CodexForecastFeatureBuilder.Build(data, time);
            var delta = tokens / 1e6 * 4 * scale;
            return new QuotaCostObservation(cohort, start, start.AddDays(i / 10 + 1), time, time.AddMinutes(30),
                0.5, 10, 10 + delta, Math.Max(0, delta - 0.2), delta + 0.2, "fixture", 10,
                [tokens, 0, 0, 0, 0], features, null, 0, []);
        }).ToArray();
    }
}
