using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class JointQuotaCostTests
{
    private static QuotaCostObservation[] Contrast(int count) => QuotaCostEvaluationTests.Observations(count).Select((row, i) =>
    {
        // Same model totals and category totals; the observed joint pairings differ.
        var work = i % 2 == 0
            ? QuotaCostIdentificationTests.Work(row, ("A", 0, 1_000_000), ("B", 3, 1_000_000))
            : QuotaCostIdentificationTests.Work(row, ("A", 3, 1_000_000), ("B", 0, 1_000_000));
        double cost = i % 2 == 0 ? 51 : 15;
        return work with { EndUsed = work.StartUsed + cost, LowerDelta = cost - .1, UpperDelta = cost + .1 };
    }).ToArray();

    [Fact]
    public void JointPairingsExplainContrastThatIdenticalMarginalsCannot()
    {
        var report = QuotaCostEvaluation.Evaluate(Contrast(60), "joint counterexample");
        var joint = report.Scores.Single(x => x.Candidate == "joint-categories");
        var pooled = report.Scores.Single(x => x.Candidate == "categories");
        Assert.Equal(40, joint.HeldOutSamples);
        Assert.True(joint.IntervalLoss < pooled.IntervalLoss * .25);
        Assert.Equal(pooled.IntervalLoss, joint.PairedCategoryIntervalLoss);
        Assert.All(joint.Coefficients.Values, x => Assert.True(x >= 0 && double.IsFinite(x)));
        Assert.False(joint.MaterialWin);
        Assert.Equal("research-only-joint-shrunk-to-pooled", joint.Status);
        Assert.DoesNotContain(QuotaAccountingModel.Evaluate(Contrast(60), "live defaults").Scores, x => x.Candidate == "joint-categories");
    }

    [Fact]
    public void FutureOutcomesAndCoordinatesCannotMoveTheFrozenJointFit()
    {
        var rows = Contrast(60);
        var original = QuotaCostEvaluation.Evaluate(rows, "fixture").Scores.Single(x => x.Candidate == "joint-categories");
        rows[45] = QuotaCostIdentificationTests.Work(rows[45], ("future", 0, 5_000_000)) with { LowerDelta = 90, UpperDelta = 92, EndUsed = 99 };
        var changed = QuotaCostEvaluation.Evaluate(rows, "fixture").Scores.Single(x => x.Candidate == "joint-categories");
        Assert.Equal(original.Coefficients, changed.Coefficients);
        Assert.Equal(original.Trials.Take(25).Select(x => x.Prediction), changed.Trials.Take(25).Select(x => x.Prediction));
        Assert.DoesNotContain(changed.Coefficients.Keys, x => x.Contains("future", StringComparison.Ordinal));
    }

    [Fact]
    public void UnobservedModelCoordinatesUsePooledPriorWithoutClaimingIdentifiedPrices()
    {
        var training = Contrast(20);
        var pooled = QuotaAccountingModel.FitFrozen(training, "categories", default);
        var joint = QuotaAccountingModel.FitFrozen(training, "joint-categories", default);
        var novel = QuotaCostIdentificationTests.Work(training[0], ("unseen", 0, 2_000_000), ("unseen", 3, 3_000_000));
        Assert.Equal(pooled(novel), joint(novel), 8);
        Assert.Equal(0, joint(QuotaCostIdentificationTests.Work(training[0])));
    }

    [Fact]
    public void MissingJointTrainingWithholdsOnlyJointCandidateAndInvalidOutcomesStayUnmatched()
    {
        var rows = Contrast(40);
        rows[0] = rows[0] with { Features = rows[0].Features with { JointWorkload = null } };
        var report = QuotaCostEvaluation.Evaluate(rows, "fixture");
        var joint = report.Scores.Single(x => x.Candidate == "joint-categories");
        Assert.Empty(joint.Trials); Assert.Equal("incomplete-joint-training", joint.Status);
        Assert.Equal(1, joint.IncompleteJointTrainingIntervals);
        Assert.NotEmpty(report.Scores.Single(x => x.Candidate == "categories").Trials);

        rows = Contrast(40);
        rows[25] = rows[25] with { Features = rows[25].Features with { JointWorkload = null } };
        report = QuotaCostEvaluation.Evaluate(rows, "fixture");
        joint = report.Scores.Single(x => x.Candidate == "joint-categories");
        Assert.Equal(19, joint.HeldOutSamples); Assert.Equal(1, joint.IncompleteJointHeldOutIntervals);
        var matched = report.Scores.Single(x => x.Candidate == "categories").Trials.Where(x => x.StartUtc != rows[25].StartUtc);
        Assert.Equal(matched.Average(x => x.IntervalLoss), joint.PairedCategoryIntervalLoss);
    }

    [Fact]
    public void ExplicitComposedResearchSeparatesOracleLossAndKeepsDefaultCandidatesUnchanged()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var control = ComposedQuotaEvaluator.Evaluate(data, horizons: [.5]);
        var research = ComposedQuotaEvaluator.Evaluate(data, horizons: [.5], includeJointResearch: true);
        Assert.DoesNotContain(control.Scores, x => x.CostModel.Contains("joint", StringComparison.Ordinal));
        foreach (var score in control.Scores)
        {
            var paired = research.Scores.Single(x => x.Cohort == score.Cohort && x.CostModel == score.CostModel);
            Assert.Equal(score.IntervalLoss, paired.IntervalLoss);
            Assert.Equal(score.CostOnlyIntervalLoss, paired.CostOnlyIntervalLoss);
        }
        var joint = research.Scores.Single(x => x.CostModel == "joint-categories");
        Assert.NotEmpty(joint.Trials);
        Assert.All(joint.Trials, x => Assert.NotNull(x.Workload.JointWorkload));
        var late = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray() };
        var strict = ComposedQuotaEvaluator.Evaluate(late, availability: ForecastReplayAvailability.CollectedByOrigin, horizons: [.5], includeJointResearch: true);
        Assert.Empty(strict.Scores.Single(x => x.CostModel == "joint-categories").Trials);
    }
}
