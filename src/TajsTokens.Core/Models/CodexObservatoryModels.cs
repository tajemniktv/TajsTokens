namespace TajsTokens.Core.Models;

public sealed record CodexIngestionResult(
    int RecordsScanned,
    int RecordsNormalized,
    string? SessionId)
{
    public string? SourceIdentity { get; init; }
    public long LastCompleteRecordOffset { get; init; }
    public long SourceLength { get; init; }

    public bool ReachedCurrentEndOfFile => LastCompleteRecordOffset >= SourceLength;

    public static CodexIngestionResult Empty { get; } = new(0, 0, null);
}

public sealed record CodexObservatoryRefreshResult(
    int FilesDiscovered,
    int FilesScanned,
    int RecordsScanned,
    int RecordsNormalized,
    int SessionsTouched,
    int Errors,
    long BytesObserved)
{
    public CodexCollectionCoverage? Coverage { get; init; }
    public static CodexObservatoryRefreshResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Ephemeral, best-effort path coverage; not a count of unique work or retained evidence.</summary>
public sealed record CodexCollectionCoverage(
    DateTimeOffset ObservedAtUtc,
    int IndexedPaths,
    int AccessibleIndexedPaths,
    int DiscoveredPaths,
    int UnindexedPaths,
    int IndexedOutsideDiscovery);

public sealed record CodexNativeTokenTotals(
    long UncachedInput,
    long CacheRead,
    long CacheWrite,
    long NonReasoningOutput,
    long ReasoningOutput,
    long ReportedTotal)
{
    public long DisjointTotal => checked(UncachedInput + CacheRead + CacheWrite + NonReasoningOutput + ReasoningOutput);
}

public sealed record CodexSessionOverview(
    string SessionId,
    string? ParentSessionId,
    string DisplayName,
    string Repository,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    string Status,
    string? Model,
    CodexNativeTokenTotals NativeTokens,
    int CompactionCount,
    double? PeakContextPercent,
    long RolloutBytes,
    int TimelineEventCount);

public sealed record CodexRolloutStorageSummary(
    string FilePath,
    string? SessionId,
    long SizeBytes,
    long RecordsSeen,
    long LargestRecordBytes,
    DateTimeOffset LastSeenAtUtc);

public sealed record CodexObservatorySummary(
    long SessionCount,
    CodexNativeTokenTotals NativeTokens,
    long RolloutBytes);

public sealed record CodexParserResumeState(
    string SourceIdentity,
    long ByteOffset,
    string SessionId,
    string? ParentSessionId,
    string AgentName,
    string Repository,
    DateTimeOffset StartedAtUtc,
    string? CurrentModel,
    string? ReasoningEffort,
    long? ContextWindowTokens,
    DateTimeOffset UpdatedAtUtc);
