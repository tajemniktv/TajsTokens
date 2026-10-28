using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexJointWorkloadTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
    private static CodexPredictiveTokenEvent Row(string? model, long input, long output, string? effort = "high") =>
        new("s", Origin.AddMinutes(-1), Origin.AddMinutes(-1), model, effort, input, 0, 0, output, 0, input + output);
    private static CodexForecastDataset Data(params CodexPredictiveTokenEvent[] rows) => new([], [], rows, [], Origin, "fixture");

    [Fact]
    public void EqualMarginalsKeepDifferentObservedJointCostsAndForecastTheirOwnPairing()
    {
        var a = Data(Row("model-a", 100, 0), Row("model-b", 0, 100));
        var b = Data(Row("model-a", 0, 100), Row("model-b", 100, 0));
        var fa = CodexForecastFeatureBuilder.Build(a, Origin);
        var fb = CodexForecastFeatureBuilder.Build(b, Origin);
        Assert.Equal(fa.Tokens, fb.Tokens);
        Assert.Equal(fa.ModelTokenShares.OrderBy(x => x.Key), fb.ModelTokenShares.OrderBy(x => x.Key));
        Assert.Equal(fa.EffortTokenShares.OrderBy(x => x.Key), fb.EffortTokenShares.OrderBy(x => x.Key));
        decimal Cost(CodexJointWorkload joint) => joint.Cells.Sum(x => x.Coordinate!.Model == "model-a"
            ? x.UncachedInput + 10 * x.NonReasoningOutput : 2 * x.UncachedInput + 20 * x.NonReasoningOutput);
        Assert.Equal(2100, Cost(fa.JointWorkload!));
        Assert.Equal(1200, Cost(fb.JointWorkload!));
        var prediction = new TokenHorizonPrediction(.5, 1000, "fixture", 20, 0, null, null, null, "fixture");
        var pa = WorkloadCompositionPrediction.Project(a, Origin, prediction)!;
        var pb = WorkloadCompositionPrediction.Project(b, Origin, prediction)!;
        Assert.Equal(pa.TokenCategories, pb.TokenCategories);
        Assert.Equal(1000, pa.JointWorkload!.ReportedTokens);
        Assert.Equal(10500, Cost(pa.JointWorkload));
        Assert.Equal(6000, Cost(pb.JointWorkload!));
        var projected = ComposedQuotaValidation.Project(QuotaCostEvaluationTests.Observations(1)[0], pa);
        Assert.Equal(pa.JointWorkload, projected.Features.JointWorkload);
        Assert.Equal(CodexJointQuantityKind.ConditionalProjection, projected.Features.JointWorkload!.Kind);
    }

    [Fact]
    public void BoundedVocabularyConservesEveryCategoryAndSeparatesOverflowFromMissingLabels()
    {
        CodexPredictiveTokenEvent[] rows = Enumerable.Range(0, 320).Select(i => Row($"m-{i:000}", 1, 0))
            .Append(Row(null, 0, 7, null)).ToArray();
        CodexJointWorkload result = CodexJointWorkloadBuilder.Build(rows);
        Assert.Equal(CodexJointWorkload.MaximumCoordinates, result.Cells.Count);
        Assert.Equal(327, result.ReportedTokens);
        Assert.Equal(result.ReportedTokens, result.CategoryTokens);
        Assert.Equal(7, result.MissingModelTokens);
        Assert.Equal(7, result.MissingEffortTokens);
        Assert.Equal(65, result.Overflow!.ReportedTotal);
        Assert.Null(result.Overflow.Coordinate);
        Assert.Equal(7, Assert.Single(result.Cells, x => x.Coordinate!.Model is null).ReportedTotal);
        Assert.Equal(result, CodexJointWorkloadBuilder.Build(rows.Reverse()));
        Assert.All(result.Cells, x => { Assert.Null(x.Coordinate!.ExecutionTier); Assert.Null(x.Coordinate.Product); Assert.Null(x.Coordinate.ContextBand); });
        var projected = CodexJointWorkloadBuilder.Project(result, 654)!;
        Assert.Equal(654, projected.ReportedTokens);
        Assert.Equal(130, projected.Overflow!.ReportedTotal);
        Assert.Equal(14, projected.MissingModelTokens);
    }

    [Fact]
    public void InconsistentOrInvalidRowsRemainDiagnosticAndCannotBecomeACompleteProjectionByCancellation()
    {
        var low = Row("m", 90, 0) with { ReportedTotalTokens = 100 };
        var high = low with { UncachedInputTokens = 110 };
        var result = CodexJointWorkloadBuilder.Build([low, high]);
        Assert.Equal(200, result.ReportedTokens);
        Assert.Equal(200, result.CategoryTokens);
        Assert.Equal(2, result.InconsistentObservations);
        Assert.False(result.IsComplete);
        Assert.Null(CodexJointWorkloadBuilder.Project(result, 100));
        var invalid = CodexJointWorkloadBuilder.Build([low with { UncachedInputTokens = -1 }]);
        Assert.Equal(-1, Assert.Single(invalid.Cells).UncachedInput);
        Assert.Equal(1, invalid.InvalidObservations);
        Assert.False(invalid.IsComplete);
        Assert.Null(CodexJointWorkloadBuilder.Project(invalid, 100));
    }

    [Fact]
    public void ExactObservedAmountsAboveLongRangeAndUnknownZeroRemainRetained()
    {
        var joint = CodexJointWorkloadBuilder.Build([Row(null, long.MaxValue, 0, null), Row(null, long.MaxValue, 0, null), Row("zero", 0, 0)]);
        Assert.Equal((decimal)long.MaxValue * 2, joint.ReportedTokens);
        Assert.Equal(joint.ReportedTokens, joint.CategoryTokens);
        Assert.Equal(3, joint.EvidenceObservations);
        Assert.True(joint.IsComplete);
        Assert.Equal(0, Assert.Single(joint.Cells, x => x.Coordinate!.Model == "zero").ReportedTotal);
        Assert.Null(CodexJointWorkloadBuilder.Project(joint, double.MaxValue));
    }

    [Fact]
    public void CollectionQualifiedFeaturesCachesAndProjectionDoNotSeeFutureOrLateMetadata()
    {
        var timely = Row("timely", 100, 0);
        var late = Row("late", 0, 100) with { CapturedAtUtc = Origin.AddHours(1) };
        var future = Row("future", 0, 100) with { ObservedAtUtc = Origin.AddMinutes(1), CapturedAtUtc = Origin.AddMinutes(1) };
        var data = Data(timely, late, future);
        var index = new CodexForecastFeatureIndex(data);
        var strict = index.Build(Origin, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Equal(100, strict.JointWorkload!.ReportedTokens);
        Assert.Equal("timely", Assert.Single(strict.JointWorkload.Cells).Coordinate!.Model);
        Assert.Equal(strict.JointWorkload, CodexForecastFeatureBuilder.Build(data, Origin, availability: ForecastReplayAvailability.CollectedByOrigin).JointWorkload);
        Assert.Same(strict, index.Build(Origin, ForecastReplayAvailability.CollectedByOrigin));
        var reconstructed = index.BuildReconstructedPrefix(Origin, Origin.AddHours(2));
        Assert.Equal(200, reconstructed.JointWorkload!.ReportedTokens);
        Assert.DoesNotContain(reconstructed.JointWorkload.Cells, x => x.Coordinate!.Model == "future");
    }

    [Fact]
    public void SerializedProjectionIsImmutableVersionedAndKeepsAdditionalDimensionLabelsDistinct()
    {
        var first = new CodexJointWorkloadCell(new("m", "high", "standard", "api", "short"), 1, 2, 3, 4, 5, 15, 1, 0, 0);
        var second = first with { Coordinate = first.Coordinate! with { ContextBand = "long", ExecutionTier = "priority" } };
        var cells = new[] { first, second };
        var joint = new CodexJointWorkload(CodexJointWorkload.Version, CodexJointQuantityKind.ObservedIncrements, cells, null, 0, 0);
        cells[0] = second;
        Assert.Equal(first, joint.Cells[0]);
        string json = JsonSerializer.Serialize(joint);
        Assert.Equal(joint, JsonSerializer.Deserialize<CodexJointWorkload>(json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CodexJointWorkload>(json.Replace(CodexJointWorkload.Version, "future-contract")));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CodexJointWorkload>("{\"Contract\":\"" + CodexJointWorkload.Version + "\"}"));
        Assert.Throws<NotSupportedException>(() => ((IList<CodexJointWorkloadCell>)joint.Cells)[0] = second);
    }

    [Fact]
    public void FrozenMarginalBaselineDistinguishesKnownVocabularyOverflowFromMissingMetadata()
    {
        QuotaCostObservation[] training = QuotaCostEvaluationTests.Observations(20).Select((row, i) => row with
        {
            Features = row.Features with { ModelTokenShares = new Dictionary<string, double> { [$"m-{i:00}"] = 1 } },
        }).ToArray();
        QuotaCostScore score = QuotaCostEvaluation.Evaluate(training, "fixture").Scores.Single(x => x.Candidate == "model-effort");
        string[] names = score.Coefficients.Keys.ToArray();
        Assert.Contains("vocabulary-overflow-model", names);
        var target = training[0] with { Features = training[0].Features with
        { ModelTokenShares = new Dictionary<string, double> { ["known-outside-vocabulary"] = .75 } } };
        var inference = QuotaAccountingModel.CaptureFrozenInference(training, "model-effort", target, 100, default);
        double total = target.Features.Tokens / 1e6;
        Assert.Equal(total * .25, inference.Inputs[Array.IndexOf(names, "unknown-model")], 10);
        Assert.Equal(total * .75, inference.Inputs[Array.IndexOf(names, "vocabulary-overflow-model")], 10);
    }
}
