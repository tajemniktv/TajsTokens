namespace TajsTokens.Core.Models;

/// <summary>
/// The token counters carried by one Codex token-count record.  The cumulative
/// counters are always present on <see cref="CodexCumulativeTokenObservation" />;
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
/// Durable reducer state.  It contains only the latest cumulative counters;
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
    DateTimeOffset LastObservedAtUtc)
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
/// Cumulative totals are the ordering, duplicate and regression authority.  A
/// complete <c>last_token_usage</c> snapshot is preferred for the emitted
/// per-turn delta, while legacy rows (or partial snapshots) use cumulative
/// deltas.  Small cumulative regressions are treated as stale observations;
/// only a clear drop of the aggregate counter starts a new epoch.
/// </summary>
public static class CodexTokenCounterReducer
{
    // Codex can expose a briefly stale cumulative snapshot while a turn is being
    // committed.  A real reset drops the lifetime counter substantially; using a
    // ratio avoids turning a one/few-token correction into a new epoch.
    private const double GenuineResetRemainingRatio = 0.5d;

    public static CodexTokenAccountingDecision Reduce(
        CodexCumulativeTokenObservation observation,
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

        var current = Counters.From(observation);
        if (previous is null)
        {
            var firstDelta = SelectDelta(current, null, observation.LastTokenUsage, reset: true);
            return new(
                CodexTokenAccountingDecisionKind.FirstObservation,
                ToDisjointDelta(firstDelta),
                ToState(observation, epoch: 0),
                true);
        }

        var regressions = current.HasRegressionFrom(previous);
        if (regressions)
        {
            if (!IsGenuineReset(current, previous))
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
                ToState(observation, checked(previous.Epoch + 1)),
                true);
        }

        var delta = SelectDelta(current, previous, observation.LastTokenUsage, reset: false);
        var kind = delta.IsZero
            ? CodexTokenAccountingDecisionKind.NoIncrement
            : CodexTokenAccountingDecisionKind.MonotonicIncrement;
        return new(kind, ToDisjointDelta(delta), ToState(observation, previous.Epoch), true);
    }

    private static bool IsGenuineReset(Counters current, CodexTokenCounterState previous)
    {
        var previousAggregate = Math.Max(previous.TotalTokens, previous.LargestCounter);
        var currentAggregate = Math.Max(current.TotalTokens, current.LargestCounter);
        if (previousAggregate <= 0)
        {
            // There is no meaningful lifetime counter to regress from.
            return false;
        }

        return currentAggregate <= previousAggregate * GenuineResetRemainingRatio;
    }

    private static Counters SelectDelta(
        Counters current,
        CodexTokenCounterState? previous,
        CodexTokenUsageSnapshot? last,
        bool reset)
    {
        var cumulative = reset || previous is null
            ? current
            : current.Subtract(previous);

        // A partial last snapshot is context telemetry, not a safe accounting
        // row.  Complete snapshots are used only when all values are valid and
        // do not exceed the current cumulative observation.  This preserves the
        // legacy cumulative fallback and prevents stale re-emitted last usage
        // from inflating a stream.
        if (last is null || !last.IsComplete || !last.IsNonNegative)
        {
            return cumulative;
        }

        var candidate = Counters.FromComplete(last);
        return candidate.FitsWithin(cumulative) ? candidate : cumulative;
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

    private static CodexTokenCounterState ToState(CodexCumulativeTokenObservation observation, int epoch) =>
        new(
            observation.SourceFile,
            epoch,
            Clamp(observation.InputTokens),
            Clamp(observation.CachedInputTokens),
            Clamp(observation.CacheWriteInputTokens),
            Clamp(observation.OutputTokens),
            Clamp(observation.ReasoningOutputTokens),
            Clamp(observation.TotalTokens),
            observation.SourceEventId,
            observation.ObservedAtUtc);

    private static long Clamp(long value) => Math.Max(0, value);

    private readonly record struct Counters(
        long InputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningOutputTokens,
        long TotalTokens)
    {
        public bool IsZero => InputTokens == 0 && CachedInputTokens == 0 && CacheWriteInputTokens == 0 &&
                              OutputTokens == 0 && ReasoningOutputTokens == 0 && TotalTokens == 0;

        public long LargestCounter => Math.Max(
            TotalTokens,
            Math.Max(InputTokens, Math.Max(CachedInputTokens, Math.Max(CacheWriteInputTokens,
                Math.Max(OutputTokens, ReasoningOutputTokens)))));

        public static Counters From(CodexCumulativeTokenObservation observation) =>
            From(observation.TotalTokenUsage);

        private static Counters From(CodexTokenUsageSnapshot snapshot) => new(
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

        public bool HasRegressionFrom(CodexTokenCounterState previous) =>
            InputTokens < previous.InputTokens ||
            CachedInputTokens < previous.CachedInputTokens ||
            CacheWriteInputTokens < previous.CacheWriteInputTokens ||
            OutputTokens < previous.OutputTokens ||
            ReasoningOutputTokens < previous.ReasoningOutputTokens ||
            TotalTokens < previous.TotalTokens;

        public bool FitsWithin(Counters limit) =>
            InputTokens <= limit.InputTokens &&
            CachedInputTokens <= limit.CachedInputTokens &&
            CacheWriteInputTokens <= limit.CacheWriteInputTokens &&
            OutputTokens <= limit.OutputTokens &&
            ReasoningOutputTokens <= limit.ReasoningOutputTokens &&
            TotalTokens <= limit.TotalTokens;
    }
}
