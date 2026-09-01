namespace TajsTokens.Core.Models;

/// <summary>
/// Content-free raw counters from one Codex <c>token_count</c> event.
///
/// The cumulative and per-turn snapshots are independent provider fields:
/// Codex may omit either one, so neither is used as a substitute for the other.
/// The reducer treats a complete cumulative snapshot as a watermark and a
/// complete <see cref="LastTokenUsage" /> snapshot as the preferred observed
/// increment.
/// </summary>
public record CodexTokenCountObservation
{
    public CodexTokenCountObservation(
        string sourceEventId,
        string sourceFile,
        string sessionId,
        string? agentId,
        DateTimeOffset observedAtUtc,
        string? model,
        string? reasoningEffort,
        CodexTokenUsageSnapshot? totalTokenUsage,
        CodexTokenUsageSnapshot? lastTokenUsage = null)
    {
        SourceEventId = sourceEventId;
        SourceFile = sourceFile;
        SessionId = sessionId;
        AgentId = agentId;
        ObservedAtUtc = observedAtUtc;
        Model = model;
        ReasoningEffort = reasoningEffort;
        TotalTokenUsage = totalTokenUsage;
        LastTokenUsage = lastTokenUsage;
    }

    /// <summary>
    /// Compatibility constructor for callers that still provide the six
    /// cumulative counters directly.
    /// </summary>
    public CodexTokenCountObservation(
        string sourceEventId,
        string sourceFile,
        string sessionId,
        string? agentId,
        DateTimeOffset observedAtUtc,
        string? model,
        string? reasoningEffort,
        long inputTokens,
        long cachedInputTokens,
        long cacheWriteInputTokens,
        long outputTokens,
        long reasoningOutputTokens,
        long totalTokens,
        CodexTokenUsageSnapshot? lastTokenUsage = null)
        : this(
            sourceEventId,
            sourceFile,
            sessionId,
            agentId,
            observedAtUtc,
            model,
            reasoningEffort,
            new CodexTokenUsageSnapshot(
                inputTokens,
                cachedInputTokens,
                cacheWriteInputTokens,
                outputTokens,
                reasoningOutputTokens,
                totalTokens),
            lastTokenUsage)
    {
    }

    public string SourceEventId { get; }
    public string SourceFile { get; }
    public string SessionId { get; }
    public string? AgentId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string? Model { get; }
    public string? ReasoningEffort { get; }
    public CodexTokenUsageSnapshot? TotalTokenUsage { get; }
    public CodexTokenUsageSnapshot? LastTokenUsage { get; }

    public bool HasCompleteTotalUsage =>
        TotalTokenUsage is not null && TotalTokenUsage.IsComplete && TotalTokenUsage.IsNonNegative;

    // Compatibility accessors for existing consumers of the old shape. They
    // intentionally return zero when cumulative usage is absent; reducer code
    // checks HasCompleteTotalUsage before using these values.
    public long InputTokens => TotalTokenUsage?.InputTokens ?? 0;
    public long CachedInputTokens => TotalTokenUsage?.CachedInputTokens ?? 0;
    public long CacheWriteInputTokens => TotalTokenUsage?.CacheWriteInputTokens ?? 0;
    public long OutputTokens => TotalTokenUsage?.OutputTokens ?? 0;
    public long ReasoningOutputTokens => TotalTokenUsage?.ReasoningOutputTokens ?? 0;
    public long TotalTokens => TotalTokenUsage?.TotalTokens ?? 0;
}

/// <summary>
/// Compatibility name retained for callers compiled against the previous
/// cumulative-only shape. New code should use <see cref="CodexTokenCountObservation" />.
/// </summary>
public sealed record CodexCumulativeTokenObservation : CodexTokenCountObservation
{
    public CodexCumulativeTokenObservation(
        string sourceEventId,
        string sourceFile,
        string sessionId,
        string? agentId,
        DateTimeOffset observedAtUtc,
        string? model,
        string? reasoningEffort,
        long inputTokens,
        long cachedInputTokens,
        long cacheWriteInputTokens,
        long outputTokens,
        long reasoningOutputTokens,
        long totalTokens,
        CodexTokenUsageSnapshot? lastTokenUsage = null)
        : base(
            sourceEventId,
            sourceFile,
            sessionId,
            agentId,
            observedAtUtc,
            model,
            reasoningEffort,
            inputTokens,
            cachedInputTokens,
            cacheWriteInputTokens,
            outputTokens,
            reasoningOutputTokens,
            totalTokens,
            lastTokenUsage)
    {
    }

    /// <summary>Compatibility view of the always-present cumulative snapshot.</summary>
    public new CodexTokenUsageSnapshot TotalTokenUsage => base.TotalTokenUsage!;
}
