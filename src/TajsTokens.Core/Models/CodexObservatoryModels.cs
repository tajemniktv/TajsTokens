// Taj's Tokens | CodexObservatoryModels.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

public enum CodexRolloutReadFailure
{
    InvalidCompressedFrame, TruncatedCompressedFrame, UnsupportedWindow, UnsupportedDictionary,
    RepresentationChanged, ConflictingRepresentations, UnsupportedCheckpoint, RecordTooLarge,
    Inaccessible, MissingFile, Unexpected,
}

public sealed record CodexIngestionResult(
    int RecordsScanned,
    int RecordsNormalized,
    string? SessionId)
{
    public string? SourceIdentity { get; init; }
    public long LastCompleteRecordOffset { get; init; }
    public long SourceLength { get; init; }
    public string? LogicalSourcePath { get; init; }
    public string? PhysicalSourcePath { get; init; }
    public long PhysicalSourceLength { get; init; }
    public bool IsSourceMissing { get; init; }
    // Ephemeral logical work counters; never physical disk I/O or retained source content.
    public long PrefixBytesHashed { get; init; }
    public long CompleteRecordBytesDecoded { get; init; }
    public long CompressedValidationBytes { get; init; }
    public int CheckpointWrites { get; init; }

    public bool ReachedCurrentEndOfFile => !IsSourceMissing && LastCompleteRecordOffset >= SourceLength;

    public static CodexIngestionResult Empty { get; } = new(0, 0, null);
    public static CodexIngestionResult MissingSource { get; } = new(0, 0, null) { IsSourceMissing = true };
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
    public IReadOnlyDictionary<CodexRolloutReadFailure, int> ReadFailures { get; init; }
        = new Dictionary<CodexRolloutReadFailure, int>();
    public long CompressedValidationBytes { get; init; }
    public CodexCollectionCoverage? Coverage { get; init; }
    public int DeferredFiles { get; init; }
    public bool DiscoveryReconciled { get; init; }
    public int CatalogRowsRead { get; init; }
    public long PrefixBytesHashed { get; init; }
    public long CompleteRecordBytesDecoded { get; init; }
    public int CheckpointWrites { get; init; }
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
    long RolloutBytes)
{
    public CodexNativeTokenCoverage? TokenCoverage { get; init; }
}

/// <summary>All-time selected local increments, not a complete account or a sum of alternate representations.</summary>
public sealed record CodexNativeTokenCoverage(long Events, long VerifiedTimeEvents, long VerifiedTimeTokens,
    long UnverifiedTimeEvents, long UnverifiedTimeTokens, long MissingModelEvents, long MissingModelTokens)
{
    public string Describe() => $"{Events:N0} selected local increments, all time; " +
        $"{UnverifiedTimeTokens:N0} tokens in {UnverifiedTimeEvents:N0} increments without verified source time; " +
        $"{MissingModelTokens:N0} tokens in {MissingModelEvents:N0} increments without a model. " +
        "These groups can overlap. Undated amounts remain in all-time totals and stay out of dated history. " +
        "Current login does not assign ownership; local history is not complete account usage. " +
        "Known occurrence replay and representation alternatives are counted once; unproven cross-source equivalence remains unresolved.";
}

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
