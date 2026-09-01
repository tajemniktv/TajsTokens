namespace TajsTokens.Core.Models;

public sealed record CodexIngestionResult(
    int RecordsScanned,
    int RecordsNormalized,
    string? SessionId)
{
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
    public static CodexObservatoryRefreshResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
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
    string? ThreadId,
    string Repository,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    string Status,
    string AgentName,
    string AgentState,
    string? Model,
    long NativeUncachedInput,
    long NativeCacheRead,
    long NativeCacheWrite,
    long NativeNonReasoningOutput,
    long NativeReasoningOutput,
    long NativeReportedTotal,
    long? LatestContextInput,
    long? ContextWindowTokens,
    int Compactions,
    long RolloutBytes,
    long RecordsSeen);

public sealed record CodexRolloutStorageSummary(
    string SourceIdentity,
    string FilePath,
    string? SessionId,
    long SizeBytes,
    long RecordsSeen,
    long LargestRecordBytes,
    DateTimeOffset LastSeenAtUtc);

public sealed record CodexObservatorySummary(
    int SessionCount,
    int AgentCount,
    int RelationshipCount,
    int RolloutFileCount,
    long RolloutBytes,
    long RolloutRecords,
    CodexNativeTokenTotals NativeTokens,
    int ContextObservationCount,
    int CompactionCount,
    long? PeakContextInput,
    DateTimeOffset? LatestActivityAtUtc)
{
    public static CodexObservatorySummary Empty { get; } = new(
        0, 0, 0, 0, 0, 0,
        new CodexNativeTokenTotals(0, 0, 0, 0, 0, 0),
        0, 0, null, null);
}
