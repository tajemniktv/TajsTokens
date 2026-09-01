using TajsTokens.Core.Models;

namespace TajsTokens.Core.Tests;

public sealed class CodexTokenCounterReducerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T10:00:00Z");

    [Fact]
    public void FirstObservation_EmitsCumulativeDeltaWhenNoLastUsageExists()
    {
        var decision = Reduce(Observation("first", 100, 40, 10, 4, 110));

        Assert.Equal(CodexTokenAccountingDecisionKind.FirstObservation, decision.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(60, 40, 0, 6, 4, 110), decision.Delta);
        Assert.Equal(0, decision.NextState!.Epoch);
    }

    [Fact]
    public void MonotonicObservation_PrefersLastUsageWhenCumulativeGapIsLarger()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110));
        var second = CodexTokenCounterReducer.Reduce(
            Observation("second", 160, 80, 30, 10, 190, Last(30, 15, 0, 10, 4, 40)),
            first.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.MonotonicIncrement, second.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(15, 15, 0, 6, 4, 40), second.Delta);
        Assert.Equal(160, second.NextState!.InputTokens);
        Assert.Equal(190, second.NextState.TotalTokens);
    }

    [Fact]
    public void FirstVisibleLifetimeTotal_LargerThanLastUsage_EmitsLastOnly()
    {
        var decision = Reduce(Observation("first", 1000, 700, 100, 20, 1100, Last(30, 20, 0, 8, 2, 38)));

        Assert.Equal(CodexTokenAccountingDecisionKind.FirstObservation, decision.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(10, 20, 0, 6, 2, 38), decision.Delta);
    }

    [Fact]
    public void ExactDuplicate_IsRejectedWithoutAdvancingState()
    {
        var first = Reduce(Observation("same", 100, 40, 10, 4, 110));
        var duplicate = CodexTokenCounterReducer.Reduce(
            Observation("same", 130, 55, 20, 8, 142),
            first.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.Duplicate, duplicate.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, duplicate.Delta);
        Assert.False(duplicate.AdvanceState);
        Assert.Same(first.NextState, duplicate.NextState);
    }

    [Fact]
    public void RepeatedLastUsageOnUnchangedCumulativeSnapshot_DoesNotDoubleCount()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110, Last(100, 40, 0, 10, 4, 110)));
        var rateLimitOnly = CodexTokenCounterReducer.Reduce(
            Observation("rate", 100, 40, 10, 4, 110, Last(100, 40, 0, 10, 4, 110)),
            first.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.NoIncrement, rateLimitOnly.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, rateLimitOnly.Delta);
    }

    [Fact]
    public void UnchangedCumulativeTotal_WinsOverComponentNoiseAndRepeatedLast()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110));
        var unchanged = CodexTokenCounterReducer.Reduce(
            Observation("unchanged", 99, 39, 9, 3, 110, Last(1, 1, 0, 1, 1, 2)),
            first.NextState);
        var recovered = CodexTokenCounterReducer.Reduce(
            Observation("recovered", 110, 45, 15, 5, 120),
            unchanged.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.NoIncrement, unchanged.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, unchanged.Delta);
        Assert.Equal(first.NextState!.InputTokens, unchanged.NextState!.InputTokens);
        Assert.Equal(new CodexTokenAccountingDelta(5, 5, 0, 4, 1, 10), recovered.Delta);
    }

    [Fact]
    public void SmallStaleRegression_IsIgnoredAndRecoveryDoesNotDoubleCount()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110));
        var stale = CodexTokenCounterReducer.Reduce(
            Observation("stale", 99, 39, 9, 3, 109, Last(1, 1, 0, 1, 0, 2)),
            first.NextState);
        var recovered = CodexTokenCounterReducer.Reduce(
            Observation("recovered", 110, 45, 12, 5, 121, Last(10, 5, 0, 2, 1, 11)),
            stale.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.StaleCumulativeRegression, stale.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, stale.Delta);
        Assert.Equal(CodexTokenAccountingDecisionKind.MonotonicIncrement, recovered.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(5, 5, 0, 1, 1, 11), recovered.Delta);
        Assert.Equal(first.NextState, stale.NextState);
    }

    [Fact]
    public void TokscaleStaleRegression_98PercentCase_RetainsBaselineAndRecovers()
    {
        var first = Reduce(Observation("first", 100, 0, 0, 0, 100));
        var stale = CodexTokenCounterReducer.Reduce(
            Observation("stale", 98, 0, 0, 0, 98, Last(1, 0, 0, 0, 0, 1)),
            first.NextState);
        var recovered = CodexTokenCounterReducer.Reduce(
            Observation("recovered", 110, 0, 0, 0, 110, Last(10, 0, 0, 0, 0, 10)),
            stale.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.StaleCumulativeRegression, stale.Kind);
        Assert.Equal(100, stale.NextState!.TotalTokens);
        Assert.Equal(new CodexTokenAccountingDelta(10, 0, 0, 0, 0, 10), recovered.Delta);
    }

    [Fact]
    public void TokscaleStaleRegression_DoubledLastCase_RetainsBaselineAndRecovers()
    {
        var first = Reduce(Observation("first", 100, 0, 0, 0, 100));
        var stale = CodexTokenCounterReducer.Reduce(
            Observation("stale", 85, 0, 0, 0, 85, Last(8, 0, 0, 0, 0, 8)),
            first.NextState);
        var recovered = CodexTokenCounterReducer.Reduce(
            Observation("recovered", 110, 0, 0, 0, 110, Last(10, 0, 0, 0, 0, 10)),
            stale.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.StaleCumulativeRegression, stale.Kind);
        Assert.Equal(100, stale.NextState!.TotalTokens);
        Assert.Equal(new CodexTokenAccountingDelta(10, 0, 0, 0, 0, 10), recovered.Delta);
    }

    [Fact]
    public void TokscaleMeaningfulRegression_60WithLast12_StartsNewEpochAndRecovers()
    {
        var first = Reduce(Observation("first", 100, 0, 0, 0, 100));
        var reset = CodexTokenCounterReducer.Reduce(
            Observation("reset", 60, 0, 0, 0, 60, Last(12, 0, 0, 0, 0, 12)),
            first.NextState);
        var recovered = CodexTokenCounterReducer.Reduce(
            Observation("recovered", 70, 0, 0, 0, 70, Last(10, 0, 0, 0, 0, 10)),
            reset.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.CounterReset, reset.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(12, 0, 0, 0, 0, 12), reset.Delta);
        Assert.Equal(1, reset.NextState!.Epoch);
        Assert.Equal(new CodexTokenAccountingDelta(10, 0, 0, 0, 0, 10), recovered.Delta);
        Assert.Equal(1, recovered.NextState!.Epoch);
    }

    [Fact]
    public void MeaningfulRegression_ReanchorsEpochAndSubsequentMovementCountsNormally()
    {
        var first = Reduce(Observation("first", 100, 80, 10, 4, 100));
        var reset = CodexTokenCounterReducer.Reduce(
            Observation("reset", 60, 45, 8, 3, 60, Last(10, 8, 0, 2, 1, 12)),
            first.NextState);
        var next = CodexTokenCounterReducer.Reduce(
            Observation("next", 70, 53, 10, 4, 70, Last(10, 8, 0, 2, 1, 12)),
            reset.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.CounterReset, reset.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(2, 8, 0, 1, 1, 12), reset.Delta);
        Assert.Equal(1, reset.NextState!.Epoch);
        Assert.Equal(CodexTokenAccountingDecisionKind.MonotonicIncrement, next.Kind);
        Assert.Equal(reset.NextState.Epoch, next.NextState!.Epoch);
        Assert.Equal(new CodexTokenAccountingDelta(2, 8, 0, 1, 1, 12), next.Delta);
    }

    [Fact]
    public void GenuineReset_StartsNewEpochAndUsesNewEpochUsage()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110));
        var reset = CodexTokenCounterReducer.Reduce(
            Observation("reset", 10, 4, 2, 1, 12, Last(10, 4, 0, 2, 1, 12)),
            first.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.CounterReset, reset.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(6, 4, 0, 1, 1, 12), reset.Delta);
        Assert.Equal(1, reset.NextState!.Epoch);
    }

    [Fact]
    public void LegacyTotalOnlyRows_UseCumulativeDeltasAcrossObservations()
    {
        var first = Reduce(Observation("first", 100, 80, 10, 4, 110));
        var second = CodexTokenCounterReducer.Reduce(
            Observation("second", 150, 120, 20, 8, 162),
            first.NextState);

        Assert.Equal(new CodexTokenAccountingDelta(10, 40, 0, 6, 4, 52), second.Delta);
    }

    [Fact]
    public void TotalOnlyRegression_ReanchorsWithoutInventedUsage()
    {
        var first = Reduce(Observation("first", 100, 80, 10, 4, 110));
        var reset = CodexTokenCounterReducer.Reduce(
            Observation("reset", 60, 45, 8, 3, 60),
            first.NextState);
        var next = CodexTokenCounterReducer.Reduce(
            Observation("next", 70, 53, 10, 4, 70),
            reset.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.CounterReset, reset.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, reset.Delta);
        Assert.Equal(1, reset.NextState!.Epoch);
        Assert.Equal(new CodexTokenAccountingDelta(2, 8, 0, 1, 1, 10), next.Delta);
    }

    [Fact]
    public void FirstLastOnlyObservation_IsCountableWithoutCumulativeBaseline()
    {
        var decision = CodexTokenCounterReducer.Reduce(
            LastOnly("first", Last(10, 6, 0, 4, 1, 15)),
            null);

        Assert.Equal(CodexTokenAccountingDecisionKind.FirstObservation, decision.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(4, 6, 0, 3, 1, 15), decision.Delta);
        Assert.False(decision.NextState!.HasCumulativeBaseline);
    }

    [Fact]
    public void LastOnlyObservation_WithPreviousBaseline_UsesSaturatingDerivedWatermark()
    {
        var first = Reduce(Observation("first", 100, 80, 10, 4, 110));
        var lastOnly = CodexTokenCounterReducer.Reduce(
            LastOnly("turn", Last(10, 6, 0, 4, 1, 15)),
            first.NextState);

        Assert.Equal(new CodexTokenAccountingDelta(4, 6, 0, 3, 1, 15), lastOnly.Delta);
        Assert.True(lastOnly.NextState!.HasCumulativeBaseline);
        Assert.Equal(125, lastOnly.NextState.TotalTokens);
    }

    [Fact]
    public void TokenCountWithoutEitherSnapshot_ProducesNoObservation()
    {
        var decision = CodexTokenCounterReducer.Reduce(
            LastOnly("empty", null),
            null);

        Assert.Equal(CodexTokenAccountingDecisionKind.NoObservation, decision.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, decision.Delta);
        Assert.False(decision.AdvanceState);
    }

    [Fact]
    public void CacheAndReasoningBucketsRemainDisjointWithLastUsage()
    {
        var decision = Reduce(Observation("first", 100, 80, 10, 4, 110, Last(100, 80, 0, 10, 4, 110)));

        Assert.Equal(new CodexTokenAccountingDelta(20, 80, 0, 6, 4, 110), decision.Delta);
        Assert.Equal(decision.Delta.ReportedTotalTokens,
            decision.Delta.UncachedInputTokens + decision.Delta.CacheReadTokens +
            decision.Delta.CacheWriteTokens + decision.Delta.NonReasoningOutputTokens +
            decision.Delta.ReasoningOutputTokens);
    }

    private static CodexTokenAccountingDecision Reduce(CodexCumulativeTokenObservation observation) =>
        CodexTokenCounterReducer.Reduce(observation, null);

    private static CodexCumulativeTokenObservation Observation(
        string id,
        long input,
        long cached,
        long output,
        long reasoning,
        long total,
        CodexTokenUsageSnapshot? last = null) =>
        new(id, "session.jsonl", "session", "session", Start, "model", "high", input, cached, 0, output, reasoning, total, last);

    private static CodexTokenCountObservation LastOnly(string id, CodexTokenUsageSnapshot? last) =>
        new(id, "session.jsonl", "session", "session", Start, "model", "high", null, last);

    private static CodexTokenUsageSnapshot Last(
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        long total) =>
        new(input, cached, cacheWrite, output, reasoning, total);
}
