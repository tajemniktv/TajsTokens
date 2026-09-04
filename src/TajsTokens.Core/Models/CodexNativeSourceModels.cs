namespace TajsTokens.Core.Models;

/// <summary>Codex-owned SQLite source families surfaced by the local observability boundary.</summary>
public enum CodexNativeSourceKind
{
    Logs,
    Memory,
    Goals,
    Queue,
    Artifacts,
    DesktopCatalog,
    ThreadSummaries
}

/// <summary>
/// Bounded, read-only query options for the source-native Codex logs store.
/// Timestamps are expressed as UTC instants while the returned entries retain Codex's raw
/// Unix-second and nanosecond fields.
/// </summary>
public sealed record CodexLogsQuery
{
    public int PageIndex { get; init; }

    public int PageSize { get; init; } = 100;

    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Exclusive upper bound for the source timestamp.</summary>
    public DateTimeOffset? ToUtcExclusive { get; init; }

    /// <summary>Source-native log levels. Matching is case-insensitive.</summary>
    public IReadOnlyList<string> Levels { get; init; } = Array.Empty<string>();

    public string? TargetContains { get; init; }

    public string? ModulePathContains { get; init; }

    public string? FileContains { get; init; }

    public string? ThreadId { get; init; }

    public string? ProcessUuid { get; init; }

    /// <summary>
    /// Whether rows without a source-provided thread ID remain eligible. A missing thread_id
    /// column is not treated as evidence that every row is threadless.
    /// </summary>
    public bool IncludeThreadless { get; init; } = true;

    /// <summary>Opt in to transferring content-bearing log bodies into the local read model.</summary>
    public bool IncludeMessages { get; init; }
}

/// <summary>Observed optional columns in one Codex logs source instance.</summary>
public sealed record CodexLogsCapabilities(
    string? MessageColumn,
    bool HasModulePath,
    bool HasFile,
    bool HasLine,
    bool HasThreadId,
    bool HasProcessUuid,
    bool HasEstimatedBytes)
{
    public bool HasMessages => MessageColumn is not null;

    public bool HasRequiredSchema { get; init; }

    public IReadOnlyList<string> AvailableOptionalColumns =>
    [
        .. (HasMessages ? [MessageColumn!] : Array.Empty<string>()),
        .. (HasModulePath ? ["module_path"] : Array.Empty<string>()),
        .. (HasFile ? ["file"] : Array.Empty<string>()),
        .. (HasLine ? ["line"] : Array.Empty<string>()),
        .. (HasThreadId ? ["thread_id"] : Array.Empty<string>()),
        .. (HasProcessUuid ? ["process_uuid"] : Array.Empty<string>()),
        .. (HasEstimatedBytes ? ["estimated_bytes"] : Array.Empty<string>())
    ];

    public static CodexLogsCapabilities None { get; } = new(null, false, false, false, false, false, false);
}

/// <summary>
/// A source-native log row. Message text is present only when the query explicitly requested it;
/// HasMessage remains available without transferring the content-bearing value.
/// </summary>
public sealed record CodexLogEntry(
    long Id,
    long TimestampUnixSeconds,
    long TimestampNanoseconds,
    string Level,
    string Target,
    string? ModulePath,
    string? File,
    long? Line,
    string? ThreadId,
    string? ProcessUuid,
    bool HasMessage,
    string? Message,
    long? EstimatedBytes)
{
    public DateTimeOffset? TimestampUtc
    {
        get
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(TimestampUnixSeconds)
                    .AddTicks(TimestampNanoseconds / 100);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}

/// <summary>One bounded query result from the dedicated Codex logs source.</summary>
public sealed record CodexLogsSource(
    CodexNativeSourceInfo Source,
    IReadOnlyList<CodexLogEntry> Entries,
    CodexLogsQuery Query,
    CodexLogsCapabilities Capabilities,
    long? TotalMatchingRows,
    bool HasMoreRows,
    IReadOnlyList<string> Warnings);

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
    CodexLogsSource Logs,
    CodexMemorySource Memory,
    CodexGoalsSource Goals,
    CodexQueueSource Queue,
    CodexArtifactsSource Artifacts,
    CodexDesktopCatalogSource DesktopCatalog,
    CodexThreadSummariesSource ThreadSummaries)
{
    public IReadOnlyList<CodexNativeSourceInfo> Sources =>
    [
        Logs.Source,
        Memory.Source,
        Goals.Source,
        Queue.Source,
        Artifacts.Source,
        DesktopCatalog.Source,
        ThreadSummaries.Source
    ];
}
