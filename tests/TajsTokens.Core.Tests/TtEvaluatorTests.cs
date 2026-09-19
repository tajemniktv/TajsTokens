using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TtEvaluatorTests
{
    [Fact]
    public void BasisIsImmutableAdditiveAnchoredAndContentAddressed()
    {
        double[] weights = [1, 2, 0, 3, 4];
        double[] reference = [100, 100, 0, 100, 100];
        var basis = new TtWorkloadBasis(weights, reference, [true, true, false, true, true], ["b", "a"], ["high"]);
        var same = new TtWorkloadBasis(weights, reference, [true, true, false, true, true], ["a", "b", "a"], ["high"]);
        Assert.Equal(basis.BasisId, same.BasisId);
        Assert.Equal(1, basis.Score(reference));
        Assert.Equal(2, basis.Score(reference.Select(x => x * 2).ToArray()));
        weights[0] = 900;
        reference[0] = 900;
        Assert.Equal(1, basis.Weights[0]);
        Assert.Equal(100, basis.Reference[0]);
        Assert.NotEqual(basis.BasisId, new TtWorkloadBasis(weights, reference, [true, true, false, true, true], ["a", "b"], ["high"]).BasisId);
        Assert.Null(basis.Score([1, 0, 1, 0, 0]));
        Assert.Null(basis.Score([double.NaN, 0, 0, 0, 0]));
        Assert.Throws<ArgumentException>(() => new TtWorkloadBasis([0, 0, 0, 0, 0], reference, [true, true, true, true, true], [], []));
    }

    [Fact]
    public void FutureQuotaChangesCalibrationButNeverFrozenWorkloadBasis()
    {
        var seed = QuotaCostObservationBuilder.Build(ComposedQuotaEvaluatorTests.TimelyData()).First(x => x.HorizonHours == .5);
        var rows = Enumerable.Range(0, 60).Select(i => seed with
        {
            StartUtc = seed.StartUtc.AddHours(i), EndUtc = seed.StartUtc.AddHours(i).AddMinutes(30),
            StartUsed = 0, EndUsed = 2, LowerDelta = 1.9, UpperDelta = 2.1
        }).ToArray();
        var before = TtEvaluator.Evaluate(rows).Scores.Single();
        var changed = rows.Select((x, i) => i >= 20 ? x with { EndUsed = 4, LowerDelta = 3.9, UpperDelta = 4.1 } : x).ToArray();
        var after = TtEvaluator.Evaluate(changed).Scores.Single();
        Assert.NotNull(before.Basis);
        Assert.Equal(before.Basis.BasisId, after.Basis!.BasisId);
        Assert.Equal(before.HeldOutTt, after.HeldOutTt);
        Assert.True(after.QuotaPointsPerTt > before.QuotaPointsPerTt);
        Assert.Equal(20, before.HeldOutIntervals);
        Assert.Equal(0, before.UnsupportedIntervals);
        Assert.True(before.ScalarMae < before.ZeroLoss);
        var revisedOutcome = TtEvaluator.Evaluate(rows.Select((x, i) => i >= 40
            ? x with { EndUsed = 8, LowerDelta = 7.9, UpperDelta = 8.1 } : x).ToArray()).Scores.Single();
        Assert.Equal(before.Basis.BasisId, revisedOutcome.Basis!.BasisId);
        Assert.Equal(before.QuotaPointsPerTt, revisedOutcome.QuotaPointsPerTt);
        Assert.Equal(before.HeldOutTt, revisedOutcome.HeldOutTt);
        Assert.NotEqual(before.ScalarMae, revisedOutcome.ScalarMae);
        var unsupported = rows.Select((x, i) => i == 59 ? x with
        { Features = x.Features with { ModelTokenShares = new Dictionary<string, double> { ["unseen"] = 1 } } } : x).ToArray();
        var partial = TtEvaluator.Evaluate(unsupported).Scores.Single();
        Assert.Equal(19, partial.HeldOutIntervals);
        Assert.Equal(1, partial.UnsupportedIntervals);
        Assert.True(partial.UnsupportedTokens > 0);
        Assert.Null(before.Basis.Score(rows[0] with { TokenCategories = [0, 0, 0, 0, 0] }));
        Assert.Empty(TtEvaluator.Evaluate(rows.Select(x => x with { Cohort = x.Cohort with { AccountKey = null } }).ToArray()).Scores);
    }
}
