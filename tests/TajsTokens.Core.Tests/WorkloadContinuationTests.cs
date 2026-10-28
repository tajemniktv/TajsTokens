using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class WorkloadContinuationTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    private static CodexPredictiveTokenEvent Row(int minutes, string? model, long input = 0, long output = 0) =>
        new("s", Origin.AddMinutes(minutes), Origin.AddMinutes(minutes), model, null, input, 0, 0, output, 0, input + output);
    private static CodexForecastDataset Data(params CodexPredictiveTokenEvent[] rows) => new([], [], rows, [], Origin, "fixture");

    [Fact]
    public void ContinuationKeepsJointAmountsAndUsesElapsedContextRatherThanFirstTokenAge()
    {
        var data = Data(Row(-20, "A", input: 1000), Row(-10, "B", output: 500), Row(-90, "C", input: 1000));
        var recent = WorkloadCompositionPrediction.Continue(data, Origin, .5, 1);
        Assert.Equal(3000, recent.Workload!.ExpectedTokens);
        Assert.Equal(2000, recent.Workload.JointWorkload!.Cells.Single(x => x.Coordinate!.Model == "A").UncachedInput);
        Assert.Equal(1000, recent.Workload.JointWorkload.Cells.Single(x => x.Coordinate!.Model == "B").NonReasoningOutput);
        Assert.Equal(Origin.AddMinutes(-30), recent.Receipt.ObservationFromUtc);
        Assert.Equal(Origin, recent.Receipt.ObservationToUtc);
        Assert.Null(recent.Receipt.TrainingFromUtc); Assert.Null(recent.Receipt.TrainingToUtc);
        Assert.True(recent.Receipt.IncludesElapsedQuietAndToolWaits);
        Assert.Equal(.5, recent.Receipt.ContextHours); Assert.Equal(1, recent.Receipt.HorizonHours);
        var longContext = WorkloadCompositionPrediction.Continue(data, Origin, 2, 1);
        Assert.Equal(1250, longContext.Workload!.ExpectedTokens);
        Assert.Equal(3, longContext.Receipt.Observations);
    }

    [Fact]
    public void MissingContextIsNotZeroButExplicitZeroObservationsCanRemainZero()
    {
        var absent = WorkloadCompositionPrediction.Continue(Data(), Origin, .5, 1);
        Assert.Null(absent.Workload); Assert.Null(absent.Receipt.RecordedTokens); Assert.Null(absent.Receipt.ProjectedTokens);
        Assert.Equal("missing-context", absent.Receipt.Status);
        var zero = WorkloadCompositionPrediction.Continue(Data(Row(-1, "A")), Origin, .5, 1);
        Assert.Equal(0, zero.Workload!.ExpectedTokens); Assert.Equal(0, zero.Workload.JointWorkload!.ReportedTokens);
        Assert.Equal(CodexJointQuantityKind.ConditionalProjection, zero.Workload.JointWorkload.Kind);
        Assert.Null(zero.Receipt.LatestPositiveWorkUtc); Assert.Equal(1, zero.Receipt.Observations);
    }

    [Fact]
    public void BoundaryFutureAndCollectionQualificationAreIndependent()
    {
        var data = Data(Row(-30, "excluded-left", input: 9999), Row(0, "right", input: 100), Row(1, "future", input: 9999),
            Row(-1, "late", input: 1000) with { CapturedAtUtc = Origin.AddDays(1) }, Row(-2, "unknown", input: 1000) with { CapturedAtUtc = null });
        var strict = WorkloadCompositionPrediction.Continue(data, Origin, .5, .5);
        Assert.Equal(100, strict.Workload!.ExpectedTokens); Assert.Single(strict.Workload.ModelShares);
        Assert.True(strict.Workload.ModelShares.ContainsKey("right"));
        var reconstruction = WorkloadCompositionPrediction.Continue(data, Origin, .5, .5, ForecastReplayAvailability.ReconstructedEventTime);
        Assert.Equal(2100, reconstruction.Workload!.ExpectedTokens); Assert.Equal(3, reconstruction.Receipt.Observations);
    }

    [Fact]
    public void UnknownLabelsStayInTheJointVectorWithoutInventingAnEffortCharge()
    {
        var result = WorkloadCompositionPrediction.Continue(Data(Row(-1, null, input: 100)), Origin, .5, 1);
        Assert.Equal(200, result.Workload!.ExpectedTokens); Assert.Equal(200, result.Workload.JointWorkload!.MissingModelTokens);
        Assert.Equal(200, result.Workload.JointWorkload.MissingEffortTokens);
        Assert.Empty(result.Workload.ModelShares); Assert.Empty(result.Workload.EffortShares);
    }

    [Fact]
    public void IncompleteCategoriesWithholdJointProjectionWithoutErasingReportedAmount()
    {
        var incomplete = Row(-1, "A", input: 100) with { ReportedTotalTokens = 200 };
        var result = WorkloadCompositionPrediction.Continue(Data(incomplete), Origin, .5, 1);
        Assert.Null(result.Workload); Assert.Equal(400, result.Receipt.ProjectedTokens);
        Assert.Equal("incomplete-categories", result.Receipt.Status);
    }

    [Fact]
    public void IndexedAndFlatPathsAgreeAndAReplacementDatasetDoesNotReuseOldAmounts()
    {
        var data = Data(Row(-20, "A", input: 100), Row(-10, "B", output: 300));
        var index = new CodexForecastFeatureIndex(data);
        var flat = WorkloadCompositionPrediction.Continue(data, Origin, .5, 1);
        var indexed = WorkloadCompositionPrediction.Continue(index, Origin, .5, 1, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(flat.Receipt, indexed.Receipt); Assert.Equal(flat.Workload!.JointWorkload, indexed.Workload!.JointWorkload);
        var changed = data with { Tokens = [data.Tokens[0] with { UncachedInputTokens = 900, ReportedTotalTokens = 900 }, data.Tokens[1]] };
        Assert.Equal(800, indexed.Workload.ExpectedTokens);
        Assert.Equal(2400, WorkloadCompositionPrediction.Continue(new CodexForecastFeatureIndex(changed), Origin, .5, 1, ForecastReplayAvailability.CollectedByOrigin).Workload!.ExpectedTokens);
    }

    [Fact]
    public void ConstantWorkHasZeroMatchedContextErrorAndLateHistoryCannotPassOperationalReplay()
    {
        var data = ComposedQuotaEvaluatorTests.TimelyData();
        var report = WorkloadContinuationEvaluation.Evaluate(data, ForecastReplayAvailability.CollectedByOrigin);
        Assert.All(report.Scores.Where(x => x.MatchedTargets > 0), x =>
        {
            Assert.Equal(0, x.TokenMae); Assert.Equal(0, x.TokenWape); Assert.Equal(0, x.MeanJointShareL1);
        });
        Assert.Contains(report.Scores, x => x.MatchedTargets > 0);
        var late = data with { Tokens = data.Tokens.Select(x => x with { CapturedAtUtc = data.CapturedAtUtc.AddDays(1) }).ToArray() };
        Assert.All(WorkloadContinuationEvaluation.Evaluate(late, ForecastReplayAvailability.CollectedByOrigin).Scores, x => Assert.Equal(0, x.MatchedTargets));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkloadContinuationEvaluation.Evaluate(data, ForecastReplayAvailability.CollectedByOrigin, cancelled.Token));
    }
}
