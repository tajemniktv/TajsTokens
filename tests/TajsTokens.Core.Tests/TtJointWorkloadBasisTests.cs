using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class TtJointWorkloadBasisTests
{
    internal static readonly CodexWorkloadCoordinate Model = new("fixture-model", null);
    internal static TtJointWorkloadBasis Basis(decimal input = 2) => new(TtJointWorkloadBasis.Version, TtJointDimensions.Model,
        "Synthetic research ruler; model/category only; no effort multiplier; fixed standard short-context reference, not billing.",
        "fixture-weight per token", "fixture-v1", ["fixture-source/v1"],
        [new(Model, TtTokenCategory.UncachedInput, input), new(Model, TtTokenCategory.NonReasoningOutput, 4)],
        [new(Model, TtTokenCategory.UncachedInput, 100), new(Model, TtTokenCategory.NonReasoningOutput, 50)]);
    internal static CodexJointWorkload Workload(long input = 200) => CodexJointWorkloadBuilder.Build(
        [new("session", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Model.Model, "high", input, 0, 0, 50, 0, input + 50)]);

    [Fact]
    public void CanonicalIdentityIsOrderScaleCultureAndReferencePartitionIndependentAndImmutable()
    {
        var basis = Basis();
        var weights = basis.Weights.Reverse().Select(x => x with { Weight = x.Weight * 1.00m }).ToArray();
        var reference = new[] { new TtJointReferenceAmount(Model, TtTokenCategory.NonReasoningOutput, 50.00m),
            new(Model, TtTokenCategory.UncachedInput, 25), new(Model, TtTokenCategory.UncachedInput, 75), new(Model, TtTokenCategory.CacheRead, 0) };
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            var equivalent = new TtJointWorkloadBasis(basis.Contract, basis.Dimensions, basis.Convention, basis.WeightUnit,
                basis.Vintage, ["fixture-source/v1", "fixture-source/v1"], weights, reference);
            Assert.Equal(basis.BasisId, equivalent.BasisId);
            Assert.Equal(400, equivalent.ReferenceScore);
            Assert.Equal(1.5m, equivalent.ScoreExact(Workload()));
            weights[0] = weights[0] with { Weight = 999 }; reference[0] = reference[0] with { Tokens = 999 };
            Assert.Equal(basis.BasisId, equivalent.BasisId);
            Assert.Equal(1.5m, equivalent.ScoreExact(Workload()));
            Assert.Throws<NotSupportedException>(() => ((IList<TtJointWeight>)equivalent.Weights)[0] = weights[0]);
            var roundtrip = JsonSerializer.Deserialize<TtJointWorkloadBasis>(JsonSerializer.Serialize(equivalent))!;
            Assert.Equal(basis.BasisId, roundtrip.BasisId);
            Assert.Equal(1.5m, roundtrip.ScoreExact(Workload()));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void ReferenceIsOneAndScalingWorkIsAdditiveWhileEveryDefinitionChangeHasANewIdentity()
    {
        var basis = Basis();
        var basket = CodexJointWorkloadBuilder.Build([new("s", DateTimeOffset.UnixEpoch, null, Model.Model, null, 100, 0, 0, 50, 0, 150)]);
        Assert.Equal(1m, basis.ScoreExact(basket));
        Assert.Equal(2m, basis.ScoreExact(CodexJointWorkloadBuilder.Build([
            new("s", DateTimeOffset.UnixEpoch, null, Model.Model, null, 200, 0, 0, 100, 0, 300)])));
        foreach (var changed in new[]
        {
            new TtJointWorkloadBasis(basis.Contract, basis.Dimensions, basis.Convention + " changed", basis.WeightUnit, basis.Vintage, basis.SourceSnapshotIds, basis.Weights, basis.Reference),
            new(basis.Contract, basis.Dimensions, basis.Convention, "other-unit", basis.Vintage, basis.SourceSnapshotIds, basis.Weights, basis.Reference),
            new(basis.Contract, basis.Dimensions, basis.Convention, basis.WeightUnit, "other-vintage", basis.SourceSnapshotIds, basis.Weights, basis.Reference),
            new(basis.Contract, basis.Dimensions, basis.Convention, basis.WeightUnit, basis.Vintage, ["new-price-snapshot"], basis.Weights, basis.Reference),
            new(basis.Contract, basis.Dimensions, basis.Convention, basis.WeightUnit, basis.Vintage, basis.SourceSnapshotIds, basis.Weights,
                [new(Model, TtTokenCategory.UncachedInput, 200)]),
            Basis(3),
        }) Assert.NotEqual(basis.BasisId, changed.BasisId);
        Assert.Equal(1m, basis.ScoreExact(basket));
    }

    [Fact]
    public void AdditiveExtensionPreservesOldWeightsNormalizationAndScoresWithoutMutatingItsParent()
    {
        var parent = Basis();
        var extended = parent.Extend([new(new("new-model", null), TtTokenCategory.UncachedInput, 10)], "fixture-source/v2");
        Assert.True(extended.IsCompatibleExtensionOf(parent));
        Assert.NotEqual(parent.BasisId, extended.BasisId);
        Assert.Equal(parent.ReferenceScore, extended.ReferenceScore);
        Assert.Equal(parent.ScoreExact(Workload()), extended.ScoreExact(Workload()));
        var novel = CodexJointWorkloadBuilder.Build([new("s", DateTimeOffset.UnixEpoch, null, "new-model", "low", 40, 0, 0, 0, 0, 40)]);
        Assert.Null(parent.ScoreExact(novel)); Assert.Equal(1m, extended.ScoreExact(novel));
        Assert.Throws<ArgumentException>(() => parent.Extend([parent.Weights[0] with { Weight = 99 }], "fixture-source/v2"));
        Assert.False(Basis(3).IsCompatibleExtensionOf(parent));
    }

    [Fact]
    public void SupportIsByCoordinateAndCategoryWithExplicitIgnoredDimensionsAndUnknownIsNotAZeroWeight()
    {
        var basis = Basis();
        Assert.Equal(1.5m, basis.ScoreExact(Workload())); // effort is deliberately outside this ruler.
        Assert.False(basis.TryWeight(new(null, null), TtTokenCategory.UncachedInput, out _));
        Assert.False(basis.TryWeight(Model, TtTokenCategory.ReasoningOutput, out _));
        var withZero = basis.Extend([new(Model, TtTokenCategory.CacheRead, 0)], "zero-price-fixture");
        Assert.True(withZero.TryWeight(Model, TtTokenCategory.CacheRead, out decimal zero)); Assert.Equal(0, zero);
        var tier = new CodexWorkloadCoordinate(Model.Model, null, "standard");
        var tierBasis = new TtJointWorkloadBasis(basis.Contract, TtJointDimensions.Model | TtJointDimensions.ExecutionTier,
            "Executed tier matters in this synthetic ruler", basis.WeightUnit, basis.Vintage, basis.SourceSnapshotIds,
            [new(tier, TtTokenCategory.UncachedInput, 2)], [new(tier, TtTokenCategory.UncachedInput, 100)]);
        Assert.Null(tierBasis.ScoreExact(Workload()));
        Assert.NotEqual(basis.BasisId, tierBasis.BasisId);
        var overflow = new CodexJointWorkload(CodexJointWorkload.Version, CodexJointQuantityKind.ObservedIncrements, [],
            new(null, 1, 0, 0, 0, 0, 1, 1, 0, 0), 0, 0);
        Assert.Null(basis.ScoreExact(overflow));
        Assert.Equal(0m, basis.ScoreExact(CodexJointWorkloadBuilder.Build([])));
        Assert.Null(basis.ScoreExact(CodexJointWorkloadBuilder.Build([new("s", DateTimeOffset.UnixEpoch, null, Model.Model, null, 1, 0, 0, 0, 0, 2)])));
    }

    [Fact]
    public void InvalidOrAmbiguousBasisDefinitionsNeverBecomePlausibleZeroRulers()
    {
        var b = Basis();
        TtJointWorkloadBasis Make(string contract, IReadOnlyList<TtJointWeight> weights, IReadOnlyList<TtJointReferenceAmount> reference) =>
            new(contract, b.Dimensions, b.Convention, b.WeightUnit, b.Vintage, b.SourceSnapshotIds, weights, reference);
        Assert.Throws<ArgumentException>(() => Make("future", b.Weights, b.Reference));
        Assert.Throws<ArgumentException>(() => Make(b.Contract, b.Weights.Append(b.Weights[0]).ToArray(), b.Reference));
        Assert.Throws<ArgumentException>(() => Make(b.Contract, [b.Weights[0] with { Weight = -1 }], b.Reference));
        Assert.Throws<ArgumentException>(() => Make(b.Contract, b.Weights, [new(Model, TtTokenCategory.CacheRead, 1)]));
        Assert.Throws<ArgumentException>(() => Make(b.Contract, [new(Model, TtTokenCategory.UncachedInput, 0)], [new(Model, TtTokenCategory.UncachedInput, 1)]));
        Assert.Throws<ArgumentException>(() => Make(b.Contract, [new(Model, TtTokenCategory.UncachedInput, decimal.MaxValue)], [new(Model, TtTokenCategory.UncachedInput, 2)]));
        Assert.Throws<ArgumentException>(() => Make(b.Contract, [new(Model with { ReasoningEffort = "high" }, TtTokenCategory.UncachedInput, 2)], b.Reference));
    }
}
