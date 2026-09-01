namespace TajsTokens.Core.Models;

public sealed record CodexObservatoryRefreshResult(
    int FilesDiscovered,
    int FilesScanned,
    int RecordsNormalized,
    int SessionsTouched,
    int Errors,
    long BytesObserved)
{
    public static CodexObservatoryRefreshResult Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

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
