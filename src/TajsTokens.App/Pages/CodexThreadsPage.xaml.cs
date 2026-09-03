using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class CodexThreadsPage : Page
{
    private CancellationTokenSource? _cancellation;
    private bool _loaded;
    private bool _loading;
    private bool _reloadRequested;
    private long _searchGeneration;
    private long _selectionGeneration;

    public CodexThreadsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        var previous = Interlocked.Exchange(ref _cancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            await LoadAsync(_cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation?.IsCancellationRequested != false)
        {
            // Navigation owns cancellation for a detached page.
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _reloadRequested = false;
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _selectionGeneration);
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _cancellation;
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        await LoadAsync(cancellation.Token);
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var cancellation = _cancellation;
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _searchGeneration);
        var search = SearchTextBox.Text.Trim();
        try
        {
            await Task.Delay(150, cancellation.Token);
            if (generation != Volatile.Read(ref _searchGeneration))
            {
                return;
            }

            await LoadAsync(cancellation.Token, search, generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Navigation owns cancellation.
        }
        catch (Exception exception)
        {
            if (_loaded && generation == Volatile.Read(ref _searchGeneration))
            {
                StatusText.Text = $"Thread search unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken, string? requestedSearch = null, long? searchGeneration = null)
    {
        if (!_loaded)
        {
            return;
        }

        if (_loading)
        {
            _reloadRequested = true;
            return;
        }

        var search = requestedSearch ?? SearchTextBox.Text.Trim();
        _loading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                // A search or refresh can arrive while a previous read is in flight. Re-read the
                // current box value for the queued pass so the latest query is not lost.
                search = SearchTextBox.Text.Trim();
                StatusText.Text = "Reading discovered Codex thread sources…";
                var result = await Task.Run(
                    () => App.Services.CodexThreadObservability.SearchThreadsAsync(
                        search,
                        search.Length == 0 ? 750 : 250,
                        cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (!_loaded || (searchGeneration is not null &&
                                 searchGeneration.Value != Volatile.Read(ref _searchGeneration)) ||
                    !string.Equals(SearchTextBox.Text.Trim(), search, StringComparison.Ordinal))
                {
                    return;
                }

                var selectedId = (ThreadList.SelectedItem as CodexThreadCatalogSearchEntry)?.Preferred.ThreadId;
                ThreadList.ItemsSource = result.Entries;
                if (selectedId is not null)
                {
                    ThreadList.SelectedItem = result.Entries.FirstOrDefault(entry =>
                        string.Equals(entry.Preferred.ThreadId, selectedId, StringComparison.OrdinalIgnoreCase));
                }

                if (ThreadList.SelectedItem is null && result.Entries.Count > 0)
                {
                    ThreadList.SelectedIndex = 0;
                }

                if (result.Entries.Count == 0)
                {
                    DetailText.Text = search.Length == 0
                        ? "No source-native Codex thread is available to inspect."
                        : "No matching source-native Codex thread is available to inspect.";
                }

                var diagnostics = result.Warnings.Count == 0
                    ? string.Empty
                    : $" · {result.Warnings.Count:N0} source warning(s)";
                var coverage = result.CoverageWarnings.Count == 0
                    ? string.Empty
                    : $" · bounded coverage reached ({result.CoverageWarnings.Count:N0} warning(s))";
                StatusText.Text = result.Entries.Count == 0
                    ? (search.Length == 0 ? "No source-native Codex threads were found." : "No matching source-native Codex threads were found.")
                    : $"{result.Entries.Count:N0} thread(s) · {result.SourceObservations.Count:N0} source observation(s){diagnostics}{coverage}.";
            }
            while (_reloadRequested && _loaded && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_loaded)
            {
                StatusText.Text = $"Codex thread inspection unavailable: {Summarize(exception.Message)}";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnThreadSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = ThreadList.SelectedItem as CodexThreadCatalogSearchEntry;
        var cancellation = _cancellation;
        var generation = Interlocked.Increment(ref _selectionGeneration);
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested || entry is null)
        {
            if (entry is null)
            {
                DetailText.Text = "Select a Codex thread to inspect its source-native metadata and history.";
            }

            return;
        }

        DetailText.Text = "Loading source-native thread metadata and history…";
        try
        {
            var result = await Task.Run(
                () => App.Services.CodexThreadObservability.ReadThreadAsync(
                    entry.Preferred.ThreadId,
                    cancellation.Token),
                cancellation.Token);
            if (!_loaded || cancellation.IsCancellationRequested ||
                !ReferenceEquals(_cancellation, cancellation) ||
                generation != Volatile.Read(ref _selectionGeneration))
            {
                return;
            }

            DetailText.Text = RenderDetail(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_loaded && generation == Volatile.Read(ref _selectionGeneration))
            {
                DetailText.Text = $"Thread detail unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private static string RenderDetail(CodexThreadReadResult result)
    {
        if (!result.HasThread && result.Turns.Count == 0 && result.Items.Count == 0 && result.RealtimeItems.Count == 0)
        {
            return string.Join("\n", result.Warnings.DefaultIfEmpty(
                "No source-native state/history row is available for this thread."));
        }

        var thread = result.Thread;
        var lines = new List<string>
        {
            "Source-native Codex thread (on-demand local inspection)",
            $"Captured: {FormatDate(result.CapturedAtUtc)}",
            $"Thread ID: {thread?.ThreadId ?? "unknown"}",
            $"Display name (history-mode policy): {thread?.DisplayName ?? "unknown"} · title={thread?.Title ?? "unavailable"} · name={thread?.Name ?? "unavailable"}",
            $"State source: {result.StateSourcePath ?? "unavailable"} · {result.StateSourceDescription ?? "unknown"}",
            $"History source: {result.HistorySourcePath ?? "unavailable"} · {result.HistorySourceDescription ?? "unknown"}",
            $"Source: {thread?.Source ?? "unavailable"} · thread source: {thread?.ThreadSource ?? "unavailable"} · provider: {thread?.ModelProvider ?? "unavailable"}",
            $"Model: {thread?.Model ?? "unavailable"} · reasoning: {thread?.ReasoningEffort ?? "unavailable"}",
            $"Created: {FormatDate(thread?.CreatedAtUtc)} · updated: {FormatDate(thread?.UpdatedAtUtc)} · recency: {FormatDate(thread?.RecencyAtUtc)}",
            $"CWD: {thread?.Cwd ?? "unavailable"}",
            $"Git: {thread?.GitBranch ?? "unavailable"} · {thread?.GitSha ?? "unavailable"} · {thread?.GitOriginUrl ?? "unavailable"}",
            $"Sandbox: {thread?.SandboxPolicy ?? "unavailable"} · approval: {thread?.ApprovalMode ?? "unavailable"}",
            $"History mode: {thread?.HistoryMode ?? "unavailable"} · memory: {thread?.MemoryMode ?? "unavailable"} · archived: {FormatBool(thread?.Archived)} · pinned: {FormatBool(thread?.IsPinned)}",
            $"Agent: {thread?.AgentNickname ?? "unavailable"} · role: {thread?.AgentRole ?? "unavailable"} · path: {thread?.AgentPath ?? "unavailable"}",
            $"State observations: {result.StateThreadObservations.Count:N0} · policy: {result.StateReconciliationPolicy}",
            $"History sources: {result.HistorySources.Count:N0} · policy: {result.HistoryReconciliationPolicy}"
        };

        if (result.Project is { } project)
        {
            lines.Add($"Project: {project.Name} ({project.ProjectId}) · capability: {FormatCapability(result.ProjectCapabilityAvailable)} · position: {project.Position?.ToString() ?? "unavailable"} · metadata: {(project.Metadata is null ? "unavailable" : Summarize(project.Metadata))} · roots capability: {FormatCapability(result.ProjectRootsCapabilityAvailable)}");
            lines.AddRange(project.OrderedRoots.Select((root, index) => $"  root[{index}]: {root}"));
        }
        else
        {
            lines.Add($"Project: unavailable · capability: {FormatCapability(result.ProjectCapabilityAvailable)}");
        }

        if (result.Section is { } section)
        {
            lines.Add($"Section: {section.Name} ({section.SectionId}) · position: {thread?.SectionPosition?.ToString() ?? "unavailable"} · appearance: {section.Appearance ?? "unavailable"}");
        }
        else
        {
            lines.Add($"Section: unavailable · capability: {FormatCapability(result.SectionCapabilityAvailable)}");
        }

        lines.Add($"Spawn edges (directional): {result.SpawnEdges.Count:N0} · capability: {FormatCapability(result.SpawnEdgesCapabilityAvailable)} · {FormatTruncation(result.SpawnEdgesTruncated)}");
        lines.AddRange(result.SpawnEdges.Take(80).Select(edge =>
            $"  {edge.ParentThreadId} → {edge.ChildThreadId} · status={edge.Status} · depth={edge.Depth}"));
        lines.Add($"Dynamic tools: {result.DynamicTools.Count:N0} · capability: {FormatCapability(result.DynamicToolsCapabilityAvailable)} · {FormatTruncation(result.DynamicToolsTruncated)}");
        lines.AddRange(result.DynamicTools.Take(80).Select(tool =>
            $"  [{tool.Position}] {tool.Name} · namespace={tool.Namespace ?? "unavailable"} · defer_loading={tool.DeferLoading} · description={Summarize(tool.Description)} · input_schema={Summarize(tool.InputSchema)}"));
        lines.Add($"Turns (rollout order): {result.Turns.Count:N0} · capability: {FormatCapability(result.TurnsCapabilityAvailable)} · {FormatTruncation(result.TurnsTruncated)}");
        lines.AddRange(result.Turns.Take(120).Select(turn =>
            $"  [{FormatOrdinal(turn.RolloutOrdinal, turn.RolloutOrdinalAvailable)}] {turn.TurnId} · status={turn.Status} · started={FormatDate(turn.StartedAtUtc)} · completed={FormatDate(turn.CompletedAtUtc)} · duration={FormatDuration(turn.DurationMs)} · first_user={turn.FirstUserItemId ?? "unavailable"} · final_agent={turn.FinalAgentItemId ?? "unavailable"}"));
        lines.Add($"Normal history items: {result.Items.Count:N0} · capability: {FormatCapability(result.ItemsCapabilityAvailable)} · {FormatTruncation(result.ItemsTruncated)}");
        lines.AddRange(result.Items.Take(160).Select(item =>
            $"  [{FormatOrdinal(item.RolloutOrdinal, item.RolloutOrdinalAvailable)}] {item.ItemType} · {item.ItemId} · turn={item.TurnId}"));
        lines.Add($"Realtime timeline (separate lane): {result.RealtimeItems.Count:N0} · capability: {FormatCapability(result.RealtimeCapabilityAvailable)} · {FormatTruncation(result.RealtimeItemsTruncated)}");
        lines.AddRange(result.RealtimeItems.Take(160).Select(item =>
            $"  [{FormatOrdinal(item.RolloutOrdinal, item.RolloutOrdinalAvailable)}] {item.ItemType} · {item.ItemId}"));

        if (result.CoverageWarnings.Count > 0)
        {
            lines.Add("Coverage warnings:");
            lines.AddRange(result.CoverageWarnings.Select(warning => $"  {warning}"));
        }

        if (result.Warnings.Count > 0)
        {
            lines.Add("Source read warnings:");
            lines.AddRange(result.Warnings.Select(warning => $"  {warning}"));
        }

        if (result.Items.Count > 160 || result.RealtimeItems.Count > 160 || result.Turns.Count > 120)
        {
            lines.Add("UI summary abbreviation: content-bearing item JSON remains available only through deliberate local raw inspection.");
        }

        return string.Join("\n", lines);

        static string FormatDate(DateTimeOffset? value) => value?.ToLocalTime().ToString("g") ?? "unavailable";
        static string FormatBool(bool? value) => value is null ? "unavailable" : value.Value ? "yes" : "no";
        static string FormatCapability(bool? value) => value is null ? "unknown" : value.Value ? "present" : "absent";
        static string FormatTruncation(bool? value) => value is null ? "coverage unknown" : value.Value ? "truncated" : "complete";
        static string FormatOrdinal(long value, bool available) => available ? value.ToString() : "unknown";
        static string FormatDuration(long? value) => value is long milliseconds ? $"{milliseconds:N0} ms" : "unavailable";
    }

    private static string Summarize(string message)
    {
        var compact = message.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 260 ? compact : compact[..260] + "…";
    }
}
