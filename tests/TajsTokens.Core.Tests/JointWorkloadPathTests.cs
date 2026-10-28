using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class JointWorkloadPathTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    private static CodexForecastDataset Data() => new([], [], Enumerable.Range(0, 6).Select(i => new CodexPredictiveTokenEvent("s",
        Origin.AddMinutes(-30 + (i + 1) * 5), Origin.AddMinutes(-30 + (i + 1) * 5), i % 2 == 0 ? "A" : "B", null,
        i % 2 == 0 ? 1000 * (i + 1) : 0, 0, 0, i % 2 == 1 ? 1000 * (i + 1) : 0, 0, 1000 * (i + 1))).ToArray(), [], Origin, "fixture");

    [Fact]
    public void WholeJointBinsAndContiguousRunsPreserveVolumeCompositionPairings()
    {
        var paths = WorkloadCompositionPrediction.SamplePaths(Data(), Origin, .5, .5);
        Assert.Equal(64, paths.Paths.Count); Assert.Equal(6, paths.SourceBins);
        foreach (var path in paths.Paths)
        {
            Assert.Equal(6, path.Steps.Count);
            Assert.Equal(Origin, path.Steps[0].FromUtc); Assert.Equal(Origin.AddMinutes(30), path.Steps[^1].ToUtc);
            int run = 0;
            for (int i = 0; i < path.Steps.Count; i++)
            {
                var step = path.Steps[i]; run = step.StartsNewBlock ? 1 : run + 1;
                Assert.InRange(run, 1, 3);
                if (!step.StartsNewBlock) Assert.Equal(path.Steps[i - 1].SourceBin + 1, step.SourceBin);
                if (i > 0) Assert.Equal(path.Steps[i - 1].ToUtc, step.FromUtc);
                var cell = Assert.Single(step.Workload.Cells);
                Assert.Equal(1000 * (step.SourceBin + 1), cell.ReportedTotal);
                Assert.Equal(step.SourceBin % 2 == 0 ? "A" : "B", cell.Coordinate!.Model);
                Assert.Equal(step.SourceBin % 2 == 0 ? cell.ReportedTotal : 0, cell.UncachedInput);
                Assert.Equal(step.SourceBin % 2 == 1 ? cell.ReportedTotal : 0, cell.NonReasoningOutput);
                Assert.Equal(CodexJointQuantityKind.ConditionalProjection, step.Workload.Kind);
                Assert.Equal(0, step.Workload.EvidenceObservations);
            }
            Assert.Equal(path.Steps.Sum(x => x.Workload.ReportedTokens), path.Aggregate.ReportedTokens);
            Assert.True(path.Aggregate.IsComplete);
        }
        Assert.Equal(paths.Paths.Average(x => x.Aggregate.ReportedTokens), paths.Mean!.ReportedTokens);
    }

    [Fact]
    public void PartialLastBinScalesEveryCategoryByItsElapsedDuration()
    {
        var paths = WorkloadCompositionPrediction.SamplePaths(Data(), Origin, .5, 7d / 60);
        Assert.All(paths.Paths, path =>
        {
            Assert.Equal(2, path.Steps.Count);
            var last = path.Steps[^1]; Assert.Equal(TimeSpan.FromMinutes(2), last.ToUtc - last.FromUtc);
            Assert.Equal(1000m * (last.SourceBin + 1) * .4m, last.Workload.ReportedTokens);
            Assert.Equal(Origin.AddMinutes(7), last.ToUtc);
        });
    }

    [Fact]
    public void MissingContextDiffersFromRecordedPausesAndNeverAdmitsFutureOrLateRows()
    {
        var data = Data();
        var empty = data with { Tokens = [] };
        Assert.Empty(WorkloadCompositionPrediction.SamplePaths(empty, Origin, .5, .5).Paths);
        var zeros = data with { Tokens = data.Tokens.Select(x => x with { UncachedInputTokens = 0, NonReasoningOutputTokens = 0, ReportedTotalTokens = 0 }).ToArray() };
        Assert.All(WorkloadCompositionPrediction.SamplePaths(zeros, Origin, .5, .5).Paths, x => Assert.Equal(0, x.Aggregate.ReportedTokens));
        var amended = data with { Tokens = data.Tokens.Concat([
            data.Tokens[0] with { ObservedAtUtc = Origin.AddSeconds(1), Model = "future", ReportedTotalTokens = 99999 },
            data.Tokens[0] with { CapturedAtUtc = Origin.AddDays(1), Model = "late" },
            data.Tokens[0] with { CapturedAtUtc = null, Model = "unknown-clock" }]).ToArray() };
        var original = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5);
        var result = WorkloadCompositionPrediction.SamplePaths(amended, Origin, .5, .5);
        Assert.Equal(original.InputRevision, result.InputRevision);
        Assert.Equal(original.Mean, result.Mean);
    }

    [Fact]
    public void CacheProvesWarmReuseMissingOutputRegenerationAndNarrowRevisionInvalidation()
    {
        var data = Data(); var cache = new WorkloadCompositionPrediction.PathCache();
        var original = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, cache: cache);
        Assert.Same(original, WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, cache: cache));
        Assert.Equal(1, cache.Hits); Assert.Equal(1, cache.Misses);
        var regenerated = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, cache: new());
        Assert.Equal(original.InputRevision, regenerated.InputRevision);
        Assert.Equal(original.Paths.Select(x => x.Aggregate.ReportedTokens), regenerated.Paths.Select(x => x.Aggregate.ReportedTokens));
        var changed = data with { Tokens = data.Tokens.Select((x, i) => i == 0 ? x with { UncachedInputTokens = x.UncachedInputTokens * 2, ReportedTotalTokens = x.ReportedTotalTokens * 2 } : x).ToArray() };
        var updated = WorkloadCompositionPrediction.SamplePaths(changed, Origin, .5, .5, cache: cache);
        Assert.NotEqual(original.InputRevision, updated.InputRevision); Assert.NotEqual(original.Mean, updated.Mean);
        Assert.Equal(original.Seed, updated.Seed);
        Assert.Equal(original.Paths.SelectMany(x => x.Steps).Select(x => x.SourceBin), updated.Paths.SelectMany(x => x.Steps).Select(x => x.SourceBin));
        var outside = data with { Tokens = data.Tokens.Append(data.Tokens[0] with { ObservedAtUtc = Origin.AddHours(-1) }).ToArray() };
        Assert.Same(original, WorkloadCompositionPrediction.SamplePaths(outside, Origin, .5, .5, cache: cache));
        for (uint seed = 1; seed <= 12; seed++) WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, seed: seed, cache: cache);
        Assert.Equal(WorkloadCompositionPrediction.PathCache.MaximumEntries, cache.Count);
        var evicted = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, cache: cache);
        Assert.NotSame(original, evicted); Assert.Equal(original.Mean, evicted.Mean);
    }

    [Fact]
    public void IndexedAndFlatPathsAgreeAndCollectionsAreImmutable()
    {
        var data = Data(); var index = new CodexForecastFeatureIndex(data);
        var flat = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5);
        var indexed = WorkloadCompositionPrediction.SamplePaths(index, Origin, .5, .5, ForecastReplayAvailability.CollectedByOrigin,
            WorkloadCompositionPrediction.DefaultPathSeed, new(), default);
        Assert.Equal(flat.InputRevision, indexed.InputRevision); Assert.Equal(flat.Mean, indexed.Mean);
        Assert.Throws<NotSupportedException>(() => ((IList<JointWorkloadPath>)indexed.Paths).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<JointWorkloadPathStep>)indexed.Paths[0].Steps).Clear());
    }

    [Fact]
    public void CollectionClockRevisionInvalidatesOnlyTheWorkCacheAndPollingToleranceIsSupported()
    {
        var data = Data(); var cache = new WorkloadCompositionPrediction.PathCache();
        var original = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, cache: cache);
        var changed = data with { Tokens = data.Tokens.Select((x, i) => i == 0 ? x with { CapturedAtUtc = Origin.AddSeconds(-1) } : x).ToArray() };
        var updated = WorkloadCompositionPrediction.SamplePaths(changed, Origin, .5, .5, cache: cache);
        Assert.NotEqual(original.InputRevision, updated.InputRevision); Assert.Equal(original.Mean, updated.Mean);
        var quotaOnly = data with { Quota = ComposedQuotaEvaluatorTests.TimelyData().Quota };
        Assert.Same(original, WorkloadCompositionPrediction.SamplePaths(quotaOnly, Origin, .5, .5, cache: cache));
        var longTarget = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, 2 + 4d / 60);
        Assert.All(longTarget.Paths, x => Assert.Equal(Origin.AddHours(2).AddMinutes(4), x.Steps[^1].ToUtc));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5, cache: cache, token: cancelled.Token));
    }

    [Fact]
    public void UnknownAndOverflowAmountsRemainQuantitiesWithoutCreatingSourceObservations()
    {
        var row = Data().Tokens[0];
        var data = Data() with { Tokens = Enumerable.Range(0, 258).Select(i => row with { Model = i == 0 ? null : $"m{i:D3}", ReportedTotalTokens = 100, UncachedInputTokens = 100 }).ToArray() };
        var paths = WorkloadCompositionPrediction.SamplePaths(data, Origin, .5, .5);
        Assert.All(paths.Paths, path =>
        {
            Assert.True(path.Aggregate.IsComplete);
            Assert.Equal(path.Steps.Sum(x => x.Workload.ReportedTokens), path.Aggregate.ReportedTokens);
            Assert.Equal(0, path.Aggregate.EvidenceObservations);
            Assert.True(path.Aggregate.Cells.Count <= CodexJointWorkload.MaximumCoordinates);
        });
        Assert.True(paths.Mean!.MissingModelTokens > 0); Assert.True(paths.Mean.Overflow!.ReportedTotal > 0);
    }
}
