using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Read-only, provider-native Codex thread view. The state catalog and thread-history stores are
/// queried on demand; no source rows are mirrored into the TajsTokens database.
/// </summary>
public sealed class CodexThreadObservabilityService
{
    private const int MaxHistoryRows = 5_000;
    private const int MaxCatalogRows = 10_000;
    private const string CatalogReconciliationPolicy =
        "Prefer newest recency, then updated/created time, source generation, source write time, and source path; retain every source observation.";
    private const string StateReconciliationPolicy =
        "Prefer the first thread row in explicit source order (generation, write time, description, then path) for related state tables; retain every thread row observation.";
    private const string HistoryReconciliationPolicy =
        "Union readable history rows by source-qualified identity; source order is generation, write time, description, then path, and no source overrides another.";
    private readonly CodexStateDbExplorerService _sources;

    private sealed record BoundedRows(
        IReadOnlyList<Dictionary<string, object?>> Rows,
        bool IsTruncated);

    private sealed record BoundedRead<T>(
        IReadOnlyList<T> Rows,
        bool IsTruncated);

    public CodexThreadObservabilityService(string? codexHome = null, string? snapshotDirectory = null)
    {
        _sources = new CodexStateDbExplorerService(codexHome, snapshotDirectory);
    }

    public string CodexHome => _sources.CodexHome;

    /// <summary>
    /// Lists source-native thread rows from all readable state stores. Every matching source row
    /// that was read is retained in <see cref="CodexThreadSearchResult.SourceObservations"/> and
    /// the bounded presentation entries expose the explicit reconciliation policy and alternatives.
    /// </summary>
    public async Task<CodexThreadSearchResult> SearchThreadsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken)
    {
        var observations = new List<CodexThreadCatalogEntry>();
        var warnings = new List<string>();
        var coverageWarnings = new List<string>();
        var normalizedSearch = search?.Trim() ?? string.Empty;

        foreach (var candidate in OrderCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
                if (!await HasTableAsync(connection, "threads", cancellationToken))
                {
                    continue;
                }

                var read = await ReadRowsAsync(
                    connection,
                    "threads",
                    whereClause: null,
                    parameter: null,
                    orderBy: null,
                    MaxCatalogRows,
                    cancellationToken);
                foreach (var values in read.Rows)
                {
                    var entry = MapThread(values, candidate);
                    if (entry is null || !Matches(entry, normalizedSearch))
                    {
                        continue;
                    }

                    observations.Add(entry);
                }

                if (read.IsTruncated)
                {
                    coverageWarnings.Add(
                        $"{candidate.Path}: threads search was truncated at {MaxCatalogRows:N0} rows.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                warnings.Add($"{candidate.Path}: {Summarize(exception.Message)}");
            }
        }

        var reconciled = observations
            .GroupBy(entry => entry.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = OrderCatalogObservations(group).ToArray();
                var preferred = ordered[0];
                return new CodexThreadCatalogSearchEntry(
                    preferred,
                    ordered,
                    $"Selected {preferred.SourcePath} by the explicit catalog policy; {ordered.Length:N0} source observation(s) remain attached.");
            })
            .OrderByDescending(entry => entry.Preferred.RecencyAtUtc ?? entry.Preferred.UpdatedAtUtc ?? entry.Preferred.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.Preferred.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, take))
            .ToArray();

        return new CodexThreadSearchResult(reconciled, observations, warnings)
        {
            ReconciliationPolicy = CatalogReconciliationPolicy,
            SourceRowsTruncated = coverageWarnings.Count > 0,
            CoverageWarnings = coverageWarnings
        };
    }

    public async Task<CodexThreadReadResult> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("A thread ID is required.", nameof(threadId));
        }

        var warnings = new List<string>();
        var coverageWarnings = new List<string>();
        var capturedAtUtc = DateTimeOffset.UtcNow;
        CodexThreadCatalogEntry? thread = null;
        var stateThreadObservations = new List<CodexThreadCatalogEntry>();
        CodexThreadProject? project = null;
        CodexThreadSection? section = null;
        var edges = Array.Empty<CodexThreadSpawnEdge>();
        var dynamicTools = Array.Empty<CodexThreadDynamicTool>();
        string? stateSourcePath = null;
        string? stateSourceDescription = null;
        bool? projectCapability = null;
        bool? projectRootsCapability = null;
        bool? sectionCapability = null;
        bool? dynamicToolsCapability = null;
        bool? spawnEdgesCapability = null;
        bool? projectRootsTruncated = null;
        bool? dynamicToolsTruncated = null;
        bool? spawnEdgesTruncated = null;
        var projectSourceSelected = false;
        var projectRootsSourceSelected = false;
        var sectionSourceSelected = false;
        var dynamicToolsSourceSelected = false;
        var spawnEdgesSourceSelected = false;

        foreach (var candidate in OrderCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
                if (!await HasTableAsync(connection, "threads", cancellationToken))
                {
                    continue;
                }

                var values = await ReadSingleRowAsync(connection, "threads", "id", threadId, cancellationToken);
                if (values is null)
                {
                    continue;
                }

                var observation = MapThread(values, candidate);
                if (observation is null)
                {
                    warnings.Add($"{candidate.Path}: threads row has no usable id.");
                    continue;
                }

                stateThreadObservations.Add(observation);
                // The first matching state row remains the preferred metadata presentation, but
                // optional related capabilities are selected independently. A rotated/older
                // state file may contain the thread row while a later candidate is the first one
                // that actually carries projects, sections, dynamic tools or spawn edges.
                if (thread is null)
                {
                    thread = observation;
                    stateSourcePath = candidate.Path;
                    stateSourceDescription = candidate.SourceDescription;
                }

                var preferredThread = thread;
                if (preferredThread is null)
                {
                    continue;
                }

                var hasProjects = await HasTableAsync(connection, "projects", cancellationToken);
                var hasProjectRoots = await HasTableAsync(connection, "project_roots", cancellationToken);
                projectCapability = ObserveCapability(projectCapability, hasProjects);
                projectRootsCapability = ObserveCapability(projectRootsCapability, hasProjectRoots);
                if (!projectSourceSelected && !string.IsNullOrWhiteSpace(preferredThread.ProjectId) && hasProjects)
                {
                    var projectRead = await ReadProjectAsync(connection, preferredThread.ProjectId!, cancellationToken);
                    if (projectRead.Project is not null)
                    {
                        project = projectRead.Project;
                        projectSourceSelected = true;
                        projectRootsSourceSelected = hasProjectRoots;
                        projectRootsTruncated = ObserveTruncation(
                            projectRootsTruncated,
                            projectRead.RootsTruncated,
                            hasProjectRoots);
                        if (projectRead.RootsTruncated)
                        {
                            coverageWarnings.Add($"{candidate.Path}: project_roots was truncated at {MaxHistoryRows:N0} rows.");
                        }
                    }
                }
                else if (project is not null && !projectRootsSourceSelected && hasProjectRoots)
                {
                    var rootsRead = await ReadProjectRootsAsync(
                        connection,
                        preferredThread.ProjectId!,
                        cancellationToken);
                    project = project with
                    {
                        OrderedRoots = rootsRead.Rows,
                        RootsCapabilityAvailable = true
                    };
                    projectRootsSourceSelected = true;
                    projectRootsTruncated = ObserveTruncation(
                        projectRootsTruncated,
                        rootsRead.IsTruncated,
                        capabilityObserved: true);
                    if (rootsRead.IsTruncated)
                    {
                        coverageWarnings.Add($"{candidate.Path}: project_roots was truncated at {MaxHistoryRows:N0} rows.");
                    }
                }

                var hasSections = await HasTableAsync(connection, "thread_sections", cancellationToken);
                sectionCapability = ObserveCapability(sectionCapability, hasSections);
                if (!sectionSourceSelected && !string.IsNullOrWhiteSpace(preferredThread.SectionId) && hasSections)
                {
                    var sectionRead = await ReadSectionAsync(connection, preferredThread.SectionId!, cancellationToken);
                    if (sectionRead is not null)
                    {
                        section = sectionRead;
                        sectionSourceSelected = true;
                    }
                }

                var hasDynamicTools = await HasTableAsync(connection, "thread_dynamic_tools", cancellationToken);
                dynamicToolsCapability = ObserveCapability(dynamicToolsCapability, hasDynamicTools);
                if (!dynamicToolsSourceSelected && hasDynamicTools)
                {
                    var toolsRead = await ReadDynamicToolsAsync(connection, threadId, cancellationToken);
                    dynamicTools = toolsRead.Rows.ToArray();
                    dynamicToolsSourceSelected = true;
                    dynamicToolsTruncated = ObserveTruncation(
                        dynamicToolsTruncated,
                        toolsRead.IsTruncated,
                        true);
                    if (toolsRead.IsTruncated)
                    {
                        coverageWarnings.Add($"{candidate.Path}: thread_dynamic_tools was truncated at {MaxHistoryRows:N0} rows.");
                    }
                }

                var hasSpawnEdges = await HasTableAsync(connection, "thread_spawn_edges", cancellationToken);
                spawnEdgesCapability = ObserveCapability(spawnEdgesCapability, hasSpawnEdges);
                if (!spawnEdgesSourceSelected && hasSpawnEdges)
                {
                    var edgesRead = await ReadSpawnEdgesAsync(connection, threadId, cancellationToken);
                    edges = edgesRead.Rows.ToArray();
                    spawnEdgesSourceSelected = true;
                    spawnEdgesTruncated = ObserveTruncation(
                        spawnEdgesTruncated,
                        edgesRead.IsTruncated,
                        true);
                    if (edgesRead.IsTruncated)
                    {
                        coverageWarnings.Add($"{candidate.Path}: thread_spawn_edges was truncated at {MaxHistoryRows:N0} rows.");
                    }
                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                warnings.Add($"{candidate.Path}: {Summarize(exception.Message)}");
            }
        }

        var turns = new List<CodexThreadTurn>();
        var items = new List<CodexThreadItem>();
        var realtimeItems = new List<CodexThreadRealtimeItem>();
        var historySources = new List<CodexThreadHistorySourceObservation>();
        string? historySourcePath = null;
        string? historySourceDescription = null;
        string? historySourceSelectionRationale = null;
        bool? turnsCapability = null;
        bool? itemsCapability = null;
        bool? realtimeCapability = null;
        bool? turnsTruncated = null;
        bool? itemsTruncated = null;
        bool? realtimeItemsTruncated = null;

        foreach (var candidate in OrderCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
                var hasTurns = await HasTableAsync(connection, "thread_turns", cancellationToken);
                var hasItems = await HasTableAsync(connection, "thread_items", cancellationToken);
                var hasRealtime = await HasTableAsync(connection, "thread_realtime_items", cancellationToken);
                turnsCapability = ObserveCapability(turnsCapability, hasTurns);
                itemsCapability = ObserveCapability(itemsCapability, hasItems);
                realtimeCapability = ObserveCapability(realtimeCapability, hasRealtime);
                if (!hasTurns && !hasItems && !hasRealtime)
                {
                    continue;
                }

                BoundedRead<CodexThreadTurn> sourceTurns = new(Array.Empty<CodexThreadTurn>(), false);
                BoundedRead<CodexThreadItem> sourceItems = new(Array.Empty<CodexThreadItem>(), false);
                BoundedRead<CodexThreadRealtimeItem> sourceRealtimeItems = new(Array.Empty<CodexThreadRealtimeItem>(), false);
                if (hasTurns)
                {
                    sourceTurns = await ReadTurnsAsync(connection, threadId, candidate, cancellationToken);
                }
                if (hasItems)
                {
                    sourceItems = await ReadItemsAsync(connection, threadId, candidate, cancellationToken);
                }
                if (hasRealtime)
                {
                    sourceRealtimeItems = await ReadRealtimeItemsAsync(connection, threadId, candidate, cancellationToken);
                }

                historySources.Add(new CodexThreadHistorySourceObservation(
                    candidate.Path,
                    candidate.SourceDescription,
                    sourceTurns.Rows,
                    sourceItems.Rows,
                    sourceRealtimeItems.Rows)
                {
                    TurnsCapabilityAvailable = hasTurns,
                    ItemsCapabilityAvailable = hasItems,
                    RealtimeCapabilityAvailable = hasRealtime,
                    TurnsTruncated = sourceTurns.IsTruncated,
                    ItemsTruncated = sourceItems.IsTruncated,
                    RealtimeItemsTruncated = sourceRealtimeItems.IsTruncated
                });
                turns.AddRange(sourceTurns.Rows);
                items.AddRange(sourceItems.Rows);
                realtimeItems.AddRange(sourceRealtimeItems.Rows);
                turnsTruncated = ObserveTruncation(turnsTruncated, sourceTurns.IsTruncated, hasTurns);
                itemsTruncated = ObserveTruncation(itemsTruncated, sourceItems.IsTruncated, hasItems);
                realtimeItemsTruncated = ObserveTruncation(realtimeItemsTruncated, sourceRealtimeItems.IsTruncated, hasRealtime);

                if (sourceTurns.IsTruncated)
                {
                    coverageWarnings.Add($"{candidate.Path}: thread_turns was truncated at {MaxHistoryRows:N0} rows.");
                }
                if (sourceItems.IsTruncated)
                {
                    coverageWarnings.Add($"{candidate.Path}: thread_items was truncated at {MaxHistoryRows:N0} rows.");
                }
                if (sourceRealtimeItems.IsTruncated)
                {
                    coverageWarnings.Add($"{candidate.Path}: thread_realtime_items was truncated at {MaxHistoryRows:N0} rows.");
                }

                if (historySourcePath is null &&
                    (sourceTurns.Rows.Count > 0 || sourceItems.Rows.Count > 0 || sourceRealtimeItems.Rows.Count > 0))
                {
                    historySourcePath = candidate.Path;
                    historySourceDescription = candidate.SourceDescription;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                warnings.Add($"{candidate.Path}: {Summarize(exception.Message)}");
            }
        }

        var historySourceOrder = historySources
            .Select((source, index) => (source.SourcePath, index))
            .ToDictionary(item => item.SourcePath, item => item.index, StringComparer.OrdinalIgnoreCase);

        turns = turns
            .GroupBy(turn => $"{turn.SourcePath}\u001f{turn.TurnId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(turn => turn.RolloutOrdinal)
            .ThenBy(turn => historySourceOrder.TryGetValue(turn.SourcePath, out var order) ? order : int.MaxValue)
            .ThenBy(turn => turn.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(turn => turn.TurnId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        items = items
            .GroupBy(item => $"{item.SourcePath}\u001f{item.TurnId}\u001f{item.ItemId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.RolloutOrdinal)
            .ThenBy(item => historySourceOrder.TryGetValue(item.SourcePath, out var order) ? order : int.MaxValue)
            .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        realtimeItems = realtimeItems
            .GroupBy(item => $"{item.SourcePath}\u001f{item.ItemId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.RolloutOrdinal)
            .ThenBy(item => historySourceOrder.TryGetValue(item.SourcePath, out var order) ? order : int.MaxValue)
            .ThenBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (historySourcePath is null && historySources.Count > 0)
        {
            var firstSource = historySources[0];
            historySourcePath = firstSource.SourcePath;
            historySourceDescription = firstSource.SourceDescription;
            historySourceSelectionRationale =
                $"No selected-thread rows were present; the first capable source in explicit history order is shown while all {historySources.Count:N0} source(s) remain in HistorySources.";
        }
        else if (historySourcePath is not null)
        {
            historySourceSelectionRationale =
                $"Presentation source selected by explicit history order; all {historySources.Count:N0} readable history source(s) remain in HistorySources.";
        }

        if (thread is null && historySourcePath is null)
        {
            warnings.Add($"No readable Codex source row was found for thread {threadId}.");
        }

        return new CodexThreadReadResult(
            thread,
            project,
            section,
            edges,
            dynamicTools,
            turns,
            items,
            realtimeItems,
            stateSourcePath,
            historySourcePath,
            warnings)
        {
            CapturedAtUtc = capturedAtUtc,
            ProjectCapabilityAvailable = projectCapability,
            ProjectRootsCapabilityAvailable = projectRootsCapability,
            SectionCapabilityAvailable = sectionCapability,
            DynamicToolsCapabilityAvailable = dynamicToolsCapability,
            SpawnEdgesCapabilityAvailable = spawnEdgesCapability,
            TurnsCapabilityAvailable = turnsCapability,
            ItemsCapabilityAvailable = itemsCapability,
            RealtimeCapabilityAvailable = realtimeCapability,
            ProjectRootsTruncated = projectRootsTruncated,
            DynamicToolsTruncated = dynamicToolsTruncated,
            SpawnEdgesTruncated = spawnEdgesTruncated,
            TurnsTruncated = turnsTruncated,
            ItemsTruncated = itemsTruncated,
            RealtimeItemsTruncated = realtimeItemsTruncated,
            StateSourceDescription = stateSourceDescription,
            StateThreadObservations = stateThreadObservations,
            StateReconciliationPolicy = StateReconciliationPolicy,
            StateSourceSelectionRationale = thread is null
                ? null
                : $"Preferred thread metadata comes from {stateSourcePath}; each optional related table uses the first capable source in explicit order, and all {stateThreadObservations.Count:N0} thread row observation(s) remain attached.",
            HistorySourceDescription = historySourceDescription,
            HistorySources = historySources,
            HistoryReconciliationPolicy = HistoryReconciliationPolicy,
            HistorySourceSelectionRationale = historySourceSelectionRationale,
            CoverageWarnings = coverageWarnings
        };
    }

    private static bool Matches(CodexThreadCatalogEntry entry, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        return new[]
            {
                entry.ThreadId, entry.Title, entry.Name, entry.Preview, entry.Cwd,
                entry.Model, entry.ModelProvider, entry.Source, entry.ThreadSource,
                entry.ProjectId, entry.GitBranch
            }
            .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private IReadOnlyList<CodexStateDatabaseCandidate> OrderCandidates() =>
        _sources.DiscoverCandidates()
            .OrderByDescending(candidate => candidate.Generation ?? int.MinValue)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.SourceDescription, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<CodexThreadCatalogEntry> OrderCatalogObservations(
        IEnumerable<CodexThreadCatalogEntry> observations) =>
        observations
            .OrderByDescending(entry => entry.RecencyAtUtc ?? entry.UpdatedAtUtc ?? entry.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.SourceGeneration ?? int.MinValue)
            .ThenByDescending(entry => entry.SourceLastWriteTimeUtc ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.SourceDescription, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase);

    private static bool? ObserveCapability(bool? previous, bool observed) =>
        previous == true || observed
            ? true
            : previous == false || !observed
                ? false
                : null;

    private static bool? ObserveTruncation(bool? previous, bool truncated, bool capabilityObserved) =>
        !capabilityObserved
            ? previous
            : previous == true || truncated
                ? true
                : false;

    private static CodexThreadCatalogEntry? MapThread(
        IReadOnlyDictionary<string, object?> values,
        CodexStateDatabaseCandidate candidate)
    {
        var id = ReadOptionalString(values, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new CodexThreadCatalogEntry(
            id,
            candidate.Path,
            ReadOptionalString(values, "source"),
            ReadOptionalString(values, "thread_source"),
            ReadOptionalString(values, "model_provider"),
            ReadOptionalString(values, "title"),
            ReadOptionalString(values, "name"),
            ReadOptionalString(values, "preview"),
            ReadOptionalString(values, "first_user_message"),
            ReadOptionalString(values, "cwd"),
            ReadTimestamp(values, "created_at_ms", "created_at"),
            ReadTimestamp(values, "updated_at_ms", "updated_at"),
            ReadTimestamp(values, "recency_at_ms", "recency_at", zeroMeansMissing: true),
            ReadOptionalString(values, "model"),
            ReadOptionalString(values, "reasoning_effort"),
            ReadOptionalString(values, "sandbox_policy"),
            ReadOptionalString(values, "approval_mode"),
            ReadOptionalString(values, "history_mode"),
            ReadOptionalString(values, "memory_mode"),
            ReadOptionalString(values, "git_sha"),
            ReadOptionalString(values, "git_branch"),
            ReadOptionalString(values, "git_origin_url"),
            ReadOptionalString(values, "agent_nickname"),
            ReadOptionalString(values, "agent_role"),
            ReadOptionalString(values, "agent_path"),
            ReadOptionalString(values, "project_id"),
            ReadOptionalString(values, "thread_section_id"),
            ReadNullableInt(values, "section_position"),
            ReadNullableBool(values, "archived"),
            ReadNullableBool(values, "is_pinned"))
        {
            SourceDescription = candidate.SourceDescription,
            DiscoveryKind = candidate.DiscoveryKind,
            SourceGeneration = candidate.Generation,
            SourceLastWriteTimeUtc = candidate.LastWriteTimeUtc
        };
    }

    private static async Task<(CodexThreadProject? Project, bool RootsTruncated)> ReadProjectAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken cancellationToken)
    {
        var values = await ReadSingleRowAsync(connection, "projects", "id", projectId, cancellationToken);
        if (values is null)
        {
            return (null, false);
        }

        var rootsCapability = await HasTableAsync(connection, "project_roots", cancellationToken);
        var rootsRead = rootsCapability
            ? await ReadProjectRootsAsync(connection, projectId, cancellationToken)
            : new BoundedRead<string>(Array.Empty<string>(), false);

        return (new CodexThreadProject(
            projectId,
            ReadOptionalString(values, "name") ?? projectId,
            ReadOptionalString(values, "metadata"),
            ReadNullableInt(values, "position"),
            ReadTimestamp(values, "created_at_ms", "created_at"),
            ReadTimestamp(values, "updated_at_ms", "updated_at"),
            rootsRead.Rows)
        {
            RootsCapabilityAvailable = rootsCapability
        }, rootsRead.IsTruncated);
    }

    private static async Task<BoundedRead<string>> ReadProjectRootsAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken cancellationToken)
    {
        var rootRead = await ReadRowsAsync(
            connection,
            "project_roots",
            "project_id = $value",
            projectId,
            await HasColumnAsync(connection, "project_roots", "position", cancellationToken) ? "position ASC" : null,
            MaxHistoryRows,
            cancellationToken);
        var roots = rootRead.Rows
            .Select(root => ReadOptionalString(root, "path"))
            .Where(path => path is not null)
            .Cast<string>()
            .ToArray();
        return new BoundedRead<string>(roots, rootRead.IsTruncated);
    }

    private static async Task<CodexThreadSection?> ReadSectionAsync(
        SqliteConnection connection,
        string sectionId,
        CancellationToken cancellationToken)
    {
        var values = await ReadSingleRowAsync(connection, "thread_sections", "id", sectionId, cancellationToken);
        return values is null
            ? null
            : new CodexThreadSection(
                sectionId,
                ReadOptionalString(values, "name") ?? sectionId,
                ReadOptionalString(values, "appearance"));
    }

    private static async Task<BoundedRead<CodexThreadDynamicTool>> ReadDynamicToolsAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var tools = new List<CodexThreadDynamicTool>();
        var read = await ReadRowsAsync(
            connection,
            "thread_dynamic_tools",
            "thread_id = $value",
            threadId,
            await HasColumnAsync(connection, "thread_dynamic_tools", "position", cancellationToken) ? "position ASC" : null,
            MaxHistoryRows,
            cancellationToken);
        foreach (var values in read.Rows)
        {
            tools.Add(new CodexThreadDynamicTool(
                ReadNullableInt(values, "position") ?? tools.Count,
                ReadOptionalString(values, "name") ?? string.Empty,
                ReadOptionalString(values, "description") ?? string.Empty,
                ReadOptionalString(values, "input_schema") ?? string.Empty,
                ReadNullableBool(values, "defer_loading") ?? false,
                ReadOptionalString(values, "namespace")));
        }

        return new BoundedRead<CodexThreadDynamicTool>(tools.ToArray(), read.IsTruncated);
    }

    private static async Task<BoundedRead<CodexThreadSpawnEdge>> ReadSpawnEdgesAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var all = new List<(string Parent, string Child, string Status)>();
        var read = await ReadRowsAsync(
            connection,
            "thread_spawn_edges",
            whereClause: null,
            parameter: null,
            orderBy: null,
            MaxHistoryRows,
            cancellationToken);
        foreach (var values in read.Rows)
        {
            var parent = ReadOptionalString(values, "parent_thread_id");
            var child = ReadOptionalString(values, "child_thread_id");
            if (parent is not null && child is not null)
            {
                all.Add((parent, child, ReadOptionalString(values, "status") ?? "unknown"));
            }
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { threadId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in all)
            {
                if (reachable.Contains(edge.Parent) || reachable.Contains(edge.Child))
                {
                    changed |= reachable.Add(edge.Parent);
                    changed |= reachable.Add(edge.Child);
                }
            }
        }

        var edges = all
            .Where(edge => reachable.Contains(edge.Parent) && reachable.Contains(edge.Child))
            .Select(edge => new CodexThreadSpawnEdge(
                edge.Parent,
                edge.Child,
                edge.Status,
                DistanceFrom(threadId, edge.Parent, edge.Child, all)))
            .OrderBy(edge => edge.Depth)
            .ThenBy(edge => edge.ParentThreadId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.ChildThreadId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BoundedRead<CodexThreadSpawnEdge>(edges, read.IsTruncated);
    }

    private static int DistanceFrom(
        string selected,
        string parent,
        string child,
        IReadOnlyList<(string Parent, string Child, string Status)> edges)
    {
        if (string.Equals(selected, parent, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selected, child, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var queue = new Queue<(string Id, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected };
        queue.Enqueue((selected, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges)
            {
                var next = string.Equals(edge.Parent, current.Id, StringComparison.OrdinalIgnoreCase)
                    ? edge.Child
                    : string.Equals(edge.Child, current.Id, StringComparison.OrdinalIgnoreCase)
                        ? edge.Parent
                        : null;
                if (next is null || !visited.Add(next))
                {
                    continue;
                }

                if (string.Equals(next, parent, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(next, child, StringComparison.OrdinalIgnoreCase))
                {
                    return current.Depth + 1;
                }

                queue.Enqueue((next, current.Depth + 1));
            }
        }

        return -1;
    }

    private static async Task<BoundedRead<CodexThreadTurn>> ReadTurnsAsync(
        SqliteConnection connection,
        string threadId,
        CodexStateDatabaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        var turns = new List<CodexThreadTurn>();
        var read = await ReadRowsAsync(
            connection,
            "thread_turns",
            "thread_id = $value",
            threadId,
            await HasColumnAsync(connection, "thread_turns", "rollout_ordinal", cancellationToken) ? "rollout_ordinal ASC" : null,
            MaxHistoryRows,
            cancellationToken);
        foreach (var values in read.Rows)
        {
            var turnId = ReadOptionalString(values, "turn_id");
            if (turnId is null)
            {
                continue;
            }

            var rolloutOrdinal = ReadNullableLong(values, "rollout_ordinal");
            turns.Add(new CodexThreadTurn(
                turnId,
                rolloutOrdinal ?? turns.Count,
                ReadOptionalString(values, "status") ?? "unknown",
                ReadOptionalString(values, "error_json"),
                ReadTimestamp(values, "started_at_ms", "started_at"),
                ReadTimestamp(values, "completed_at_ms", "completed_at"),
                ReadNullableLong(values, "duration_ms"),
                ReadOptionalString(values, "first_user_item_id"),
                ReadOptionalString(values, "final_agent_item_id"),
                ReadNullableLong(values, "rollout_byte_offset"),
                 ReadNullableLong(values, "rollout_end_ordinal"),
                 ReadNullableLong(values, "rollout_end_byte_offset"))
            {
                SourcePath = candidate.Path,
                SourceDescription = candidate.SourceDescription,
                RolloutOrdinalAvailable = rolloutOrdinal is not null
            });
        }

        return new BoundedRead<CodexThreadTurn>(turns.ToArray(), read.IsTruncated);
    }

    private static async Task<BoundedRead<CodexThreadItem>> ReadItemsAsync(
        SqliteConnection connection,
        string threadId,
        CodexStateDatabaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        var items = new List<CodexThreadItem>();
        var read = await ReadRowsAsync(
            connection,
            "thread_items",
            "thread_id = $value",
            threadId,
            await HasColumnAsync(connection, "thread_items", "rollout_ordinal", cancellationToken) ? "rollout_ordinal ASC" : null,
            MaxHistoryRows,
            cancellationToken);
        foreach (var values in read.Rows)
        {
            var itemId = ReadOptionalString(values, "item_id");
            if (itemId is null)
            {
                continue;
            }

            var rolloutOrdinal = ReadNullableLong(values, "rollout_ordinal");
            items.Add(new CodexThreadItem(
                ReadOptionalString(values, "turn_id") ?? string.Empty,
                itemId,
                rolloutOrdinal ?? items.Count,
                ReadTimestamp(values, "created_at_ms", "created_at"),
                ReadOptionalString(values, "item_type") ?? string.Empty,
                 ReadOptionalString(values, "item_json") ?? string.Empty,
                 ReadNullableLong(values, "updated_at_ordinal"))
            {
                SourcePath = candidate.Path,
                SourceDescription = candidate.SourceDescription,
                RolloutOrdinalAvailable = rolloutOrdinal is not null
            });
        }

        return new BoundedRead<CodexThreadItem>(items.ToArray(), read.IsTruncated);
    }

    private static async Task<BoundedRead<CodexThreadRealtimeItem>> ReadRealtimeItemsAsync(
        SqliteConnection connection,
        string threadId,
        CodexStateDatabaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        var items = new List<CodexThreadRealtimeItem>();
        var read = await ReadRowsAsync(
            connection,
            "thread_realtime_items",
            "thread_id = $value",
            threadId,
            await HasColumnAsync(connection, "thread_realtime_items", "rollout_ordinal", cancellationToken) ? "rollout_ordinal ASC" : null,
            MaxHistoryRows,
            cancellationToken);
        foreach (var values in read.Rows)
        {
            var itemId = ReadOptionalString(values, "item_id");
            if (itemId is null)
            {
                continue;
            }

            var rolloutOrdinal = ReadNullableLong(values, "rollout_ordinal");
            items.Add(new CodexThreadRealtimeItem(
                itemId,
                rolloutOrdinal ?? items.Count,
                ReadTimestamp(values, "created_at_ms", "created_at"),
                 ReadOptionalString(values, "item_type") ?? string.Empty,
                 ReadOptionalString(values, "item_json") ?? string.Empty)
            {
                SourcePath = candidate.Path,
                SourceDescription = candidate.SourceDescription,
                RolloutOrdinalAvailable = rolloutOrdinal is not null
            });
        }

        return new BoundedRead<CodexThreadRealtimeItem>(items.ToArray(), read.IsTruncated);
    }

    private static async Task<Dictionary<string, object?>?> ReadSingleRowAsync(
        SqliteConnection connection,
        string table,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        var read = await ReadRowsAsync(
            connection,
            table,
            $"{QuoteIdentifier(column)} = $value",
            value,
            orderBy: null,
            limit: 1,
            cancellationToken);
        return read.Rows.FirstOrDefault();
    }

    private static async Task<BoundedRows> ReadRowsAsync(
        SqliteConnection connection,
        string table,
        string? whereClause,
        string? parameter,
        string? orderBy,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var sql = $"SELECT * FROM {QuoteIdentifier(table)}";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += " WHERE " + whereClause;
        }
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sql += " ORDER BY " + orderBy;
        }
        sql += " LIMIT $limit;";
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("$value", parameter);
        }
        var requestedLimit = Math.Max(1, limit);
        command.Parameters.AddWithValue("$limit", (long)requestedLimit + 1L);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Dictionary<string, object?>>(requestedLimit);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= requestedLimit)
            {
                return new BoundedRows(rows, IsTruncated: true);
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(values);
        }

        return new BoundedRows(rows, IsTruncated: false);
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(table)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string? ReadOptionalString(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value is null || value is DBNull)
        {
            return null;
        }

        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            byte[] bytes => Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static long? ReadNullableLong(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value is null || value is DBNull)
        {
            return null;
        }

        try
        {
            return value switch
            {
                long number => number,
                int number => number,
                short number => number,
                byte number => number,
                double number => checked((long)number),
                decimal number => checked((long)number),
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
                _ => null
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static int? ReadNullableInt(IReadOnlyDictionary<string, object?> values, string name)
    {
        var value = ReadNullableLong(values, name);
        return value is long number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
    }

    private static bool? ReadNullableBool(IReadOnlyDictionary<string, object?> values, string name)
    {
        var number = ReadNullableLong(values, name);
        if (number is not null)
        {
            return number.Value != 0;
        }

        var text = ReadOptionalString(values, name);
        return bool.TryParse(text, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadTimestamp(
        IReadOnlyDictionary<string, object?> values,
        string preferred,
        string fallback,
        bool zeroMeansMissing = false)
    {
        if (values.TryGetValue(preferred, out var preferredValue) && preferredValue is not null && preferredValue is not DBNull)
        {
            var parsed = ParseTimestamp(preferredValue, zeroMeansMissing);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return values.TryGetValue(fallback, out var fallbackValue) && fallbackValue is not null && fallbackValue is not DBNull
            ? ParseTimestamp(fallbackValue, zeroMeansMissing)
            : null;
    }

    private static DateTimeOffset? ParseTimestamp(object value, bool zeroMeansMissing)
    {
        if (value is string text)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return null;
            }

            value = integer;
        }

        try
        {
            var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (zeroMeansMissing && number == 0)
            {
                return null;
            }

            return Math.Abs(number) >= 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number);
        }
        catch (Exception) when (value is IConvertible)
        {
            return null;
        }
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsSourceFailure(Exception? exception = null) =>
        exception is null || exception is SqliteException or IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException or
        InvalidCastException or FormatException or OverflowException;

    private static string Summarize(string message) =>
        message.ReplaceLineEndings(" ").Trim() switch
        {
            var compact when compact.Length <= 260 => compact,
            var compact => compact[..260] + "…"
        };
}
