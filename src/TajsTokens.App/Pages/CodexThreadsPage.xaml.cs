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
                    () => App.Services.CodexThreadReadModel.SearchThreadsAsync(
                        search,
                        search.Length == 0 ? 750 : 250,
                        cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (!_loaded)
                {
                    return;
                }

                if ((searchGeneration is not null &&
                     searchGeneration.Value != Volatile.Read(ref _searchGeneration)) ||
                    !string.Equals(SearchTextBox.Text.Trim(), search, StringComparison.Ordinal))
                {
                    // The result is stale, but the active source read has completed. Consume the
                    // queued request in the loop using the latest TextBox value instead of
                    // returning while _loading is still true and dropping that request.
                    searchGeneration = null;
                    _reloadRequested = true;
                    continue;
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
                    SetDetailMessage(search.Length == 0
                        ? "No source-native Codex thread is available to inspect."
                        : "No matching source-native Codex thread is available to inspect.");
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
                SetDetailMessage("Select a Codex thread to inspect its source-native metadata and history.");
            }

            return;
        }

        SetDetailMessage("Loading source-native thread metadata and history…");
        try
        {
            var result = await Task.Run(
                () => App.Services.CodexThreadReadModel.ReadThreadAsync(
                    entry.Preferred.ThreadId,
                    cancellation.Token),
                cancellation.Token);
            if (!_loaded || cancellation.IsCancellationRequested ||
                !ReferenceEquals(_cancellation, cancellation) ||
                generation != Volatile.Read(ref _selectionGeneration))
            {
                return;
            }

            RenderDetail(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_loaded && generation == Volatile.Read(ref _selectionGeneration))
            {
                SetDetailMessage($"Thread detail unavailable: {Summarize(exception.Message)}");
            }
        }
    }

    private void RenderDetail(CodexThreadReadResult result)
    {
        DetailPanel.Children.Clear();
        if (!result.HasThread && result.Turns.Count == 0 && result.Items.Count == 0 && result.RealtimeItems.Count == 0)
        {
            AddNotice(DetailPanel, string.Join("\n", result.Warnings.DefaultIfEmpty(
                "No source-native state/history row is available for this thread.")));
            return;
        }

        var thread = result.Thread;
        var overview = AddSection(DetailPanel, "Thread overview",
            "Source-native values are shown as observed. Missing fields remain unavailable.");
        AddFields(overview,
            ("Captured", FormatDate(result.CapturedAtUtc)),
            ("Thread ID", Value(thread?.ThreadId)),
            ("Display name", Value(thread?.DisplayName)),
            ("Title", Value(thread?.Title)),
            ("Name", Value(thread?.Name)),
            ("Source", Value(thread?.Source)),
            ("Thread source", Value(thread?.ThreadSource)),
            ("Model provider", Value(thread?.ModelProvider)),
            ("Model", Value(thread?.Model)),
            ("Reasoning effort", Value(thread?.ReasoningEffort)),
            ("History mode", Value(thread?.HistoryMode)),
            ("Memory mode", Value(thread?.MemoryMode)),
            ("Created", FormatDate(thread?.CreatedAtUtc)),
            ("Updated", FormatDate(thread?.UpdatedAtUtc)),
            ("Recency", FormatDate(thread?.RecencyAtUtc)),
            ("Archived", FormatBool(thread?.Archived)),
            ("Pinned", FormatBool(thread?.IsPinned)));

        var workspace = AddSection(DetailPanel, "Workspace, policies and agent");
        AddFields(workspace,
            ("Working directory", Value(thread?.Cwd)),
            ("Git branch", Value(thread?.GitBranch)),
            ("Git SHA", Value(thread?.GitSha)),
            ("Git origin URL", Value(thread?.GitOriginUrl)),
            ("Sandbox policy", Value(thread?.SandboxPolicy)),
            ("Approval mode", Value(thread?.ApprovalMode)),
            ("Agent nickname", Value(thread?.AgentNickname)),
            ("Agent role", Value(thread?.AgentRole)),
            ("Agent path", Value(thread?.AgentPath)),
            ("Project ID", Value(thread?.ProjectId)),
            ("Section ID", Value(thread?.SectionId)),
            ("Section position", FormatInt(thread?.SectionPosition)),
            ("Discovery kind", Value(thread?.DiscoveryKind)),
            ("Source generation", FormatInt(thread?.SourceGeneration)),
            ("Source last write", FormatDate(thread?.SourceLastWriteTimeUtc)));

        var provenance = AddSection(DetailPanel, "Source provenance and read-model policy");
        AddFields(provenance,
            ("State source", Value(result.StateSourcePath)),
            ("State source description", Value(result.StateSourceDescription)),
            ("History source", Value(result.HistorySourcePath)),
            ("History source description", Value(result.HistorySourceDescription)),
            ("State observations", result.StateThreadObservations.Count.ToString("N0")),
            ("State sources retained", result.StateSources.Count.ToString("N0")),
            ("State reconciliation", Value(result.StateReconciliationPolicy)),
            ("State selection rationale", Value(result.StateSourceSelectionRationale)),
            ("History sources retained", result.HistorySources.Count.ToString("N0")),
            ("History reconciliation", Value(result.HistoryReconciliationPolicy)),
            ("History selection rationale", Value(result.HistorySourceSelectionRationale)));

        var coverage = AddSection(DetailPanel, "Capabilities and coverage");
        AddFields(coverage,
            ("Project capability", FormatCapability(result.ProjectCapabilityAvailable)),
            ("Project roots capability", FormatCapability(result.ProjectRootsCapabilityAvailable)),
            ("Section capability", FormatCapability(result.SectionCapabilityAvailable)),
            ("Dynamic tools capability", FormatCapability(result.DynamicToolsCapabilityAvailable)),
            ("Spawn edges capability", FormatCapability(result.SpawnEdgesCapabilityAvailable)),
            ("Turns capability", FormatCapability(result.TurnsCapabilityAvailable)),
            ("Normal items capability", FormatCapability(result.ItemsCapabilityAvailable)),
            ("Realtime capability", FormatCapability(result.RealtimeCapabilityAvailable)),
            ("Project roots coverage", FormatTruncation(result.ProjectRootsTruncated)),
            ("Dynamic tools coverage", FormatTruncation(result.DynamicToolsTruncated)),
            ("Spawn edges coverage", FormatTruncation(result.SpawnEdgesTruncated)),
            ("Turns coverage", FormatTruncation(result.TurnsTruncated)),
            ("Normal items coverage", FormatTruncation(result.ItemsTruncated)),
            ("Realtime coverage", FormatTruncation(result.RealtimeItemsTruncated)));

        var projectSection = AddSection(DetailPanel, "Project");
        if (result.Project is { } project)
        {
            AddProjectFields(projectSection, project, result);
        }
        else
        {
            AddFields(projectSection,
                ("Value", "unavailable"),
                ("Capability", FormatCapability(result.ProjectCapabilityAvailable)));
        }

        var sectionSection = AddSection(DetailPanel, "Section");
        if (result.Section is { } section)
        {
            AddFields(sectionSection,
                ("Section ID", Value(section.SectionId)),
                ("Name", Value(section.Name)),
                ("Position", FormatInt(thread?.SectionPosition)),
                ("Appearance", Value(section.Appearance)),
                ("Capability", FormatCapability(result.SectionCapabilityAvailable)));
        }
        else
        {
            AddFields(sectionSection,
                ("Value", "unavailable"),
                ("Capability", FormatCapability(result.SectionCapabilityAvailable)));
        }

        var stateObservations = AddSection(DetailPanel,
            $"State observations ({result.StateSources.Count:N0})",
            "Each readable state database is retained separately; expand a row to inspect its source-qualified metadata.",
            expanded: false);
        if (result.StateSources.Count == 0)
        {
            AddNotice(stateObservations, "No matching state-source observation was available.");
        }
        else
        {
            for (var index = 0; index < result.StateSources.Count; index++)
            {
                var source = result.StateSources[index];
                var sourceBody = AddRecord(stateObservations,
                    $"{index + 1}. {source.SourceDescription}",
                    ("Source path", Value(source.SourcePath)),
                    ("Project capability", FormatCapability(source.ProjectCapabilityAvailable)),
                    ("Project roots capability", FormatCapability(source.ProjectRootsCapabilityAvailable)),
                    ("Section capability", FormatCapability(source.SectionCapabilityAvailable)),
                    ("Dynamic tools capability", FormatCapability(source.DynamicToolsCapabilityAvailable)),
                    ("Spawn edges capability", FormatCapability(source.SpawnEdgesCapabilityAvailable)),
                    ("Project roots coverage", FormatTruncation(source.ProjectRootsTruncated)),
                    ("Dynamic tools coverage", FormatTruncation(source.DynamicToolsTruncated)),
                    ("Spawn edges coverage", FormatTruncation(source.SpawnEdgesTruncated)));
                AddNotice(sourceBody, "Thread row in this source");
                AddCatalogFields(sourceBody, source.Thread);
            }
        }

        if (result.StateReadModel is { } stateReadModel)
        {
            var reconciliation = AddSection(DetailPanel, "Retained alternatives and conflicts",
                "The preferred value is a presentation choice. Alternatives remain visible so conflicting source observations are not silently lost.",
                expanded: false);
            AddFields(reconciliation,
                ("Selection rationale", Value(stateReadModel.SelectionRationale)),
                ("Project alternatives", stateReadModel.ProjectAlternatives.Count.ToString("N0") +
                 $" · conflict={FormatBool(stateReadModel.ProjectConflict)}"),
                ("Project-roots alternatives", stateReadModel.ProjectRootsAlternatives.Count.ToString("N0") +
                 $" · conflict={FormatBool(stateReadModel.ProjectRootsConflict)}"),
                ("Section alternatives", stateReadModel.SectionAlternatives.Count.ToString("N0") +
                 $" · conflict={FormatBool(stateReadModel.SectionConflict)}"),
                ("Dynamic-tool alternatives", stateReadModel.DynamicToolsAlternatives.Count.ToString("N0") +
                 $" · conflict={FormatBool(stateReadModel.DynamicToolsConflict)}"),
                ("Spawn-edge alternatives", stateReadModel.SpawnEdgesAlternatives.Count.ToString("N0") +
                 $" · conflict={FormatBool(stateReadModel.SpawnEdgesConflict)}"));

            foreach (var (alternative, index) in stateReadModel.ProjectAlternatives.Select((value, index) => (value, index)))
            {
                var alternativeBody = AddRecord(reconciliation, $"Project alternative {index + 1}");
                AddProjectFields(alternativeBody, alternative, result);
            }

            foreach (var (roots, index) in stateReadModel.ProjectRootsAlternatives.Select((value, index) => (value, index)))
            {
                AddRecord(reconciliation, $"Project roots alternative {index + 1}",
                    ("Roots", roots.Count == 0 ? "empty" : string.Join(Environment.NewLine, roots)));
            }

            foreach (var (alternative, index) in stateReadModel.SectionAlternatives.Select((value, index) => (value, index)))
            {
                AddRecord(reconciliation, $"Section alternative {index + 1}",
                    ("Section ID", Value(alternative.SectionId)),
                    ("Name", Value(alternative.Name)),
                    ("Appearance", Value(alternative.Appearance)));
            }

            AddAlternativeTools(reconciliation, stateReadModel.DynamicToolsAlternatives);
            AddAlternativeEdges(reconciliation, stateReadModel.SpawnEdgesAlternatives);
        }

        var topology = AddSection(DetailPanel, $"Spawn edges ({result.SpawnEdges.Count:N0})",
            "Directional parent → child relationships from the source-native topology table.",
            expanded: false);
        AddFields(topology,
            ("Capability", FormatCapability(result.SpawnEdgesCapabilityAvailable)),
            ("Coverage", FormatTruncation(result.SpawnEdgesTruncated)));
        foreach (var edge in result.SpawnEdges.Take(160))
        {
            AddRecord(topology, $"{edge.ParentThreadId} → {edge.ChildThreadId}",
                ("Parent thread ID", edge.ParentThreadId),
                ("Child thread ID", edge.ChildThreadId),
                ("Status", Value(edge.Status)),
                ("Depth", edge.Depth.ToString()));
        }
        AddLimitNotice(topology, result.SpawnEdges.Count, 160);

        var tools = AddSection(DetailPanel, $"Dynamic tools ({result.DynamicTools.Count:N0})",
            "Tool definitions are source metadata; input schemas are shown locally and are not persisted by TajsTokens.",
            expanded: false);
        AddFields(tools,
            ("Capability", FormatCapability(result.DynamicToolsCapabilityAvailable)),
            ("Coverage", FormatTruncation(result.DynamicToolsTruncated)));
        foreach (var tool in result.DynamicTools.Take(160))
        {
            AddRecord(tools, $"[{tool.Position}] {Value(tool.Name)}",
                ("Position", tool.Position.ToString()),
                ("Name", Value(tool.Name)),
                ("Namespace", Value(tool.Namespace)),
                ("Description", Value(tool.Description)),
                ("Input schema", Value(tool.InputSchema)),
                ("Defer loading", tool.DeferLoading ? "yes" : "no"));
        }
        AddLimitNotice(tools, result.DynamicTools.Count, 160);

        var historySources = AddSection(DetailPanel, $"History sources ({result.HistorySources.Count:N0})",
            "History stores remain separate observations. The flat lanes below are a deterministic union for reading.",
            expanded: false);
        if (result.HistorySources.Count == 0)
        {
            AddNotice(historySources, "No history-source observation was available.");
        }
        else
        {
            for (var index = 0; index < result.HistorySources.Count; index++)
            {
                var source = result.HistorySources[index];
                AddRecord(historySources, $"{index + 1}. {source.SourceDescription}",
                    ("Source path", Value(source.SourcePath)),
                    ("Turns", source.Turns.Count.ToString("N0")),
                    ("Turns capability", FormatCapability(source.TurnsCapabilityAvailable)),
                    ("Turns coverage", FormatTruncation(source.TurnsTruncated)),
                    ("Normal items", source.Items.Count.ToString("N0")),
                    ("Items capability", FormatCapability(source.ItemsCapabilityAvailable)),
                    ("Items coverage", FormatTruncation(source.ItemsTruncated)),
                    ("Realtime items", source.RealtimeItems.Count.ToString("N0")),
                    ("Realtime capability", FormatCapability(source.RealtimeCapabilityAvailable)),
                    ("Realtime coverage", FormatTruncation(source.RealtimeItemsTruncated)));
            }
        }

        var turns = AddSection(DetailPanel, $"Turns ({result.Turns.Count:N0})",
            "Materialized turn metadata in rollout order. Error payloads are represented as presence so the detail view does not become a transcript dump.",
            expanded: false);
        AddFields(turns,
            ("Capability", FormatCapability(result.TurnsCapabilityAvailable)),
            ("Coverage", FormatTruncation(result.TurnsTruncated)));
        foreach (var turn in result.Turns.Take(160))
        {
            AddRecord(turns, $"[{FormatOrdinal(turn.RolloutOrdinal, turn.RolloutOrdinalAvailable)}] {turn.TurnId}",
                ("Turn ID", turn.TurnId),
                ("Rollout ordinal", FormatOrdinal(turn.RolloutOrdinal, turn.RolloutOrdinalAvailable)),
                ("Status", Value(turn.Status)),
                ("Started", FormatDate(turn.StartedAtUtc)),
                ("Completed", FormatDate(turn.CompletedAtUtc)),
                ("Duration", FormatDuration(turn.DurationMs)),
                ("First user item ID", Value(turn.FirstUserItemId)),
                ("Final agent item ID", Value(turn.FinalAgentItemId)),
                ("Rollout byte offset", FormatLong(turn.RolloutByteOffset)),
                ("Rollout end ordinal", FormatLong(turn.RolloutEndOrdinal)),
                ("Rollout end byte offset", FormatLong(turn.RolloutEndByteOffset)),
                ("Error JSON", FormatPayloadPresence(turn.ErrorJson)),
                ("Source", Value(turn.SourceDescription)),
                ("Source path", Value(turn.SourcePath)));
        }
        AddLimitNotice(turns, result.Turns.Count, 160);

        var items = AddSection(DetailPanel, $"Normal history items ({result.Items.Count:N0})",
            "Item metadata is readable here. Content-bearing item JSON remains outside the summary and is not copied into TajsTokens storage.",
            expanded: false);
        AddFields(items,
            ("Capability", FormatCapability(result.ItemsCapabilityAvailable)),
            ("Coverage", FormatTruncation(result.ItemsTruncated)));
        foreach (var item in result.Items.Take(200))
        {
            AddRecord(items, $"[{FormatOrdinal(item.RolloutOrdinal, item.RolloutOrdinalAvailable)}] {Value(item.ItemType)} · {item.ItemId}",
                ("Item ID", item.ItemId),
                ("Item type", Value(item.ItemType)),
                ("Turn ID", Value(item.TurnId)),
                ("Rollout ordinal", FormatOrdinal(item.RolloutOrdinal, item.RolloutOrdinalAvailable)),
                ("Created", FormatDate(item.CreatedAtUtc)),
                ("Updated-at ordinal", FormatLong(item.UpdatedAtOrdinal)),
                ("Source", Value(item.SourceDescription)),
                ("Source path", Value(item.SourcePath)),
                ("Item payload", FormatPayloadPresence(item.ItemJson)));
        }
        AddLimitNotice(items, result.Items.Count, 200);

        var realtime = AddSection(DetailPanel, $"Realtime timeline ({result.RealtimeItems.Count:N0})",
            "Realtime items remain a separate source-native lane rather than being merged into normal history.",
            expanded: false);
        AddFields(realtime,
            ("Capability", FormatCapability(result.RealtimeCapabilityAvailable)),
            ("Coverage", FormatTruncation(result.RealtimeItemsTruncated)));
        foreach (var item in result.RealtimeItems.Take(200))
        {
            AddRecord(realtime, $"[{FormatOrdinal(item.RolloutOrdinal, item.RolloutOrdinalAvailable)}] {Value(item.ItemType)} · {item.ItemId}",
                ("Item ID", item.ItemId),
                ("Item type", Value(item.ItemType)),
                ("Rollout ordinal", FormatOrdinal(item.RolloutOrdinal, item.RolloutOrdinalAvailable)),
                ("Created", FormatDate(item.CreatedAtUtc)),
                ("Source", Value(item.SourceDescription)),
                ("Source path", Value(item.SourcePath)),
                ("Item payload", FormatPayloadPresence(item.ItemJson)));
        }
        AddLimitNotice(realtime, result.RealtimeItems.Count, 200);

        AddWarnings(DetailPanel, "Coverage warnings", result.CoverageWarnings);
        AddWarnings(DetailPanel, "Source read warnings", result.Warnings);
    }

    private void SetDetailMessage(string message)
    {
        DetailPanel.Children.Clear();
        AddNotice(DetailPanel, message);
    }

    private static StackPanel AddSection(
        StackPanel parent,
        string title,
        string? description = null,
        bool expanded = true)
    {
        var body = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4, 0, 4, 4)
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            AddNotice(body, description);
        }

        parent.Children.Add(new Expander
        {
            Header = title,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body
        });
        return body;
    }

    private static StackPanel AddRecord(
        StackPanel parent,
        string title,
        params (string Label, string Value)[] fields)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(4, 0, 0, 4) };
        AddFields(body, fields);
        parent.Children.Add(new Expander
        {
            Header = title,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body
        });
        return body;
    }

    private static void AddFields(StackPanel parent, params (string Label, string Value)[] fields)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 5 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var index = 0; index < fields.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = fields[index].Label,
                Opacity = 0.66,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            var value = new TextBlock
            {
                Text = fields[index].Value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(label, index);
            Grid.SetColumn(label, 0);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        parent.Children.Add(grid);
    }

    private static void AddCatalogFields(StackPanel parent, CodexThreadCatalogEntry entry)
    {
        AddFields(parent,
            ("Thread ID", Value(entry.ThreadId)),
            ("Display name", Value(entry.DisplayName)),
            ("Title", Value(entry.Title)),
            ("Name", Value(entry.Name)),
            ("Source", Value(entry.Source)),
            ("Thread source", Value(entry.ThreadSource)),
            ("Model provider", Value(entry.ModelProvider)),
            ("Model", Value(entry.Model)),
            ("Reasoning effort", Value(entry.ReasoningEffort)),
            ("Created", FormatDate(entry.CreatedAtUtc)),
            ("Updated", FormatDate(entry.UpdatedAtUtc)),
            ("Recency", FormatDate(entry.RecencyAtUtc)),
            ("Working directory", Value(entry.Cwd)),
            ("Git branch", Value(entry.GitBranch)),
            ("Git SHA", Value(entry.GitSha)),
            ("Git origin URL", Value(entry.GitOriginUrl)),
            ("Sandbox policy", Value(entry.SandboxPolicy)),
            ("Approval mode", Value(entry.ApprovalMode)),
            ("History mode", Value(entry.HistoryMode)),
            ("Memory mode", Value(entry.MemoryMode)),
            ("Archived", FormatBool(entry.Archived)),
            ("Pinned", FormatBool(entry.IsPinned)),
            ("Agent nickname", Value(entry.AgentNickname)),
            ("Agent role", Value(entry.AgentRole)),
            ("Agent path", Value(entry.AgentPath)),
            ("Project ID", Value(entry.ProjectId)),
            ("Section ID", Value(entry.SectionId)),
            ("Section position", FormatInt(entry.SectionPosition)),
            ("Source path", Value(entry.SourcePath)),
            ("Source description", Value(entry.SourceDescription)),
            ("Discovery kind", Value(entry.DiscoveryKind)),
            ("Source generation", FormatInt(entry.SourceGeneration)),
            ("Source last write", FormatDate(entry.SourceLastWriteTimeUtc)),
            ("Preview", FormatPayloadPresence(entry.Preview)),
            ("First user message", FormatPayloadPresence(entry.FirstUserMessage)));
    }

    private static void AddProjectFields(
        StackPanel parent,
        CodexThreadProject project,
        CodexThreadReadResult result)
    {
        AddFields(parent,
            ("Project ID", Value(project.ProjectId)),
            ("Name", Value(project.Name)),
            ("Metadata", Value(project.Metadata)),
            ("Position", FormatInt(project.Position)),
            ("Created", FormatDate(project.CreatedAtUtc)),
            ("Updated", FormatDate(project.UpdatedAtUtc)),
            ("Roots capability", FormatBool(project.RootsCapabilityAvailable)),
            ("Result roots capability", FormatCapability(result.ProjectRootsCapabilityAvailable)));
        if (project.OrderedRoots.Count == 0)
        {
            AddNotice(parent, "Roots: empty");
        }
        else
        {
            AddFields(parent, ("Ordered roots", string.Join(Environment.NewLine, project.OrderedRoots)));
        }
    }

    private static void AddAlternativeTools(
        StackPanel parent,
        IReadOnlyList<IReadOnlyList<CodexThreadDynamicTool>> alternatives)
    {
        foreach (var (alternative, index) in alternatives.Select((value, index) => (value, index)))
        {
            var body = AddRecord(parent, $"Dynamic-tool set alternative {index + 1}",
                ("Tool count", alternative.Count.ToString("N0")));
            foreach (var tool in alternative)
            {
                AddRecord(body, $"[{tool.Position}] {Value(tool.Name)}",
                    ("Position", tool.Position.ToString()),
                    ("Name", Value(tool.Name)),
                    ("Namespace", Value(tool.Namespace)),
                    ("Description", Value(tool.Description)),
                    ("Input schema", Value(tool.InputSchema)),
                    ("Defer loading", tool.DeferLoading ? "yes" : "no"));
            }
        }
    }

    private static void AddAlternativeEdges(
        StackPanel parent,
        IReadOnlyList<IReadOnlyList<CodexThreadSpawnEdge>> alternatives)
    {
        foreach (var (alternative, index) in alternatives.Select((value, index) => (value, index)))
        {
            var body = AddRecord(parent, $"Spawn-edge set alternative {index + 1}",
                ("Edge count", alternative.Count.ToString("N0")));
            foreach (var edge in alternative)
            {
                AddRecord(body, $"{edge.ParentThreadId} → {edge.ChildThreadId}",
                    ("Parent thread ID", edge.ParentThreadId),
                    ("Child thread ID", edge.ChildThreadId),
                    ("Status", Value(edge.Status)),
                    ("Depth", edge.Depth.ToString()));
            }
        }
    }

    private static void AddWarnings(StackPanel parent, string title, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        var section = AddSection(parent, $"{title} ({warnings.Count:N0})", expanded: false);
        foreach (var warning in warnings)
        {
            AddNotice(section, warning);
        }
    }

    private static void AddLimitNotice(StackPanel parent, int total, int shown)
    {
        if (total > shown)
        {
            AddNotice(parent, $"Showing the first {shown:N0} of {total:N0} rows in this lane. Source coverage is reported above; item payloads remain available only through deliberate local inspection.");
        }
    }

    private static void AddNotice(StackPanel parent, string message)
    {
        parent.Children.Add(new TextBlock
        {
            Text = message,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
    }

    private static string Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unavailable" : value;

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("g") ?? "unavailable";

    private static string FormatBool(bool? value) =>
        value is null ? "unavailable" : value.Value ? "yes" : "no";

    private static string FormatCapability(bool? value) =>
        value is null ? "unknown" : value.Value ? "present" : "absent";

    private static string FormatTruncation(bool? value) =>
        value is null ? "coverage unknown" : value.Value ? "truncated" : "complete";

    private static string FormatInt(int? value) =>
        value?.ToString() ?? "unavailable";

    private static string FormatLong(long? value) =>
        value?.ToString("N0") ?? "unavailable";

    private static string FormatOrdinal(long value, bool available) =>
        available ? value.ToString() : "unknown";

    private static string FormatDuration(long? value) =>
        value is long milliseconds ? $"{milliseconds:N0} ms" : "unavailable";

    private static string FormatPayloadPresence(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "absent" : "present · content not rendered here";

    private static string Summarize(string message)
    {
        var compact = message.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 260 ? compact : compact[..260] + "…";
    }
}
