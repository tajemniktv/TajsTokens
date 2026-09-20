using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class ChronologicalEvidenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private sealed record Outcome(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Reset, double Error);

    [Fact]
    public void OneWeeklyCycleCanSupplyManyBlocksButDuplicatedPollsCannot()
    {
        var rows = Enumerable.Range(0, 12).Select(i => new Outcome(Start.AddHours(i * 6),
            Start.AddHours(i * 6 + 1), Start.AddDays(7), i % 2 == 0 ? -1 : 1)).ToArray();
        var support = ChronologicalEvidence.Describe(rows.Concat(rows).ToArray(), x => x.Start, x => x.End, x => x.Reset, x => x.Error);
        Assert.Equal(24, support.RawObservations);
        Assert.Equal(12, support.NonOverlappingOutcomes);
        Assert.Equal(12, support.EvaluationBlocks);
        Assert.Equal(1, support.DistinctResetCycles);
        Assert.Equal(3, support.DistinctDays);
        Assert.InRange(support.EffectiveSampleSize, 1, 12);
    }

    [Fact]
    public void ResetCrossingsAndOverlappingOutcomesAreExcluded()
    {
        Outcome[] rows = [new(Start, Start.AddHours(2), Start.AddHours(6), 1),
            new(Start.AddHours(1), Start.AddHours(3), Start.AddHours(6), 2),
            new(Start.AddHours(5), Start.AddHours(7), Start.AddHours(6), 3),
            new(Start.AddHours(6), Start.AddHours(7), Start.AddHours(12), 4)];
        var blocks = ChronologicalEvidence.Blocks(rows, x => x.Start, x => x.End, x => x.Reset);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(new[] { 1d, 4d }, blocks.SelectMany(x => x).Select(x => x.Error));
    }

    [Fact]
    public void FixedHorizonCalibrationLearnsBeforeWeeklyResetButResetOutlookDoesNot()
    {
        var reset = Start.AddDays(7);
        var rows = Enumerable.Range(0, 10).Select(i => new QuotaForecastTrial("pace", "0.5h",
            Start.AddHours(i * 6), Start.AddHours(i * 6 + .5), reset, .5, 50, 49,
            null, null, 0, null, false, null)).ToArray();
        var origin = Start.AddDays(3);
        Assert.NotNull(QuotaForecastCalibration.ErrorRadius(QuotaForecastCalibration.CalibrationErrors(rows, origin, reset, .5)));
        Assert.Empty(QuotaForecastCalibration.CalibrationErrors(rows.Select(x => x with { Target = "near-reset-proxy" }), origin, reset, .5));
        Assert.Empty(QuotaForecastCalibration.CalibrationErrors(rows, Start, reset, .5));
    }

    [Fact]
    public void EffectiveSampleDiagnosticDoesNotInventSupportFromConstantResiduals()
    {
        Assert.Equal(1, ChronologicalEvidence.EffectiveSamples(Enumerable.Repeat(2d, 30).ToArray()));
        Assert.True(ChronologicalEvidence.EffectiveSamples(Enumerable.Range(0, 30).Select(x => (double)x).ToArray()) < 15);
    }
}
