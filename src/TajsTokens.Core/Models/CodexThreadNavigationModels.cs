namespace TajsTokens.Core.Models;

/// <summary>
/// Bounded query for the provider-native Codex navigation tree. Search is applied to the
/// source-native catalog before ancestors are retained for a readable hierarchy.
/// </summary>
public sealed record CodexThreadNavigationQuery(
    string? Search = null,
    int Take = 2_000,
    bool IncludeArchived = false);

public enum CodexThreadNavigationGroupKind
{
    Project,
    Workspace,
    Unassigned
}

/// <summary>
/// A source-native thread node projected into a navigable parent/child tree. The catalog entry
/// remains attached so native identity, alternatives, and provenance are not lost in the UI.
/// </summary>
public sealed record CodexThreadNavigationNode(
    CodexThreadCatalogSearchEntry Thread,
    IReadOnlyList<CodexThreadNavigationNode> Children,
    bool MissingParent = false,
    bool CycleDetected = false)
{
    public string ThreadId => Thread.Preferred.ThreadId;
}

public sealed record CodexThreadNavigationGroup(
    string Key,
    string DisplayName,
    CodexThreadNavigationGroupKind Kind,
    string? ProjectId,
    string? WorkspacePath,
    IReadOnlyList<CodexThreadNavigationNode> RootThreads,
    int ThreadCount,
    DateTimeOffset? LatestActivityAtUtc);

/// <summary>
/// A spawn-edge observation retaining the source that produced it. The preferred edge used to
/// construct the tree is a presentation choice; all observations remain available to evidence
/// views and conflict diagnostics.
/// </summary>
public sealed record CodexThreadSpawnEdgeObservation(
    string SourcePath,
    string SourceDescription,
    CodexThreadSpawnEdge Edge);

public sealed record CodexThreadNavigationResult(
    IReadOnlyList<CodexThreadNavigationGroup> Groups,
    CodexThreadSearchResult Catalog,
    IReadOnlyList<CodexThreadSpawnEdge> PreferredSpawnEdges,
    IReadOnlyList<CodexThreadSpawnEdgeObservation> SpawnEdgeObservations,
    IReadOnlyList<string> Warnings)
{
    public string GroupingPolicy { get; init; } = string.Empty;

    public bool SpawnEdgesTruncated { get; init; }

    public bool CatalogCapabilityAvailable => Catalog.SourceCapabilityAvailable;

    public bool SpawnEdgesCapabilityAvailable { get; init; }

    public IReadOnlyList<string> ConflictWarnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CoverageWarnings { get; init; } = Array.Empty<string>();

    public int TotalThreadCount => Groups.Sum(group => group.ThreadCount);
}

public sealed record CodexThreadItemFact(string Label, string Value);

public enum CodexThreadItemPresentationKind
{
    Message,
    Reasoning,
    CommandExecution,
    FileChange,
    ToolCall,
    Collaboration,
    Plan,
    SystemEvent,
    Unknown
}

/// <summary>
/// Local-only readable projection of a raw Codex history item. <see cref="Source"/> remains the
/// complete source observation; extracted text is presentation data and is never persisted.
/// </summary>
public sealed record CodexThreadItemPresentation(
    CodexThreadItem Source,
    CodexThreadItemPresentationKind Kind,
    string Heading,
    string? Body,
    IReadOnlyList<CodexThreadItemFact> Facts,
    bool IsExpandedByDefault,
    bool IsContentBearing,
    string? LinkedThreadId = null)
{
    public string ItemId => Source.ItemId;

    public string TurnId => Source.TurnId;

    public string NativeType => Source.ItemType;

    public string SourceDescription => Source.SourceDescription;

    public string SourcePath => Source.SourcePath;

    public long RolloutOrdinal => Source.RolloutOrdinal;
}
