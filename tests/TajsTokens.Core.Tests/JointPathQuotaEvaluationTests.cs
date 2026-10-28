using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class JointPathQuotaEvaluationTests
{
    [Fact]
    public void ExplicitPathResearchKeepsIncumbentsUnchangedAndSeparatesRawPathRanges()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var control = ComposedQuotaEvaluator.Evaluate(data, horizons: [.5], includeJointResearch: true);
        var research = ComposedQuotaEvaluator.Evaluate(data, horizons: [.5], includeJointPathsResearch: true);
        foreach (var original in control.Scores)
        {
            var same = research.Scores.Single(x => x.Cohort == original.Cohort && x.CostModel == original.CostModel);
            Assert.Equal(original.IntervalLoss, same.IntervalLoss);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original.Trials), System.Text.Json.JsonSerializer.Serialize(same.Trials));
        }
        foreach (var score in research.Scores.Where(x => x.CostModel.StartsWith("joint-blocks-", StringComparison.Ordinal)))
        {
            Assert.NotEmpty(score.Trials);
            Assert.All(score.Trials, trial =>
            {
                var path = Assert.IsType<JointQuotaPathEvidence>(trial.JointPath);
                Assert.Equal(64, path.Paths); Assert.NotNull(path.Seed); Assert.NotNull(path.InputRevision);
                Assert.Equal(QuotaUncertaintyKind.ConditionalResampledPathRange, path.PathRangeKind);
                Assert.True(path.PathP10Delta <= path.PathP90Delta);
                Assert.True(path.CostTrainingToUtc <= trial.OriginUtc);
                Assert.Equal(CodexJointQuantityKind.ConditionalProjection, trial.Workload.JointWorkload!.Kind);
                Assert.Contains("frozen-fit-outside-declared-measurement-envelopes", path.InputCompatibilityReasons);
                Assert.True(path.MaximumTrainingEnvelopeViolation > 0);
            });
        }
        Assert.DoesNotContain(ComposedQuotaEvaluator.Evaluate(data, horizons: [.5]).Scores, x => x.CostModel.StartsWith("joint-", StringComparison.Ordinal));
    }

    [Fact]
    public void FlatMeterCanBeCompatibleWithoutProvingThatFutureWorkIsFree()
    {
        var original = ComposedQuotaEvaluatorTests.TimelyData();
        var data = original with { Quota = original.Quota.Select(x => x with { UsedPercent = 20 }).ToArray() };
        var research = ComposedQuotaEvaluator.Evaluate(data, horizons: [.5], includeJointPathsResearch: true);
        foreach (var score in research.Scores.Where(x => x.CostModel.StartsWith("joint-blocks-", StringComparison.Ordinal)))
            Assert.All(score.Trials, trial =>
            {
                Assert.Equal(0, trial.PredictedDelta); Assert.True(trial.JointPath!.CompatibleFrozenFitInputs);
                Assert.Contains("not identification", trial.JointPath.Qualification);
            });
    }

    [Fact]
    public void LaterOutcomesCannotMoveOriginPathsAndLateTrainingCannotQualify()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var original = ComposedQuotaEvaluator.Evaluate(data, horizons: [.5], includeJointPathsResearch: true).Scores.Single(x => x.CostModel == "joint-blocks-30m");
        var first = original.Trials[0];
        var amended = data with { Tokens = data.Tokens.Select(x => x.ObservedAtUtc > first.OriginUtc
            ? x with { Model = "future", UncachedInputTokens = 50000, ReportedTotalTokens = 50000 } : x).ToArray() };
        var after = ComposedQuotaEvaluator.Evaluate(amended, horizons: [.5], includeJointPathsResearch: true).Scores.Single(x => x.CostModel == original.CostModel);
        Assert.Equal(first.PredictedDelta, after.Trials[0].PredictedDelta);
        Assert.Equal(first.JointPath!.InputRevision, after.Trials[0].JointPath!.InputRevision);
        Assert.Contains(after.Trials.Skip(1), x => x.JointPath!.NovelCoordinates > 0 && x.JointPath.InputCompatibilityReasons.Contains("novel-projected-coordinates"));
        var late = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray() };
        var strict = ComposedQuotaEvaluator.Evaluate(late, availability: ForecastReplayAvailability.CollectedByOrigin, horizons: [.5], includeJointPathsResearch: true);
        Assert.All(strict.Scores.Where(x => x.CostModel.StartsWith("joint-", StringComparison.Ordinal)), x => Assert.Empty(x.Trials));
    }
}
