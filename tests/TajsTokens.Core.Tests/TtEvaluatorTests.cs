using TajsTokens.Core.Research;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TtEvaluatorTests
{
    [Fact]
    public void SignedErrorSeparatesResetBalancedBiasFromCumulativeMeterEnvelope()
    {
        var seed = QuotaCostObservationBuilder.Build(ComposedQuotaEvaluatorTests.TimelyData()).First(x => x.HorizonHours == .5);
        var rows = Enumerable.Range(0, 44).Select(i => seed with
        {
            StartUtc = seed.StartUtc.AddHours(i), EndUtc = seed.StartUtc.AddHours(i).AddMinutes(30),
            ResetUtc = seed.StartUtc.AddDays(i < 41 ? 7 : 14),
            StartUsed = 0, EndUsed = i < 40 ? 2 : i == 40 ? 4 : 1,
            LowerDelta = i < 40 ? 2 : i == 40 ? 3.5 : .5,
            UpperDelta = i < 40 ? 2 : i == 40 ? 4.5 : 1.5
        }).ToArray();
        var score = TtEvaluator.Evaluate(rows).Scores.Single();
        Assert.Equal(2, score.ResetGenerations);
        Assert.Equal(-.5, score.ScalarBias!.Value, 6); // One -2 interval, three +1 intervals, equal cycle weight.
        Assert.Equal(1, score.CumulativeError!.Value, 6);
        Assert.Equal(-1, score.CumulativeErrorLower!.Value, 6);
        Assert.Equal(3, score.CumulativeErrorUpper!.Value, 6);
        var noOutcomes = TtEvaluator.Evaluate(rows.Take(40).ToArray()).Scores.Single();
        Assert.Null(noOutcomes.ScalarBias);
        Assert.Null(noOutcomes.CumulativeError);
    }

    [Fact]
    public void LaterCompatibleCohortReusesBasisButFitsItsOwnConversion()
    {
        var seed = QuotaCostObservationBuilder.Build(ComposedQuotaEvaluatorTests.TimelyData()).First(x => x.HorizonHours == .5);
        var rows = Enumerable.Range(0, 60).Select(i => seed with
        {
            Cohort = seed.Cohort with { PlanType = i < 20 ? "earlier" : "later" },
            StartUtc = seed.StartUtc.AddHours(i), EndUtc = seed.StartUtc.AddHours(i).AddMinutes(30),
            StartUsed = 0, EndUsed = i < 20 ? 2 : 4,
            LowerDelta = i < 20 ? 1.9 : 3.9, UpperDelta = i < 20 ? 2.1 : 4.1
        }).ToArray();
        var report = TtEvaluator.Evaluate(rows);
        var source = report.Scores.Single(x => !x.IsTransfer && x.Cohort.PlanType == "earlier");
        var transfer = report.Scores.Single(x => x.IsTransfer);
        Assert.Equal(source.Basis!.BasisId, transfer.Basis!.BasisId);
        Assert.Equal("earlier", transfer.BasisCohort!.PlanType);
        Assert.Equal("later", transfer.Cohort.PlanType);
        Assert.Equal(20, transfer.CalibrationIntervals);
        Assert.Equal(20, transfer.HeldOutIntervals);
        Assert.Equal(rows[19].EndUtc, transfer.BasisEndUtc);
        Assert.Equal(rows[39].EndUtc, transfer.CalibrationEndUtc);
        Assert.NotNull(transfer.QuotaPointsPerTt);
        Assert.Null(source.QuotaPointsPerTt);

        foreach (var change in new[] { "account", "source", "profile", "session", "horizon", "overlap" })
        {
            var incompatible = rows.Select((x, i) => i < 20 ? x : change switch
            {
                "account" => x with { Cohort = x.Cohort with { AccountKey = "other" } },
                "source" => x with { Cohort = x.Cohort with { Source = "other" } },
                "profile" => x with { Cohort = x.Cohort with { Profile = "other" } },
                "session" => x with { Cohort = x.Cohort with { SessionId = "other" } },
                "horizon" => x with { HorizonHours = 2 },
                _ => x with { StartUtc = x.StartUtc.AddHours(-10), EndUtc = x.EndUtc.AddHours(-10) }
            }).ToArray();
            Assert.DoesNotContain(TtEvaluator.Evaluate(incompatible).Scores, x => x.IsTransfer);
        }
    }

    [Fact]
    public void ScalarCannotHideChangedRelativeCategoryCostsBehindARefittedScale()
    {
        var seed = QuotaCostObservationBuilder.Build(ComposedQuotaEvaluatorTests.TimelyData()).First(x => x.HorizonHours == .5);
        var start = seed.StartUtc;
        var rows = Enumerable.Range(0, 60).Select(i =>
        {
            var output = i % 2 == 1;
            var cost = i >= 20 && output ? 8d : 2d;
            var origin = start.AddDays(i / 20 * 7).AddHours(i % 20);
            return seed with
            {
                StartUtc = origin, EndUtc = origin.AddMinutes(30), ResetUtc = start.AddDays((i / 20 + 1) * 7),
                StartUsed = 0, EndUsed = cost, LowerDelta = cost - .1, UpperDelta = cost + .1,
                TokenCategories = output ? [0, 0, 0, 10000, 0] : [10000, 0, 0, 0, 0],
                Features = seed.Features with { Tokens = 10000 }
            };
        }).ToArray();
        var score = TtEvaluator.Evaluate(rows).Scores.Single();
        Assert.NotNull(score.Basis);
        // Equal costs during basis fitting produce equal TT; later quota pricing differs 4:1.
        Assert.Equal(score.Basis.Score(rows[0]), score.Basis.Score(rows[1]));
        Assert.Equal(20, score.HeldOutIntervals);
        Assert.Equal(0, score.UnsupportedIntervals);
        Assert.True(score.ScalarMae > 2);
        Assert.True(score.FullVectorMae < score.ScalarMae);
        Assert.True(score.FullVectorLoss < score.ScalarLoss);
        Assert.Equal("research-only-not-promoted", score.Status);
    }

    [Fact]
    public void MissingCalibrationCoverageCannotBecomeAZeroCostConversion()
    {
        var seed = QuotaCostObservationBuilder.Build(ComposedQuotaEvaluatorTests.TimelyData()).First(x => x.HorizonHours == .5);
        var rows = Enumerable.Range(0, 60).Select(i => seed with
        {
            StartUtc = seed.StartUtc.AddHours(i), EndUtc = seed.StartUtc.AddHours(i).AddMinutes(30),
            StartUsed = 0, EndUsed = 2, LowerDelta = 1.9, UpperDelta = 2.1,
            Features = i == 30 ? seed.Features with { EffortTokenShares = new Dictionary<string, double>() } : seed.Features
        }).ToArray();
        var score = TtEvaluator.Evaluate(rows).Scores.Single();
        Assert.NotNull(score.Basis);
        Assert.Equal("insufficient-or-unsupported-calibration", score.Status);
        Assert.Null(score.QuotaPointsPerTt);
        Assert.Null(score.ScalarLoss);
        Assert.Null(score.ScalarBias);
        Assert.Null(score.CumulativeError);
        Assert.NotNull(score.HeldOutTt); // Observed workload scoring does not require a quota conversion.
        var unavailableBasis = TtEvaluator.Evaluate(rows.Select((x, i) => i == 0
            ? x with { Features = x.Features with { ModelTokenShares = new Dictionary<string, double>() } } : x).ToArray()).Scores.Single();
        Assert.Null(unavailableBasis.Basis);
        Assert.Null(unavailableBasis.HeldOutTt);
        Assert.Equal(20, unavailableBasis.UnsupportedIntervals);
    }

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
