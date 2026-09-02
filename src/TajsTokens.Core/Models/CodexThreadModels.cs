namespace TajsTokens.Core.Models;

/// <summary>
/// A source-native thread row read from Codex's local state catalog. Nullable fields preserve
/// schema capability/absence instead of turning missing columns into product facts.
/// </summary>
public sealed record CodexThreadCatalogEntry(
    string ThreadId,
    string SourcePath,
    string? Source,
    string? ThreadSource,
    string? ModelProvider,
    string? Title,
    string? Name,
    string? Preview,
    string? FirstUserMessage,
    string? Cwd,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? RecencyAtUtc,
    string? Model,
    string? ReasoningEffort,
    string? SandboxPolicy,
    string? ApprovalMode,
    string? HistoryMode,
    string? MemoryMode,
    string? GitSha,
    string? GitBranch,
    string? GitOriginUrl,
    string? AgentNickname,
    string? AgentRole,
    string? AgentPath,
    string? ProjectId,
    string? SectionId,
    int? SectionPosition,
    bool? Archived,
    bool? IsPinned)
{
    public string SourceDescription { get; init; } = string.Empty;

    public string DiscoveryKind { get; init; } = string.Empty;

    public string DisplayName
    {
        get
        {
            // Codex's source contract distinguishes legacy and paginated display behavior:
            // legacy rows prefer title, while paginated rows prefer name. Keep both native fields
            // available and only apply this presentation choice at the read-model boundary.
            var preferred = string.Equals(HistoryMode, "legacy", StringComparison.OrdinalIgnoreCase)
                ? FirstNonEmpty(Title, Name)
                : string.Equals(HistoryMode, "paginated", StringComparison.OrdinalIgnoreCase)
                    ? FirstNonEmpty(Name, Title)
                    : FirstNonEmpty(Name, Title);
            return string.IsNullOrWhiteSpace(preferred) ? ThreadId : preferred;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed record CodexThreadProject(
    string ProjectId,
    string Name,
    string? Metadata,
    int? Position,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<string> OrderedRoots)
{
    public bool RootsCapabilityAvailable { get; init; }
}

public sealed record CodexThreadSection(
    string SectionId,
    string Name,
    string? Appearance);

public sealed record CodexThreadDynamicTool(
    int Position,
    string Name,
    string Description,
    string InputSchema,
    bool DeferLoading,
    string? Namespace);

public sealed record CodexThreadSpawnEdge(
    string ParentThreadId,
    string ChildThreadId,
    string Status,
    int Depth);

public sealed record CodexThreadTurn(
    string TurnId,
    long RolloutOrdinal,
    string Status,
    string? ErrorJson,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMs,
    string? FirstUserItemId,
    string? FinalAgentItemId,
    long? RolloutByteOffset,
    long? RolloutEndOrdinal,
    long? RolloutEndByteOffset);

/// <summary>
/// History item content is source data and is returned only for local on-demand inspection. It is
/// never copied into the TajsTokens durable evidence database by this read model.
/// </summary>
public sealed record CodexThreadItem(
    string TurnId,
    string ItemId,
    long RolloutOrdinal,
    DateTimeOffset? CreatedAtUtc,
    string ItemType,
    string ItemJson,
    long? UpdatedAtOrdinal)
{
    public string DisplaySummary => string.IsNullOrWhiteSpace(ItemType)
        ? ItemId
        : $"{ItemType} · {ItemId}";
}

public sealed record CodexThreadRealtimeItem(
    string ItemId,
    long RolloutOrdinal,
    DateTimeOffset? CreatedAtUtc,
    string ItemType,
    string ItemJson);

/// <summary>
/// A provider-native, on-demand thread view assembled from the Codex state and thread-history
/// stores. Source paths and warnings make capability/coverage boundaries visible to the caller.
/// </summary>
public sealed record CodexThreadReadResult(
    CodexThreadCatalogEntry? Thread,
    CodexThreadProject? Project,
    CodexThreadSection? Section,
    IReadOnlyList<CodexThreadSpawnEdge> SpawnEdges,
    IReadOnlyList<CodexThreadDynamicTool> DynamicTools,
    IReadOnlyList<CodexThreadTurn> Turns,
    IReadOnlyList<CodexThreadItem> Items,
    IReadOnlyList<CodexThreadRealtimeItem> RealtimeItems,
    string? StateSourcePath,
    string? HistorySourcePath,
    IReadOnlyList<string> Warnings)
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool? ProjectCapabilityAvailable { get; init; }

    public bool? ProjectRootsCapabilityAvailable { get; init; }

    public bool? SectionCapabilityAvailable { get; init; }

    public bool? DynamicToolsCapabilityAvailable { get; init; }

    public bool? SpawnEdgesCapabilityAvailable { get; init; }

    public bool? TurnsCapabilityAvailable { get; init; }

    public bool? ItemsCapabilityAvailable { get; init; }

    public bool? RealtimeCapabilityAvailable { get; init; }

    public string? StateSourceDescription { get; init; }

    public string? HistorySourceDescription { get; init; }

    public bool HasStateSource => !string.IsNullOrWhiteSpace(StateSourcePath);

    public bool HasHistorySource => !string.IsNullOrWhiteSpace(HistorySourcePath);

    public bool HasThread => Thread is not null;
}
