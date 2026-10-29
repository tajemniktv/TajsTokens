using System.Text.Json;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class CoherentQuotaPathsTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
    private static CodexJointWorkload Work(decimal input, decimal output = 0) => new(CodexJointWorkload.Version,
        CodexJointQuantityKind.ConditionalProjection,
        [new(new("model", null), input, 0, 0, output, 0, input + output, 0, 0, 0)], null, 0, input + output);
    private static JointWorkloadPaths Paths(params CodexJointWorkload[][] rows)
    {
        var sample = WorkloadCompositionPrediction.SamplePaths(new([], [],
            [new("session", Origin, Origin, "model", null, 1, 0, 0, 0, 0, 1)], [], Origin, "Synthetic"), Origin, .5, rows[0].Length * 5d / 60);
        return new(sample.Version, sample.InputRevision, sample.Seed, sample.SourceBins, sample.MaximumBlockBins, sample.Context,
            rows.Select(row => new JointWorkloadPath(row.Select((work, i) => new JointWorkloadPathStep(i, true,
                Origin.AddMinutes(i * 5), Origin.AddMinutes((i + 1) * 5), work)), CodexJointWorkloadBuilder.CombineConditional(row.Select(w => (w, 1m))))),
            null, sample.Methodology);
    }
    private static QuotaPathWindow Budget(QuotaWindowKind kind, double remaining = 10, double resetMinutes = 60,
        params QuotaPathReset[] resets) => new(kind, remaining, Origin.AddMinutes(resetMinutes), resets);
    private static CoherentQuotaPathReport Run(JointWorkloadPaths paths, QuotaPathWindow? five = null, QuotaPathWindow? week = null,
        Func<CodexJointWorkload, double>? fiveCost = null, Func<CodexJointWorkload, double>? weekCost = null, int? minutes = null) =>
        CoherentQuotaPaths.Evaluate(paths, five ?? Budget(QuotaWindowKind.FiveHour), week ?? Budget(QuotaWindowKind.Weekly),
            fiveCost ?? (w => (double)w.Cells.Sum(c => c.UncachedInput)), weekCost ?? (w => (double)w.Cells.Sum(c => c.NonReasoningOutput)),
            minutes is { } m ? Origin.AddMinutes(m) : paths.Paths[0].Steps[^1].ToUtc, includeSteps: true);

    [Fact]
    public void SameJointPathsPreserveAntiCorrelatedWindowCrossingsBeforeQuantiles()
    {
        var paths = Paths([Work(10), Work(0), Work(0), Work(0)], [Work(0), Work(10), Work(0), Work(0)],
            [Work(0), Work(0), Work(0, 10), Work(0)], [Work(0), Work(0), Work(0), Work(0, 10)]);
        var fiveInputs = new List<CodexJointWorkload>(); var weekInputs = new List<CodexJointWorkload>();
        var report = Run(paths, fiveCost: w => { fiveInputs.Add(w); return (double)w.Cells.Sum(c => c.UncachedInput); },
            weekCost: w => { weekInputs.Add(w); return (double)w.Cells.Sum(c => c.NonReasoningOutput); });
        Assert.Equal(4, report.CrossingPaths); Assert.Equal(1, report.CrossingPathShare);
        Assert.Equal(20, report.CrossingConditionalBinEndMinutesP90);
        // Each marginal has two non-crossing paths, so its unconditional 90th percentile is beyond the target.
        Assert.Equal(2, report.Outcomes.Count(o => o.CrossingWindowCandidates.Contains(QuotaWindowKind.FiveHour)));
        Assert.Equal(2, report.Outcomes.Count(o => o.CrossingWindowCandidates.Contains(QuotaWindowKind.Weekly)));
        Assert.Equal(fiveInputs.Count, weekInputs.Count);
        for (int i = 0; i < fiveInputs.Count; i++) Assert.Same(fiveInputs[i], weekInputs[i]);
    }

    [Fact]
    public void NoCrossingMassIsNotRenormalizedAndConditionalTimingExcludesIt()
    {
        var report = Run(Paths([Work(10)], [Work(0)], [Work(0)], [Work(0)]));
        Assert.Equal(.25, report.CrossingPathShare); Assert.Equal(.75, report.NoCrossingPathShare);
        Assert.Equal(5, report.CrossingConditionalBinEndMinutesP50);
        var none = Run(Paths([Work(0)])); Assert.Null(none.CrossingConditionalBinEndMinutesP10);
        Assert.Equal(1, none.NoCrossingPathShare);
    }

    [Fact]
    public void UnknownResetEndsJointSupportWithoutInventingARefill()
    {
        var report = Run(Paths([Work(5)]), Budget(QuotaWindowKind.FiveHour, 3, 2));
        var result = Assert.Single(report.Outcomes); Assert.Equal("unresolved", result.State);
        Assert.Equal("unsupported-reset", result.Reason); Assert.Equal(Origin.AddMinutes(2), result.KnownThroughUtc);
        Assert.Equal(1, report.UnresolvedPathShare); Assert.Equal(0, report.NoCrossingPaths);
        var exactTarget = Run(Paths([Work(0)]), Budget(QuotaWindowKind.FiveHour, 3, 5));
        Assert.Equal(1, exactTarget.UnresolvedPaths);
        var otherAtBoundary = Run(Paths([Work(0, 5)]), Budget(QuotaWindowKind.FiveHour, 3, 2), Budget(QuotaWindowKind.Weekly, 2));
        Assert.Equal(1, otherAtBoundary.UnresolvedPaths); Assert.Equal("reset-order-tie", otherAtBoundary.Outcomes[0].Reason);
        var earlier = Run(Paths([Work(10)]), Budget(QuotaWindowKind.FiveHour, 1, 2));
        Assert.Equal(1, earlier.CrossingPaths); Assert.Equal(Origin.AddMinutes(2), earlier.Outcomes[0].FirstCrossingThroughUtc);
    }

    [Fact]
    public void ExplicitResetSplitsBothWindowsAtOneBoundaryAndUsesDeclaredCapacity()
    {
        var reset = new QuotaPathReset(Origin.AddMinutes(2), Origin.AddMinutes(60), 1, Origin);
        var budget = Budget(QuotaWindowKind.FiveHour, 3, 2, reset); string before = JsonSerializer.Serialize(budget);
        var result = Run(Paths([Work(5)]), budget);
        Assert.Equal(1, result.ExplicitScenarioResets); Assert.Equal(1, result.CrossingPaths);
        Assert.Equal(Origin.AddMinutes(2), result.Outcomes[0].FirstCrossingFromUtc);
        Assert.Equal(Origin.AddMinutes(5), result.Outcomes[0].FirstCrossingThroughUtc);
        var refill = budget with { Resets = [reset with { RemainingPercent = 4 }] };
        Assert.Equal(1, Run(Paths([Work(5)]), refill).NoCrossingPaths);
        Assert.Equal(before, JsonSerializer.Serialize(budget));
        var zero = budget with { Resets = [reset with { RemainingPercent = 0 }] };
        Assert.Equal(Origin.AddMinutes(2), Run(Paths([Work(0)]), zero).Outcomes[0].FirstCrossingThroughUtc);
    }

    [Fact]
    public void ExactOldBudgetExhaustionAtResetIsAnOrderingTie()
    {
        var reset = new QuotaPathReset(Origin.AddMinutes(2), Origin.AddMinutes(60), 4, Origin);
        var result = Run(Paths([Work(5)]), Budget(QuotaWindowKind.FiveHour, 2, 2, reset));
        Assert.Equal(1, result.UnresolvedPaths); Assert.Equal("reset-order-tie", result.Outcomes[0].Reason);
    }

    [Fact]
    public void FutureAssumptionsAndBrokenPathsAreRejectedRatherThanZeroFilled()
    {
        var reset = new QuotaPathReset(Origin.AddMinutes(2), Origin.AddMinutes(60), 5, Origin.AddSeconds(1));
        Assert.Throws<ArgumentException>(() => Run(Paths([Work(5)]), Budget(QuotaWindowKind.FiveHour, 3, 2, reset)));
        Assert.Throws<ArgumentException>(() => Run(Paths([Work(5)]), Budget(QuotaWindowKind.Weekly)));
        var paths = Paths([Work(1), Work(1)]);
        var broken = new JointWorkloadPaths(paths.Version, paths.InputRevision, paths.Seed, paths.SourceBins, paths.MaximumBlockBins,
            paths.Context, [new(paths.Paths[0].Steps.Skip(1), paths.Paths[0].Aggregate)], null, paths.Methodology);
        Assert.Throws<ArgumentException>(() => Run(broken));
        var invalid = Run(paths, fiveCost: _ => double.NaN);
        Assert.Equal("invalid-step-cost", invalid.Outcomes[0].Reason); Assert.Equal(1, invalid.UnresolvedPathShare);
        Assert.Equal(Origin, invalid.Outcomes[0].KnownThroughUtc);
    }

    [Fact]
    public void AlreadyExhaustedOriginAndFuturePathChangesKeepTheirOwnTargets()
    {
        var paths = Paths([Work(0), Work(100)]); var initial = Run(paths, Budget(QuotaWindowKind.FiveHour, 0));
        Assert.Equal(0, initial.CrossingConditionalBinEndMinutesP50);
        var early = Run(paths, minutes: 5);
        var changed = Run(Paths([Work(0), Work(999999)]), minutes: 5);
        Assert.Equal(JsonSerializer.Serialize(early), JsonSerializer.Serialize(changed));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CoherentQuotaPaths.Evaluate(paths,
            Budget(QuotaWindowKind.FiveHour), Budget(QuotaWindowKind.Weekly), _ => 1, _ => 1,
            Origin.AddMinutes(5), cancelled.Token));
    }

    private static CodexForecastDataset PairedData()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData(); var start = data.Quota[0].CapturedAtUtc;
        return data with { Quota = data.Quota.SelectMany((q, i) => new[]
        {
            q with { Lane = "secondary" },
            q with { Kind = QuotaWindowKind.FiveHour, WindowMinutes = 300, Lane = "primary", UsedPercent = i % 20 * 4,
                ResetsAtUtc = start.AddHours((i / 20 + 1) * 5) },
        }).ToArray() };
    }

    [Fact]
    public void NativeStudyPairsSemanticWindowsAcrossSlotsAndUsesOnlyMatureTraining()
    {
        var data = PairedData(); var report = CoherentQuotaPathStudy.Evaluate(data, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(65, report.AlignedWindowPairs); Assert.NotEmpty(report.Cases);
        Assert.All(report.Cases, c =>
        {
            Assert.All(c.Fits, f => { Assert.Equal(20, f.TrainingRows); Assert.True(f.TrainingThroughUtc <= c.OriginUtc); Assert.True(f.TrainingAvailableUtc <= c.OriginUtc); });
            Assert.Equal("primary", c.Fits.Single(f => f.Window == QuotaWindowKind.FiveHour).ReportedSlot);
            Assert.Equal("secondary", c.Fits.Single(f => f.Window == QuotaWindowKind.Weekly).ReportedSlot);
            Assert.Equal(4, c.Horizons.Count); Assert.All(c.Horizons, h => { Assert.Equal(64, h.Paths); Assert.Equal(0, h.ExplicitScenarioResets); });
        });
        var moved = data with { Quota = data.Quota.Select(q => q.Kind == QuotaWindowKind.Weekly ? q with { Lane = "primary" } : q).ToArray() };
        Assert.Equal(report.AlignedWindowPairs, CoherentQuotaPathStudy.Evaluate(moved, ForecastReplayAvailability.CollectedByOrigin).AlignedWindowPairs);
    }

    [Fact]
    public void NativeStudyDoesNotBorrowAnotherAccountClockOrUnavailableTraining()
    {
        var data = PairedData();
        var wrongAccount = data with { Quota = data.Quota.Select(q => q.Kind == QuotaWindowKind.FiveHour ? q with { AccountKey = "other" } : q).ToArray() };
        Assert.Equal(0, CoherentQuotaPathStudy.Evaluate(wrongAccount, ForecastReplayAvailability.CollectedByOrigin).AlignedWindowPairs);
        var wrongClock = data with { Quota = data.Quota.Select(q => q.Kind == QuotaWindowKind.FiveHour ? q with
            { CapturedAtUtc = q.CapturedAtUtc.AddSeconds(1), CollectedAtUtc = q.CollectedAtUtc?.AddSeconds(1) } : q).ToArray() };
        Assert.Equal(0, CoherentQuotaPathStudy.Evaluate(wrongClock, ForecastReplayAvailability.CollectedByOrigin).AlignedWindowPairs);
        var late = data with { Tokens = data.Tokens.Select(t => t with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray() };
        var qualified = CoherentQuotaPathStudy.Evaluate(late, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Empty(qualified.Cases); Assert.True(qualified.WithheldOrigins["insufficient-mature-paired-training"] > 0);
        Assert.NotEmpty(CoherentQuotaPathStudy.Evaluate(late, ForecastReplayAvailability.ReconstructedEventTime).Cases);
    }
}
