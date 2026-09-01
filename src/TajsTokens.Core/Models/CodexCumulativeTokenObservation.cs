namespace TajsTokens.Core.Models;

/// <summary>
/// Content-free token counters observed in one Codex rollout token_count event.
/// The six primary values are cumulative session counters.  <see cref="LastTokenUsage" /> carries
/// the optional per-turn snapshot used by the accounting reducer when it is complete and valid.
/// Input includes cached/cache-write input and output includes reasoning output; the reducer converts
/// emitted deltas into disjoint native-shadow buckets before persistence.
/// </summary>
public sealed record CodexCumulativeTokenObservation(
    string SourceEventId,
    string SourceFile,
    string SessionId,
    string? AgentId,
    DateTimeOffset ObservedAtUtc,
    string? Model,
    string? ReasoningEffort,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    CodexTokenUsageSnapshot? LastTokenUsage = null)
{
    /// <summary>Raw cumulative snapshot as it appeared in <c>total_token_usage</c>.</summary>
    public CodexTokenUsageSnapshot TotalTokenUsage => new(
        InputTokens,
        CachedInputTokens,
        CacheWriteInputTokens,
        OutputTokens,
        ReasoningOutputTokens,
        TotalTokens);
}
