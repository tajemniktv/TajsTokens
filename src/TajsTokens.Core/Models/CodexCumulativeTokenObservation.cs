namespace TajsTokens.Core.Models;

/// <summary>
/// Content-free cumulative token counters observed in one Codex rollout token_count event.
/// Input includes cached/cache-write input and output includes reasoning output; the store converts
/// counter deltas into disjoint native-shadow buckets before persistence.
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
    long TotalTokens);