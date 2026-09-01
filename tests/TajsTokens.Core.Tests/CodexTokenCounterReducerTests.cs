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
    public void MonotonicObservation_PrefersMatchingLastUsageIncrement()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110));
        var second = CodexTokenCounterReducer.Reduce(
            Observation("second", 130, 55, 20, 8, 142, Last(30, 15, 0, 10, 4, 32)),
            first.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.MonotonicIncrement, second.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(15, 15, 0, 6, 4, 32), second.Delta);
        Assert.Equal(130, second.NextState!.InputTokens);
        Assert.Equal(142, second.NextState.TotalTokens);
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
    public void SmallStaleRegression_IsIgnoredAndRecoveryDoesNotDoubleCount()
    {
        var first = Reduce(Observation("first", 100, 40, 10, 4, 110));
        var stale = CodexTokenCounterReducer.Reduce(
            Observation("stale", 99, 39, 10, 4, 109),
            first.NextState);
        var recovered = CodexTokenCounterReducer.Reduce(
            Observation("recovered", 110, 45, 12, 5, 121, Last(10, 5, 0, 2, 1, 11)),
            stale.NextState);

        Assert.Equal(CodexTokenAccountingDecisionKind.StaleCumulativeRegression, stale.Kind);
        Assert.Equal(CodexTokenAccountingDelta.Zero, stale.Delta);
        Assert.Equal(CodexTokenAccountingDecisionKind.MonotonicIncrement, recovered.Kind);
        Assert.Equal(new CodexTokenAccountingDelta(5, 5, 0, 1, 1, 11), recovered.Delta);
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

    private static CodexTokenUsageSnapshot Last(
        long input,
        long cached,
        long cacheWrite,
        long output,
        long reasoning,
        long total) =>
        new(input, cached, cacheWrite, output, reasoning, total);
}
