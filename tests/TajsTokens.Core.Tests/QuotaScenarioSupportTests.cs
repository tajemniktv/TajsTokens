using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaScenarioSupportTests
{
    private static QuotaCostObservation Work(double low, double high, long input, long output)
    {
        var row = QuotaCostIdentificationTests.Work(QuotaCostEvaluationTests.Observations(1)[0], ("A", 0, input), ("B", 3, output));
        return row with { LowerDelta = low, UpperDelta = high };
    }
    private static QuotaScenarioSupportResult Analyze(QuotaCostObservation[] rows, QuotaCostObservation request, QuotaCostSupportAssumptions? assumptions = null) =>
        QuotaScenarioSupport.Analyze(rows, rows[0].Cohort, rows[0].HorizonHours, request.Features.JointWorkload!, assumptions ?? new(0, 0, 0));

    [Fact]
    public void FamiliarMixtureHasUsefulBoundsDespiteUnidentifiedIndividualWeights()
    {
        var mix = Work(9, 11, 1_000_000, 1_000_000);
        var familiar = Analyze([mix], mix);
        Assert.Equal(QuotaScenarioSupportStatus.Bounded, familiar.Status);
        Assert.Equal(9, familiar.LowerCost!.Value, 7); Assert.Equal(11, familiar.UpperCost!.Value, 7);
        var volume = Analyze([mix], Work(0, 0, 2_000_000, 2_000_000));
        Assert.Equal(18, volume.LowerCost!.Value, 7); Assert.Equal(22, volume.UpperCost!.Value, 7);
        var edit = Analyze([mix], Work(0, 0, 2_000_000, 0));
        Assert.Equal(0, edit.LowerCost!.Value, 7); Assert.Equal(22, edit.UpperCost!.Value, 7);
        var contrast = Analyze([mix, Work(14, 16, 2_000_000, 1_000_000)], Work(0, 0, 2_000_000, 0));
        Assert.Equal(6, contrast.LowerCost!.Value, 7); Assert.Equal(14, contrast.UpperCost!.Value, 7);
    }

    [Fact]
    public void ReplicationCannotReplaceContrastOrManufacturePrecision()
    {
        var mix = Work(9, 11, 1_000_000, 1_000_000);
        var result = Analyze(Enumerable.Repeat(mix, 20).ToArray(), Work(0, 0, 2_000_000, 0));
        Assert.Equal(0, result.LowerCost!.Value, 7); Assert.Equal(22, result.UpperCost!.Value, 7);
        Assert.True(result.VertexSystems < 10);
    }

    [Fact]
    public void NewCoordinateIsUnboundedRatherThanReceivingAnInvisiblePooledPrice()
    {
        var mix = Work(9, 11, 1_000_000, 1_000_000);
        var novel = QuotaCostIdentificationTests.Work(mix, ("new-model", 0, 1_000_000));
        var result = Analyze([mix], novel);
        Assert.Equal(QuotaScenarioSupportStatus.Unbounded, result.Status);
        Assert.Equal(0, result.LowerCost); Assert.Null(result.UpperCost);
        Assert.Single(result.UnobservedRequestedCoordinates);
    }

    [Fact]
    public void ExternalActivityAndLagRemainExplicitSensitivityAssumptions()
    {
        var mix = Work(9, 11, 1_000_000, 1_000_000);
        var unrestricted = Analyze([mix], mix, new(null, 0, 0));
        Assert.Equal(0, unrestricted.LowerCost); Assert.Equal(11, unrestricted.UpperCost!.Value, 7);
        var bounded = Analyze([mix], mix, new(2, 0, 0));
        Assert.Equal(7, bounded.LowerCost!.Value, 7);
        var sensitivity = Analyze([mix], mix, new(0, 1, .5));
        Assert.Equal(7.5, sensitivity.LowerCost!.Value, 7); Assert.Equal(12.5, sensitivity.UpperCost!.Value, 7);
        var empty = Work(9, 11, 0, 0);
        Assert.Equal(QuotaScenarioSupportStatus.Infeasible, Analyze([empty], mix).Status);
        Assert.Equal(QuotaScenarioSupportStatus.Unbounded, Analyze([empty], mix, new(null, 0, 0)).Status);
    }

    [Fact]
    public void ConflictingConstraintsAreReportedWithoutAutomaticallyWideningBounds()
    {
        var mix = Work(9, 11, 1_000_000, 1_000_000);
        var impossible = Work(15, 16, 1_000_000, 1_000_000);
        var result = Analyze([mix, impossible], mix);
        Assert.Equal(QuotaScenarioSupportStatus.Infeasible, result.Status);
        Assert.Null(result.LowerCost); Assert.Null(result.UpperCost);
        Assert.Equal(new(0, 0, 0), result.Assumptions);
    }

    [Fact]
    public void ScopeUnknownMetadataAndResourceBoundsAreExplicit()
    {
        var mix = Work(9, 11, 1_000_000, 1_000_000);
        Assert.Equal("mixed-scope-or-horizon", Analyze([mix, mix with { Cohort = mix.Cohort with { AccountKey = "other" } }], mix).Reason);
        var unknown = QuotaCostIdentificationTests.Work(mix, (null, 0, 1_000_000));
        Assert.Equal("unknown-model-or-coordinate-overflow", Analyze([mix], unknown).Reason);
        Assert.Equal("research-constraint-bound", Analyze(Enumerable.Repeat(mix, 21).ToArray(), mix).Reason);
        var wide = QuotaCostIdentificationTests.Work(mix, Enumerable.Range(0, 5).Select(i => ((string?)$"m{i}", 0, 1_000_000L)).ToArray());
        Assert.Equal("research-dimension-bound", Analyze([wide], wide).Reason);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => QuotaScenarioSupport.Analyze([mix], mix.Cohort, mix.HorizonHours, mix.Features.JointWorkload!, new(0, 0, 0), cancelled.Token));
        Assert.Throws<ArgumentException>(() => Analyze([mix], mix, new(0, -1, 0)));
    }

    [Fact]
    public void NearSingularDesignDoesNotProduceAConfidentNarrowRange()
    {
        var mix = Work(9, 11, 10_000_000_000, 10_000_000_000);
        var contrast = Work(9, 11, 10_000_000_001, 10_000_000_000);
        Assert.Equal(QuotaScenarioSupportStatus.NumericallyUncertain, Analyze([mix, contrast], mix).Status);
    }
}
