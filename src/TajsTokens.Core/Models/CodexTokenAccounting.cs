namespace TajsTokens.Core.Models;

/// <summary>
/// The token counters carried by one Codex token-count record.  The cumulative
/// counters are optional on <see cref="CodexTokenCountObservation" />;
/// the optional last-usage counters are nullable because Codex has emitted
/// partial and rate-limit-only snapshots in the wild.
/// </summary>
public sealed record CodexTokenUsageSnapshot(
    long? InputTokens,
    long? CachedInputTokens,
    long? CacheWriteInputTokens,
    long? OutputTokens,
    long? ReasoningOutputTokens,
    long? TotalTokens)
{
    public bool IsComplete => InputTokens is not null &&
                              CachedInputTokens is not null &&
                              CacheWriteInputTokens is not null &&
                              OutputTokens is not null &&
                              ReasoningOutputTokens is not null &&
                              TotalTokens is not null;

    public bool IsNonNegative =>
        (InputTokens is null or >= 0) &&
        (CachedInputTokens is null or >= 0) &&
        (CacheWriteInputTokens is null or >= 0) &&
        (OutputTokens is null or >= 0) &&
        (ReasoningOutputTokens is null or >= 0) &&
        (TotalTokens is null or >= 0);
}

/// <summary>
/// Durable reducer state.  It contains the latest cumulative watermark. When
/// <see cref="HasCumulativeBaseline" /> is false, the counters are only a
/// synthetic sum of last-only observed turns and are not a lifetime claim;
/// emitted deltas are represented by the native event table, not folded into
/// this state a second time.
/// </summary>
public sealed record CodexTokenCounterState(
    string SourceFile,
    int Epoch,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    string LastSourceEventId,
    DateTimeOffset LastObservedAtUtc,
    bool HasCumulativeBaseline = true)
{
    public long LargestCounter => Math.Max(
        TotalTokens,
        Math.Max(InputTokens, Math.Max(CachedInputTokens, Math.Max(CacheWriteInputTokens,
            Math.Max(OutputTokens, ReasoningOutputTokens)))));
}

public enum CodexTokenAccountingDecisionKind
{
    FirstObservation,
    MonotonicIncrement,
    NoIncrement,
    NoObservation,
    Duplicate,
    StaleObservation,
    StaleCumulativeRegression,
    CounterReset
}

/// <summary>Disjoint native token deltas emitted by the reducer.</summary>
public sealed record CodexTokenAccountingDelta(
    long UncachedInputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long NonReasoningOutputTokens,
    long ReasoningOutputTokens,
    long ReportedTotalTokens)
{
    public static CodexTokenAccountingDelta Zero { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// A pure accounting decision.  SQLite persists the delta and <see cref="NextState"
/// /> in one transaction; it does not repeat any counter inference.
/// </summary>
public sealed record CodexTokenAccountingDecision(
    CodexTokenAccountingDecisionKind Kind,
    CodexTokenAccountingDelta Delta,
    CodexTokenCounterState? NextState,
    bool AdvanceState);

/// <summary>
/// Reducer for Codex's cumulative and per-turn token counters.
///
/// Cumulative totals are watermark evidence for ordering, duplicate, and
/// regression decisions.  They are not required to equal the observed turn
/// represented by <c>last_token_usage</c>: a visible lifetime total can include
/// earlier turns, and cumulative movement can span more than one turn.  When a
/// complete, non-negative <c>last_token_usage</c> snapshot exists it is therefore
/// the primary emitted increment. Legacy rows with a complete cumulative
/// snapshot (or incomplete last snapshot) use cumulative deltas; last-only
/// rows remain directly countable through the synthetic watermark path.
///
/// A regressed cumulative watermark is stale when the complete observed turn
/// covers the aggregate watermark drop (or corroborates a component-only drop
/// while the aggregate remains monotonic), which is Tokscale-style short/out-of-order
/// evidence. Otherwise it starts a new epoch. A total-only regression has no
/// observed increment to emit, so it re-anchors with a zero delta.
/// </summary>
public static class CodexTokenCounterReducer
{
    public static CodexTokenAccountingDecision Reduce(
        CodexTokenCountObservation observation,
        CodexTokenCounterState? previous,
        bool sourceEventAlreadyPersisted = false)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (sourceEventAlreadyPersisted ||
            (previous is not null &&
             string.Equals(observation.SourceEventId, previous.LastSourceEventId, StringComparison.Ordinal)))
        {
            return new(CodexTokenAccountingDecisionKind.Duplicate, CodexTokenAccountingDelta.Zero, previous, false);
        }

        if (previous is not null && observation.ObservedAtUtc < previous.LastObservedAtUtc)
        {
            return new(CodexTokenAccountingDecisionKind.StaleObservation, CodexTokenAccountingDelta.Zero, previous, false);
        }

        var hasCompleteLast = HasCompleteLast(observation.LastTokenUsage);
        if (!observation.HasCompleteTotalUsage)
        {
            if (!hasCompleteLast)
            {
                // A token_count with neither usable snapshot is context-only;
                // do not create a zero native accounting event for it.
                return new(CodexTokenAccountingDecisionKind.NoObservation, CodexTokenAccountingDelta.Zero, previous, false);
            }

            var observedIncrement = Counters.FromComplete(observation.LastTokenUsage!);
            if (previous is null)
            {
                // Keep the first observed turn countable without claiming that
                // it is a cumulative lifetime baseline.
                return new(
                    CodexTokenAccountingDecisionKind.FirstObservation,
                    ToDisjointDelta(observedIncrement),
                    ToState(observation, epoch: 0, counters: observedIncrement, hasCumulativeBaseline: false),
                    true);
            }

            // With a cumulative watermark, advance a synthetic watermark using
            // saturating addition. Without one, retain accumulated synthetic
            // counters until a real cumulative snapshot appears.
            var synthetic = Counters.From(previous).SaturatingAdd(observedIncrement);
            return new(
                CodexTokenAccountingDecisionKind.MonotonicIncrement,
                ToDisjointDelta(observedIncrement),
                ToState(observation, previous.Epoch, synthetic, previous.HasCumulativeBaseline),
                true);
        }

        var current = Counters.From(observation.TotalTokenUsage!);
        if (previous is null)
        {
            var firstDelta = SelectDelta(current, null, observation.LastTokenUsage, reset: true);
            return new(
                CodexTokenAccountingDecisionKind.FirstObservation,
                ToDisjointDelta(firstDelta),
                ToState(observation, epoch: 0, counters: current, hasCumulativeBaseline: true),
                true);
        }

        if (!previous.HasCumulativeBaseline)
        {
            // A real cumulative watermark has become available after one or
            // more last-only rows. Prefer the current observed turn when it is
            // present; otherwise use only movement above the synthetic
            // watermark, never a fabricated full-reset delta.
            var firstCumulativeDelta = SelectDelta(current, previous, observation.LastTokenUsage, reset: false);
            var firstCumulativeKind = firstCumulativeDelta.IsZero
                ? CodexTokenAccountingDecisionKind.NoIncrement
                : CodexTokenAccountingDecisionKind.MonotonicIncrement;
            return new(
                firstCumulativeKind,
                ToDisjointDelta(firstCumulativeDelta),
                ToState(observation, previous.Epoch, current, hasCumulativeBaseline: true),
                true);
        }

        // The cumulative total is the watermark.  Repeated totals are not new
        // observed turns, even if Codex repeats a non-zero last_token_usage
        // payload alongside a rate-limit/context snapshot.
        if (current.TotalTokens == previous.TotalTokens)
        {
            return new(
                CodexTokenAccountingDecisionKind.NoIncrement,
                CodexTokenAccountingDelta.Zero,
                WithObservationMetadata(previous, observation),
                true);
        }

        var regressions = current.HasRegressionFrom(previous);
        if (regressions)
        {
            if (IsStaleRegression(current, previous, observation.LastTokenUsage))
            {
                return new(
                    CodexTokenAccountingDecisionKind.StaleCumulativeRegression,
                    CodexTokenAccountingDelta.Zero,
                    previous,
                    false);
            }

            var resetDelta = SelectDelta(current, previous, observation.LastTokenUsage, reset: true);
            return new(
                CodexTokenAccountingDecisionKind.CounterReset,
                ToDisjointDelta(resetDelta),
                ToState(observation, checked(previous.Epoch + 1), current, hasCumulativeBaseline: true),
                true);
        }

        var delta = SelectDelta(current, previous, observation.LastTokenUsage, reset: false);
        var kind = delta.IsZero
            ? CodexTokenAccountingDecisionKind.NoIncrement
            : CodexTokenAccountingDecisionKind.MonotonicIncrement;
        return new(kind, ToDisjointDelta(delta), ToState(observation, previous.Epoch, current, hasCumulativeBaseline: true), true);
    }

    private static bool IsStaleRegression(
        Counters current,
        CodexTokenCounterState previous,
        CodexTokenUsageSnapshot? last)
    {
        // Without a complete per-turn snapshot there is no evidence that a
        // regression is merely a short/out-of-order watermark.  Re-anchor with
        // zero instead of manufacturing a negative or full-lifetime delta.
        if (last is null || !last.IsComplete || !last.IsNonNegative)
        {
            return false;
        }

        var increment = Counters.FromComplete(last);
        // Total tokens are Codex's aggregate watermark. If the observed turn
        // can cover the aggregate drop, the lower snapshot is plausibly a
        // short/out-of-order watermark rather than a new epoch. Component
        // counters remain useful corroboration when the aggregate itself did
        // not regress.
        if (current.TotalTokens < previous.TotalTokens)
        {
            return RegressionCovered(current.TotalTokens, previous.TotalTokens, increment.TotalTokens);
        }

        return RegressionCovered(current.InputTokens, previous.InputTokens, increment.InputTokens) &&
               RegressionCovered(current.CachedInputTokens, previous.CachedInputTokens, increment.CachedInputTokens) &&
               RegressionCovered(current.CacheWriteInputTokens, previous.CacheWriteInputTokens, increment.CacheWriteInputTokens) &&
               RegressionCovered(current.OutputTokens, previous.OutputTokens, increment.OutputTokens) &&
               RegressionCovered(current.ReasoningOutputTokens, previous.ReasoningOutputTokens, increment.ReasoningOutputTokens);
    }

    private static bool RegressionCovered(long current, long previous, long increment) =>
        current >= previous || previous - current <= increment;

    private static CodexTokenCounterState WithObservationMetadata(
        CodexTokenCounterState previous,
        CodexTokenCountObservation observation) =>
        previous with
        {
            SourceFile = observation.SourceFile,
            LastSourceEventId = observation.SourceEventId,
            LastObservedAtUtc = observation.ObservedAtUtc
        };

    private static bool HasCompleteLast(CodexTokenUsageSnapshot? last) =>
        last is not null && last.IsComplete && last.IsNonNegative;

    private static Counters SelectDelta(
        Counters current,
        CodexTokenCounterState? previous,
        CodexTokenUsageSnapshot? last,
        bool reset)
    {
        // last_token_usage is an observed-turn increment, not a restatement of
        // cumulative movement.  Keep it primary whenever it is complete and
        // valid—even when cumulative history is only partially visible or spans
        // multiple turns.  The exact unchanged-watermark case is handled before
        // this method so a repeated last payload cannot double-count.
        if (HasCompleteLast(last))
        {
            return Counters.FromComplete(last!);
        }

        if (previous is null)
        {
            return current;
        }

        // A reset without last_token_usage has no safe observed increment.  The
        // caller still persists the current cumulative counters as the new
        // baseline and the next monotonic observation can resume normally.
        return reset ? Counters.Zero : current.Subtract(previous);
    }

    private static CodexTokenAccountingDelta ToDisjointDelta(Counters value)
    {
        // Subtract in stages: two long.MaxValue cache counters must not wrap
        // before the non-negative clamp is applied.
        var uncachedInput = Math.Max(0, value.InputTokens - value.CachedInputTokens);
        uncachedInput = Math.Max(0, uncachedInput - value.CacheWriteInputTokens);
        var nonReasoningOutput = Math.Max(0, value.OutputTokens - value.ReasoningOutputTokens);
        return new(
            uncachedInput,
            value.CachedInputTokens,
            value.CacheWriteInputTokens,
            nonReasoningOutput,
            value.ReasoningOutputTokens,
            value.TotalTokens);
    }

    private static CodexTokenCounterState ToState(
        CodexTokenCountObservation observation,
        int epoch,
        Counters counters,
        bool hasCumulativeBaseline) =>
        new(
            observation.SourceFile,
            epoch,
            counters.InputTokens,
            counters.CachedInputTokens,
            counters.CacheWriteInputTokens,
            counters.OutputTokens,
            counters.ReasoningOutputTokens,
            counters.TotalTokens,
            observation.SourceEventId,
            observation.ObservedAtUtc,
            hasCumulativeBaseline);

    private static long Clamp(long value) => Math.Max(0, value);

    private readonly record struct Counters(
        long InputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens,
        long TotalTokens)
    {
        public static Counters Zero { get; } = new(0, 0, 0, 0, 0, 0);

        public bool IsZero => InputTokens == 0 && CachedInputTokens == 0 && CacheWriteInputTokens == 0 &&
                              OutputTokens == 0 && ReasoningOutputTokens == 0 && TotalTokens == 0;

        public long LargestCounter => Math.Max(
            TotalTokens,
            Math.Max(InputTokens, Math.Max(CachedInputTokens, Math.Max(CacheWriteInputTokens,
                Math.Max(OutputTokens, ReasoningOutputTokens)))));

        public static Counters From(CodexTokenCountObservation observation) =>
            From(observation.TotalTokenUsage);

        public static Counters From(CodexTokenCounterState state) => new(
            state.InputTokens,
            state.CachedInputTokens,
            state.CacheWriteInputTokens,
            state.OutputTokens,
            state.ReasoningOutputTokens,
            state.TotalTokens);

        public static Counters From(CodexTokenUsageSnapshot? snapshot) => snapshot is null ? Zero : FromSnapshot(snapshot);

        private static Counters FromSnapshot(CodexTokenUsageSnapshot snapshot) => new(
            Clamp(snapshot.InputTokens ?? 0), Clamp(snapshot.CachedInputTokens ?? 0),
            Clamp(snapshot.CacheWriteInputTokens ?? 0), Clamp(snapshot.OutputTokens ?? 0),
            Clamp(snapshot.ReasoningOutputTokens ?? 0), Clamp(snapshot.TotalTokens ?? 0));

        public static Counters FromComplete(CodexTokenUsageSnapshot snapshot) => new(
            snapshot.InputTokens!.Value, snapshot.CachedInputTokens!.Value,
            snapshot.CacheWriteInputTokens!.Value, snapshot.OutputTokens!.Value,
            snapshot.ReasoningOutputTokens!.Value, snapshot.TotalTokens!.Value);

        public Counters Subtract(CodexTokenCounterState previous) => new(
            Math.Max(0, InputTokens - previous.InputTokens),
            Math.Max(0, CachedInputTokens - previous.CachedInputTokens),
            Math.Max(0, CacheWriteInputTokens - previous.CacheWriteInputTokens),
            Math.Max(0, OutputTokens - previous.OutputTokens),
            Math.Max(0, ReasoningOutputTokens - previous.ReasoningOutputTokens),
            Math.Max(0, TotalTokens - previous.TotalTokens));

        public Counters SaturatingAdd(Counters increment) => new(
            SaturatingAdd(InputTokens, increment.InputTokens),
            SaturatingAdd(CachedInputTokens, increment.CachedInputTokens),
            SaturatingAdd(CacheWriteInputTokens, increment.CacheWriteInputTokens),
            SaturatingAdd(OutputTokens, increment.OutputTokens),
            SaturatingAdd(ReasoningOutputTokens, increment.ReasoningOutputTokens),
            SaturatingAdd(TotalTokens, increment.TotalTokens));

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        public bool HasRegressionFrom(CodexTokenCounterState previous) =>
            InputTokens < previous.InputTokens ||
            CachedInputTokens < previous.CachedInputTokens ||
            CacheWriteInputTokens < previous.CacheWriteInputTokens ||
            OutputTokens < previous.OutputTokens ||
            ReasoningOutputTokens < previous.ReasoningOutputTokens ||
            TotalTokens < previous.TotalTokens;

    }
}
