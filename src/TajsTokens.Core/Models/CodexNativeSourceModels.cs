namespace TajsTokens.Core.Models;

/// <summary>Codex-owned SQLite source families surfaced by the local observability boundary.</summary>
public enum CodexNativeSourceKind
{
    Memory,
    Goals,
    Queue,
    Artifacts,
    DesktopCatalog,
    ThreadSummaries
}

/// <summary>Availability of a source/capability at one inspection point.</summary>
public enum CodexNativeSourceAvailability
{
    Unavailable,
    Unsupported,
    Empty,
    Available,
    Error
}

public sealed record CodexNativeSourceInfo(
    CodexNativeSourceKind Kind,
    CodexNativeSourceAvailability Availability,
    string DisplayName,
    string? DatabasePath,
    DateTimeOffset CapturedAtUtc,
    string? SchemaFingerprint,
    IReadOnlyList<string> SupportedTables,
    string? Error)
{
    /// <summary>Commit used only as corroborating reference; installed runtime remains authoritative.</summary>
    public string? UpstreamCorroborationCommit { get; init; }

    /// <summary>Latest source-native migration/version value when the database exposes one.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Explorer discovery provenance (for example codex-home or snapshot-folder).</summary>
    public string? DiscoveryKind { get; init; }

    /// <summary>At least one source-native table contained rows beyond the bounded read.</summary>
    public bool HasMoreRows { get; init; }

    public bool IsInspectable => Availability is CodexNativeSourceAvailability.Available or CodexNativeSourceAvailability.Empty;

    public string StatusText => Availability switch
    {
        CodexNativeSourceAvailability.Available => $"Available · {SupportedTables.Count:N0} supported table(s)" + (HasMoreRows ? " · more rows available" : string.Empty),
        CodexNativeSourceAvailability.Empty => "Supported · no rows observed",
        CodexNativeSourceAvailability.Unsupported => "Source found · capability unsupported",
        CodexNativeSourceAvailability.Unavailable => "Source not discovered",
        CodexNativeSourceAvailability.Error => $"Inspection failed · {Error ?? "unknown error"}",
        _ => "Unknown"
    };
}

public sealed record CodexMemoryJob(
    string Kind,
    string JobKey,
    string Status,
    string? WorkerId,
    long? StartedAt,
    long? FinishedAt,
    long? LeaseUntil,
    long? RetryAt,
    int RetryRemaining,
    string? LastError,
    long? InputWatermark,
    long? LastSuccessWatermark);

/// <summary>
/// Stage-one memory metadata. RawMemory and RolloutSummary are intentionally not included: the
/// source remains locally inspectable through the raw explorer, but this read model only carries
/// metadata and presence flags.
/// </summary>
public sealed record CodexMemoryStage1Output(
    string ThreadId,
    long SourceUpdatedAt,
    string? RolloutSlug,
    long GeneratedAt,
    int? UsageCount,
    int? LastUsage,
    bool SelectedForPhase2,
    long? SelectedForPhase2SourceUpdatedAt,
    bool HasRawMemory,
    bool HasRolloutSummary);

public sealed record CodexMemorySource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexMemoryJob> Jobs,
    IReadOnlyList<CodexMemoryStage1Output> Stage1Outputs);

public sealed record CodexGoal(
    string ThreadId,
    string GoalId,
    string? Objective,
    string Status,
    long? TokenBudget,
    long TokensUsed,
    long TimeUsedSeconds,
    long CreatedAtMs,
    long UpdatedAtMs,
    bool HasContinuationDeferral);

public sealed record CodexGoalsSource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexGoal> Goals,
    IReadOnlyList<string> ContinuationDeferralThreadIds);

public sealed record CodexQueueItem(
    string Id,
    string ThreadId,
    long QueueOrder,
    long CreatedAtMs,
    long UpdatedAtMs,
    long? Revision,
    bool HasPayload);

public sealed record CodexQueueRevision(string ThreadId, long Revision);

public sealed record CodexQueueSource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexQueueItem> Items,
    IReadOnlyList<CodexQueueRevision> Revisions);

/// <summary>Artifact metadata retained in memory for local inspection; payload is not persisted.</summary>
public sealed record CodexThreadArtifact(
    string Id,
    string ThreadId,
    string ArtifactType,
    string IdentityKey,
    long CreatedAt,
    bool HasPayload);

public sealed record CodexArtifactsSource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexThreadArtifact> Artifacts);

/// <summary>Desktop catalog row. Equality with core state is an observation, not reconciliation.</summary>
public sealed record CodexDesktopCatalogEntry(
    string HostId,
    string ThreadId,
    string DisplayTitle,
    double SourceCreatedAt,
    double SourceUpdatedAt,
    string? Cwd,
    string SourceKind,
    string? SourceDetail,
    string? ModelProvider,
    string? GitBranch,
    long ObservationSequence,
    bool MissingCandidate,
    string? ThreadSource,
    double SourceRecencyAt,
    bool PendingObservedTitle,
    string? ProjectId,
    string? ConversationOrigin);

public sealed record CodexDesktopCatalogSource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexDesktopCatalogEntry> Entries);

/// <summary>Desktop thread-summary row; summary text is available only in the local read.</summary>
public sealed record CodexThreadSummary(
    string PrincipalKey,
    string HostKey,
    string ThreadId,
    string Summary,
    string? CompactSummary,
    string? CompactSummaryTurnKey,
    long Revision,
    long UpdatedAt);

public sealed record CodexThreadSummariesSource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexThreadSummary> Summaries);

public sealed record CodexNativeSourcesSnapshot(
    DateTimeOffset CapturedAtUtc,
    CodexMemorySource Memory,
    CodexGoalsSource Goals,
    CodexQueueSource Queue,
    CodexArtifactsSource Artifacts,
    CodexDesktopCatalogSource DesktopCatalog,
    CodexThreadSummariesSource ThreadSummaries)
{
    public IReadOnlyList<CodexNativeSourceInfo> Sources =>
    [
        Memory.Source,
        Goals.Source,
        Queue.Source,
        Artifacts.Source,
        DesktopCatalog.Source,
        ThreadSummaries.Source
    ];
}
