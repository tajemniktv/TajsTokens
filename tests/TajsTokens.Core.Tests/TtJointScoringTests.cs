using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TtJointScoringTests
{
    private static readonly CodexWorkloadCoordinate Model = new("fixture", null);
    private static TtJointWorkloadBasis Basis(params decimal[] weights) => new(TtJointWorkloadBasis.Version, TtJointDimensions.Model,
        "Synthetic scoring fixture, not an active public ruler", "fixture/token", "fixture-v1", ["fixture/v1"],
        weights.Select((w, i) => new TtJointWeight(Model, (TtTokenCategory)i, w)).ToArray(), [new(Model, TtTokenCategory.UncachedInput, 100)]);
    private static TtScoreAssessment Score(TtQuantityConstraints row) => TtJointScoring.Evaluate(Basis(2, 1, 3, 4, 4), [row]);

    [Fact]
    public void EqualOutputWeightsIdentifyTheScoreWithoutInventingItsSplit()
    {
        var score = Score(new(Model, UncachedInput: 0, CacheRead: 0, CacheWrite: 0, OutputTotal: 10, ReportedTotal: 10));
        Assert.Equal(TtScoreState.Exact, score.State); Assert.Equal(.2m, score.ExactTt);
        Assert.Equal(.2m, score.CoveredSubtotalTt); Assert.Equal(10, score.TotalTokens);
        Assert.Contains("under the named basis", score.Explanation);
    }

    [Fact]
    public void UnknownCacheSplitHasFiniteBoundsAndKnownCacheAndWriteAmountsCanIdentifyTheRest()
    {
        var score = Score(new(Model, CacheWrite: 0, NonReasoningOutput: 20, ReasoningOutput: 0, InputTotal: 100, ReportedTotal: 120));
        Assert.Equal(TtScoreState.FiniteBounds, score.State); Assert.Null(score.ExactTt);
        Assert.Equal(.9m, score.LowerTt); Assert.Equal(1.4m, score.UpperTt); Assert.Equal(.4m, score.CoveredSubtotalTt);
        Assert.Equal(120, score.TotalTokens); Assert.Equal(0, score.KnownUnmappedTokens);
        Assert.Contains("not a confidence interval", score.Explanation);
        var identified = Score(new(Model, CacheRead: 3, CacheWrite: 1, NonReasoningOutput: 0, ReasoningOutput: 0, InputTotal: 10));
        Assert.Equal(TtScoreState.Exact, identified.State); Assert.Equal(.09m, identified.ExactTt);
    }

    [Fact]
    public void UnsupportedMappingPreservesIdentifiedSupportedSubtotalsWithoutInventingBoundsOrAnInfinitePrice()
    {
        var basis = Basis(2, 1, 3, 4, 4);
        var rows = new[] { new TtQuantityConstraints(Model, 100, 0, 0, 0, 0, ReportedTotal: 100),
            new(new("unlisted", null), 0, 0, 0, 50, 0, ReportedTotal: 50) };
        var score = TtJointScoring.Evaluate(basis, rows);
        Assert.Equal(TtScoreState.Partial, score.State); Assert.Null(score.LowerTt); Assert.Null(score.UpperTt); Assert.Null(score.ExactTt);
        Assert.Equal(1, score.CoveredSubtotalTt); Assert.Equal(50, score.KnownUnmappedTokens); Assert.Equal(150, score.TotalTokens);
        Assert.Equal(1, score.UnmappedRows);
        var missing = TtJointScoring.Evaluate(basis, [new(new(null, null), ReportedTotal: 100)]);
        Assert.Equal(TtScoreState.Partial, missing.State); Assert.Equal(100, missing.KnownUnmappedTokens);
        Assert.Null(missing.LowerTt); Assert.Null(missing.UpperTt);
        var mixed = TtJointScoring.Evaluate(Basis(2), [new(Model, CacheWrite: 0, NonReasoningOutput: 0, ReasoningOutput: 0, InputTotal: 100)]);
        Assert.Equal(TtScoreState.Partial, mixed.State); Assert.Equal(0, mixed.KnownUnmappedTokens); // allocation to an unmapped category is unresolved, not proven absent.
        Assert.Equal(1, mixed.UnmappedRows);
    }

    [Fact]
    public void MissingQuantityWithKnownPositiveCoefficientsIsUnboundedButKnownZeroCoefficientsNeedNoQuantity()
    {
        var unbounded = Score(new(Model, UncachedInput: 10));
        Assert.Equal(TtScoreState.Unbounded, unbounded.State); Assert.Equal(.1m, unbounded.LowerTt); Assert.Null(unbounded.UpperTt);
        Assert.Null(unbounded.TotalTokens); Assert.Equal(.1m, unbounded.CoveredSubtotalTt);
        var zero = TtJointScoring.Evaluate(Basis(2, 0, 0, 0, 0), [new(Model, UncachedInput: 0)]);
        Assert.Equal(TtScoreState.Exact, zero.State); Assert.Equal(0, zero.ExactTt); Assert.Null(zero.TotalTokens);
        var unmappedZero = TtJointScoring.Evaluate(Basis(2), [new(Model, UncachedInput: 100, ReportedTotal: 100)]);
        Assert.Equal(TtScoreState.Exact, unmappedZero.State); Assert.Equal(1, unmappedZero.ExactTt);
        Assert.Equal(0, unmappedZero.UnmappedRows);
    }

    [Fact]
    public void NegativeConflictingTotalsAndSourceDiagnosticsRemainInconsistentWithoutAPlausibleSubtotal()
    {
        foreach (var row in new[] { new TtQuantityConstraints(Model, UncachedInput: -1),
            new(Model, UncachedInput: 5, CacheRead: 6, InputTotal: 10), new(Model, InputTotal: 10, OutputTotal: 5, ReportedTotal: 10),
            new(Model, UncachedInput: 10, CacheRead: 0, CacheWrite: 0, InputTotal: 11),
            new(Model, InputTotal: 20, ReportedTotal: 10), new(Model, UncachedInput: 20, ReportedTotal: 10),
            new(Model, UncachedInput: 1, CacheRead: 0, CacheWrite: 0, NonReasoningOutput: 0, ReasoningOutput: 0, ReportedTotal: 1, SourceInconsistent: true) })
        {
            var score = Score(row); Assert.Equal(TtScoreState.Inconsistent, score.State); Assert.Equal(1, score.InconsistentRows);
            Assert.Null(score.CoveredSubtotalTt); Assert.Null(score.LowerTt); Assert.Null(score.UpperTt);
        }
    }

    [Fact]
    public void JointAdapterRetainsUnknownOverflowInconsistencyAndConditionalProvenance()
    {
        var basis = Basis(2, 1, 3, 4, 4);
        var cell = new CodexJointWorkloadCell(Model, 100, 0, 0, 0, 0, 100, 1, 0, 0);
        var joint = new CodexJointWorkload(CodexJointWorkload.Version, CodexJointQuantityKind.ConditionalProjection,
            [cell], new(null, 5, 0, 0, 0, 0, 5, 1, 0, 0), 0, 0);
        var score = TtJointScoring.Evaluate(basis, joint);
        Assert.Equal(TtScoreState.Partial, score.State); Assert.Equal(1, score.CoveredSubtotalTt); Assert.Equal(5, score.KnownUnmappedTokens);
        Assert.Equal(CodexJointQuantityKind.ConditionalProjection, score.Kind);
        Assert.Equal(TtScoreState.Inconsistent, TtJointScoring.Evaluate(basis,
            new(CodexJointWorkload.Version, joint.Kind, [cell with { InconsistentObservations = 2 }], null, 0, 0)).State);
        Assert.Equal(TtJointWorkloadBasisTests.Basis().ScoreExact(TtJointWorkloadBasisTests.Workload()),
            TtJointScoring.Evaluate(TtJointWorkloadBasisTests.Basis(), TtJointWorkloadBasisTests.Workload()).ExactTt);
    }

    [Fact]
    public void MissingActiveTierDoesNotBorrowTheRangeOfKnownTierPrices()
    {
        var standard = Model with { ExecutionTier = "standard" }; var fast = Model with { ExecutionTier = "fast" };
        var basis = new TtJointWorkloadBasis(TtJointWorkloadBasis.Version, TtJointDimensions.Model | TtJointDimensions.ExecutionTier,
            "Synthetic executed-tier ruler", "fixture/token", "fixture", ["fixture"],
            [new(standard, TtTokenCategory.UncachedInput, 2), new(fast, TtTokenCategory.UncachedInput, 4)],
            [new(standard, TtTokenCategory.UncachedInput, 100)]);
        var score = TtJointScoring.Evaluate(basis, [new(Model, 100, 0, 0, 0, 0)]);
        Assert.Equal(TtScoreState.Partial, score.State); Assert.Null(score.LowerTt); Assert.Null(score.UpperTt);
        Assert.Equal(100, score.KnownUnmappedTokens);
    }

    [Fact]
    public void ArithmeticLimitsAreUnavailableAndDoNotMislabelFeasibleSourceConstraints()
    {
        var rescued = Score(new(Model, decimal.MaxValue, 0, 0, 0, 0));
        Assert.Equal(TtScoreState.Exact, rescued.State); Assert.Equal(decimal.MaxValue / 100, rescued.ExactTt);
        var score = Score(new(Model, decimal.MaxValue, 1, 0, 0, 0));
        Assert.Equal(TtScoreState.ArithmeticUnavailable, score.State); Assert.Equal(0, score.InconsistentRows);
        Assert.Null(score.CoveredSubtotalTt); Assert.Null(score.ExactTt);
    }

    [Fact]
    public void NonterminatingBoundsRoundOutwardAndIntermediateProductsDoNotLoseRepresentableScores()
    {
        var bounds = TtJointScoring.Evaluate(Basis(3, 4, 0, 0, 0), [new(Model, CacheWrite: 0,
            NonReasoningOutput: 0, ReasoningOutput: 0, InputTotal: 1)]);
        Assert.Equal(.01m, bounds.LowerTt);
        Assert.Equal(.0133333333333333333333333334m, bounds.UpperTt);
        var tiny = TtJointScoring.Evaluate(Basis(.0000000000000000000000000001m, 0, 0, 0, 0),
            [new(Model, .00000000000000000000000001m, 0, 0, 0, 0)]);
        Assert.Equal(TtScoreState.Exact, tiny.State); Assert.Equal(.0000000000000000000000000001m, tiny.ExactTt);
    }

    [Fact]
    public void ClosedFormExtremaMatchExhaustiveIndependentIntegerAllocationsForHierarchicalConstraints()
    {
        var allocations = (from a in Enumerable.Range(0, 7) from b in Enumerable.Range(0, 7 - a)
            from c in Enumerable.Range(0, 7 - a - b) from d in Enumerable.Range(0, 7 - a - b - c)
            select new decimal[] { a, b, c, d, 6 - a - b - c - d }).ToArray();
        var random = new Random(443);
        for (int trial = 0; trial < 80; trial++)
        {
            decimal[] actual = allocations[random.Next(allocations.Length)];
            decimal?[] known = actual.Select(x => random.Next(2) == 0 ? (decimal?)x : null).ToArray();
            decimal? input = random.Next(2) == 0 ? actual.Take(3).Sum() : null;
            decimal? output = random.Next(2) == 0 ? actual.Skip(3).Sum() : null;
            decimal[] weights = Enumerable.Range(0, 5).Select(i => (decimal)(i == 0 ? 1 : random.Next(5))).ToArray();
            var possible = allocations.Where(x => x.Where((v, i) => known[i] is { } k && k != v).Any() == false &&
                (input is null || x.Take(3).Sum() == input) && (output is null || x.Skip(3).Sum() == output)).ToArray();
            decimal Cost(decimal[] x) => x.Select((v, i) => v * weights[i]).Sum() / 100;
            var score = TtJointScoring.Evaluate(Basis(weights), [new(Model, known[0], known[1], known[2], known[3], known[4], input, output, 6)]);
            Assert.Equal(possible.Min(Cost), score.LowerTt); Assert.Equal(possible.Max(Cost), score.UpperTt);
            Assert.Equal(possible.Min(Cost) == possible.Max(Cost) ? TtScoreState.Exact : TtScoreState.FiniteBounds, score.State);
        }
    }
}
