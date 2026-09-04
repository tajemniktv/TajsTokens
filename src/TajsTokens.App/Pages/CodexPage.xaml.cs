using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.App.Pages;

public sealed partial class CodexPage : Page
{
    private CancellationTokenSource? _cancellation;
    private bool _loaded;
    private bool _loading;
    private bool _reloadRequested;
    private bool _suppressTreeSelection;
    private long _loadGeneration;
    private long _selectionGeneration;
    private CodexThreadNavigationResult? _navigation;
    private IReadOnlyDictionary<string, CodexSessionOverview> _sessions =
        new Dictionary<string, CodexSessionOverview>(StringComparer.OrdinalIgnoreCase);
    private IntelligenceDashboard? _dashboard;
    private CodexThreadReadResult? _selectedThread;
    private IReadOnlyList<CodexThreadItemPresentation> _allPresentations = Array.Empty<CodexThreadItemPresentation>();
    private bool _showExecutionDetails;
    private bool _isNarrow;
    private ListView? _conversationList;
    private readonly Dictionary<string, TreeViewNode> _threadNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TreeViewNode, TreeViewNode?> _treeParents = [];

    public CodexPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ReplaceCancellation();
        await LoadAsync(_cancellation!.Token, Interlocked.Increment(ref _loadGeneration));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _reloadRequested = false;
        Interlocked.Increment(ref _selectionGeneration);
        Interlocked.Increment(ref _loadGeneration);
        ReplaceCancellation(cancelOnly: true);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_loaded && _cancellation is { IsCancellationRequested: false } cancellation)
        {
            await LoadAsync(cancellation.Token, Interlocked.Increment(ref _loadGeneration));
        }
    }

    private async void OnArchivedChanged(object sender, RoutedEventArgs e)
    {
        if (_loaded && _cancellation is { IsCancellationRequested: false } cancellation)
        {
            await LoadAsync(cancellation.Token, Interlocked.Increment(ref _loadGeneration));
        }
    }

    private async void OnUsageControlsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && _cancellation is { IsCancellationRequested: false } cancellation)
        {
            await LoadAsync(cancellation.Token, Interlocked.Increment(ref _loadGeneration));
        }
    }

    private void OnOpenNavigationClicked(object sender, RoutedEventArgs e)
    {
        BrowserSplitView.IsPaneOpen = true;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 900;
        if (narrow == _isNarrow && BrowserSplitView.DisplayMode == (narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline))
        {
            return;
        }

        _isNarrow = narrow;
        BrowserSplitView.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        OpenNavigationButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        BrowserSplitView.IsPaneOpen = !narrow;
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _cancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        try
        {
            await Task.Delay(180, cancellation.Token);
            if (generation != Volatile.Read(ref _loadGeneration)) return;
            await LoadAsync(cancellation.Token, generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken, long generation)
    {
        if (!_loaded) return;
        if (_loading)
        {
            _reloadRequested = true;
            return;
        }

        _loading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                // A queued search/range refresh must adopt the newest generation. Keeping the
                // original generation here would make the in-flight pass perpetually stale.
                generation = Volatile.Read(ref _loadGeneration);
                var query = new CodexThreadNavigationQuery(
                    SearchTextBox.Text.Trim(),
                    Take: 5_000,
                    IncludeArchived: ArchivedCheckBox.IsChecked == true);
                StatusText.Text = "Reading Codex workspaces, threads and usage…";
                var now = DateTimeOffset.UtcNow;
                var usageQuery = new IntelligenceQuery(
                    now.AddDays(-ParseRangeDays()),
                    now,
                    ParseBucket(),
                    720);
                var navigationTask = Task.Run(
                    () => App.Services.CodexThreadReadModel.BrowseThreadsAsync(query, cancellationToken),
                    cancellationToken);
                var sessionsTask = Task.Run(
                    () => App.Services.ObservatoryReadModel.SearchSessionsAsync(string.Empty, 10_000, cancellationToken),
                    cancellationToken);
                var intelligenceTask = Task.Run(
                    () => App.Services.Intelligence.QueryAsync(
                        usageQuery,
                        cancellationToken),
                    cancellationToken);
                await Task.WhenAll(navigationTask, sessionsTask, intelligenceTask);
                cancellationToken.ThrowIfCancellationRequested();
                if (!_loaded || generation != Volatile.Read(ref _loadGeneration))
                {
                    _reloadRequested = true;
                    continue;
                }

                _navigation = navigationTask.Result;
                _sessions = sessionsTask.Result.ToDictionary(
                    session => session.SessionId,
                    StringComparer.OrdinalIgnoreCase);
                _dashboard = intelligenceTask.Result;
                BuildNavigationTree(_navigation);
                RenderOverview(_dashboard, _navigation);
                var warningCount = _navigation.Warnings.Count + _navigation.CoverageWarnings.Count;
                StatusText.Text =
                    $"{_navigation.TotalThreadCount:N0} visible thread(s) · {_navigation.Groups.Count:N0} workspace/project group(s) · " +
                    $"usage through {now.ToLocalTime():g}" +
                    (warningCount == 0 ? "." : $" · {warningCount:N0} source/coverage warning(s).");
            }
            while (_reloadRequested && _loaded && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_loaded && generation == Volatile.Read(ref _loadGeneration))
            {
                StatusText.Text = $"Codex overview unavailable: {Summarize(exception.Message)}";
                SetDetailMessage("Codex could not be read. Refresh after checking the source status in Codex Data Explorer.");
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildNavigationTree(CodexThreadNavigationResult navigation)
    {
        _suppressTreeSelection = true;
        try
        {
            NavigationTree.RootNodes.Clear();
            _threadNodes.Clear();
            _treeParents.Clear();
            var allItem = new NavigationTreeItem("All Codex", "Usage, activity and every readable thread", NavigationTreeKind.All);
            var allNode = new TreeViewNode { Content = allItem, IsExpanded = true };
            _treeParents[allNode] = null;
            foreach (var group in navigation.Groups)
            {
                var groupItem = new NavigationTreeItem(
                    group.DisplayName,
                    $"{group.ThreadCount:N0} thread(s) · {FormatDate(group.LatestActivityAtUtc)}",
                    NavigationTreeKind.Group,
                    Group: group);
                var groupNode = new TreeViewNode { Content = groupItem, IsExpanded = group == navigation.Groups.FirstOrDefault() };
                allNode.Children.Add(groupNode);
                _treeParents[groupNode] = allNode;
                foreach (var root in group.RootThreads)
                {
                    AddThreadNode(root, groupNode, isChild: false);
                }
            }
            NavigationTree.RootNodes.Add(allNode);
            NavigationTree.SelectedNode = allNode;
        }
        finally
        {
            _suppressTreeSelection = false;
        }
    }

    private void AddThreadNode(CodexThreadNavigationNode thread, TreeViewNode parent, bool isChild)
    {
        var entry = thread.Thread.Preferred;
        var role = isChild ? "Subagent" : "Root thread";
        var subtitle = string.Join(
            " · ",
            new[]
            {
                role,
                entry.AgentNickname,
                entry.Model,
                FormatDate(entry.RecencyAtUtc ?? entry.UpdatedAtUtc ?? entry.CreatedAtUtc)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (thread.MissingParent) subtitle += " · parent unavailable";
        if (thread.CycleDetected) subtitle += " · cycle truncated";
        var item = new NavigationTreeItem(entry.DisplayName, subtitle, NavigationTreeKind.Thread, Thread: thread);
        var node = new TreeViewNode { Content = item, IsExpanded = false };
        parent.Children.Add(node);
        _treeParents[node] = parent;
        // A cycle-safe projection may contain a terminal duplicate of an already-seen ID. Keep
        // the first (real) node as the navigation target for linked-thread actions.
        _threadNodes.TryAdd(entry.ThreadId, node);
        foreach (var child in thread.Children)
        {
            AddThreadNode(child, node, isChild: true);
        }
    }

    private async void OnNavigationSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_suppressTreeSelection || args.AddedItems.Count == 0) return;
        if (args.AddedItems[0] is not TreeViewNode { Content: NavigationTreeItem item }) return;
        switch (item.Kind)
        {
            case NavigationTreeKind.All:
                RenderOverview(_dashboard, _navigation);
                if (_isNarrow) BrowserSplitView.IsPaneOpen = false;
                return;
            case NavigationTreeKind.Group when item.Group is not null:
                RenderWorkspace(item.Group);
                if (_isNarrow) BrowserSplitView.IsPaneOpen = false;
                return;
            case NavigationTreeKind.Thread when item.Thread is not null:
                if (_isNarrow) BrowserSplitView.IsPaneOpen = false;
                await LoadThreadAsync(item.Thread.ThreadId);
                return;
        }
    }

    private async Task LoadThreadAsync(string threadId)
    {
        var cancellation = _cancellation;
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested) return;
        var generation = Interlocked.Increment(ref _selectionGeneration);
        SetDetailMessage("Loading the selected Codex conversation…");
        try
        {
            var result = await Task.Run(
                () => App.Services.CodexThreadReadModel.ReadThreadAsync(threadId, cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_loaded || !ReferenceEquals(_cancellation, cancellation) || generation != Volatile.Read(ref _selectionGeneration)) return;
            _selectedThread = result;
            RenderThread(result);
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

    private void RenderOverview(IntelligenceDashboard? dashboard, CodexThreadNavigationResult? navigation)
    {
        DetailPanel.Children.Clear();
        AddHeading(DetailPanel, "All Codex", "Whole readable local Codex history. Select a project, workspace, or thread on the left for scoped detail.");
        if (dashboard is null || navigation is null)
        {
            AddNotice(DetailPanel, "Loading Codex usage and navigation…");
            return;
        }

        var total = dashboard.UsageHistory.Sum(bucket => bucket.NativeTokens);
        var root = dashboard.UsageHistory.Sum(bucket => bucket.RootTokens);
        var subagent = dashboard.UsageHistory.Sum(bucket => bucket.SubagentTokens);
        var cards = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < 4; i++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fiveHourDelta = dashboard.UsageHistory.Sum(bucket => bucket.FiveHourQuotaDelta ?? 0);
        var weeklyDelta = dashboard.UsageHistory.Sum(bucket => bucket.WeeklyQuotaDelta ?? 0);
        AddMetric(cards, 0, "Native tokens · all Codex", FormatCount(total));
        AddMetric(cards, 1, "Root / subagent · all Codex", $"{FormatCount(root)} / {FormatCount(subagent)}");
        AddMetric(cards, 2, "5h meter movement · all Codex", FormatQuotaDelta(fiveHourDelta));
        AddMetric(cards, 3, "Weekly movement · all Codex", FormatQuotaDelta(weeklyDelta));
        DetailPanel.Children.Add(cards);
        AddNotice(DetailPanel,
            $"Usage range: {dashboard.Query.FromUtc.ToLocalTime():g} → {dashboard.Query.ToUtc.ToLocalTime():g} · " +
            $"timeline bucket: {dashboard.Query.BucketSize.ToString().ToLowerInvariant()}. " +
            "These totals are intentionally database-wide; the range changes lookback and the bucket changes timeline resolution. " +
            (dashboard.UsageHistory.Sum(bucket => bucket.IntegrityDelta) == 0
                ? "Reported/disjoint token integrity is exact for this range."
                : "Reported/disjoint token totals include an observed integrity difference."));
        AddFields(DetailPanel,
            ("Thread catalog capability", navigation.CatalogCapabilityAvailable ? "present" : "absent or unavailable"),
            ("Spawn topology capability", navigation.SpawnEdgesCapabilityAvailable ? "present" : "absent or unavailable"),
            ("Visible thread coverage", $"{navigation.TotalThreadCount:N0} thread(s) in the bounded navigation result"));

        AddListSection(DetailPanel, "Usage timeline", dashboard.UsageHistory.Count == 0
            ? ["No native Codex token events are available for this range yet."]
            : dashboard.UsageHistory.Select(bucket =>
                $"{FormatBucket(bucket.StartUtc, dashboard.Query.BucketSize)}  ·  {FormatCount(bucket.NativeTokens)} tokens  ·  " +
                $"root {FormatCount(bucket.RootTokens)} · subagents {FormatCount(bucket.SubagentTokens)} · {bucket.ActiveSessions:N0} active session(s) · compactions {bucket.Compactions:N0}" +
                (bucket.FiveHourQuotaDelta is double five ? $" · 5h +{five:0.#}pp" : string.Empty) +
                (bucket.WeeklyQuotaDelta is double week ? $" · weekly +{week:0.#}pp" : string.Empty)).ToArray());
        AddListSection(DetailPanel, "Breakdowns", dashboard.Dimensions.Count == 0
            ? ["No breakdowns are available for this range."]
            : dashboard.Dimensions.Select(item =>
                $"{item.Dimension}: {item.Value}  ·  {FormatCount(item.NativeTokens)} tokens · {item.Sessions:N0} session(s)").ToArray());
        AddListSection(DetailPanel, "Active workspaces", navigation.Groups.Select(group =>
            $"{group.DisplayName}  ·  {group.ThreadCount:N0} thread(s) · latest {FormatDate(group.LatestActivityAtUtc)}").ToArray());
        AddListSection(DetailPanel, "Hour heatmap", dashboard.Heatmap.Count == 0
            ? ["No native Codex activity is available for the heatmap range."]
            : dashboard.Heatmap
                .OrderBy(cell => ((int)cell.Day + 6) % 7)
                .ThenBy(cell => cell.Hour)
                .Select(cell =>
                    $"{cell.Day} {cell.Hour:00}:00 UTC  ·  {FormatCount(cell.NativeTokens)} tokens · {cell.ActiveBuckets:N0} active bucket(s)")
                .ToArray());
        AddListSection(DetailPanel, "Quota movement", dashboard.QuotaBurnIntervals.Count == 0
            ? ["No quota-burn intervals were observed for this range."]
            : dashboard.QuotaBurnIntervals.Select(interval =>
                $"{interval.Kind} · {interval.StartUtc.ToLocalTime():g} → {interval.EndUtc.ToLocalTime():g} · {FormatQuotaDelta(interval.DeltaUsedPercent)} · {FormatCount(interval.NativeTokens)} tokens").ToArray());
        AddWarnings(DetailPanel, "Navigation coverage", navigation.CoverageWarnings);
        AddWarnings(DetailPanel, "Source warnings", navigation.Warnings);
    }

    private void RenderWorkspace(CodexThreadNavigationGroup group)
    {
        DetailPanel.Children.Clear();
        AddHeading(DetailPanel, group.DisplayName, "Scoped to this project/workspace. Select a thread to read its conversation and source-native evidence.");
        var threads = Flatten(group.RootThreads).ToArray();
        var sessions = threads.Select(node => _sessions.TryGetValue(node.ThreadId, out var session) ? session : null).Where(session => session is not null).Cast<CodexSessionOverview>().ToArray();
        var cards = new Grid { ColumnSpacing = 10 };
        for (var i = 0; i < 4; i++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMetric(cards, 0, "Threads", threads.Length.ToString("N0", CultureInfo.InvariantCulture));
        AddMetric(cards, 1, "Root / subagent", $"{group.RootThreads.Count:N0} / {threads.Length - group.RootThreads.Count:N0}");
        AddMetric(cards, 2, "Observed tokens", FormatCount(sessions.Sum(session => session.NativeTokens.ReportedTotal)));
        AddMetric(cards, 3, "Telemetry available", $"{sessions.Length:N0} of {threads.Length:N0}");
        DetailPanel.Children.Add(cards);
        AddFields(DetailPanel,
            ("Latest activity", FormatDate(group.LatestActivityAtUtc)),
            ("Workspace path", group.WorkspacePath ?? "not applicable"),
            ("Project ID", group.ProjectId ?? "not applicable"),
            ("Models observed", string.Join(", ", sessions.Select(session => session.Model).Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(model => model)))
        );
        AddListSection(DetailPanel, "Thread families", group.RootThreads.Select(node =>
            $"{node.Thread.Preferred.DisplayName}  ·  {CountNodes(node):N0} thread(s) · {node.Thread.Preferred.Model ?? "model unavailable"}").ToArray());
    }

    private void RenderThread(CodexThreadReadResult result)
    {
        DetailPanel.Children.Clear();
        _showExecutionDetails = false;
        var thread = result.Thread;
        var session = thread is not null && _sessions.TryGetValue(thread.ThreadId, out var selectedSession) ? selectedSession : null;
        AddHeading(DetailPanel, thread?.DisplayName ?? "Codex thread", "Conversation-first local inspection; execution details are collapsed by default.");
        var facts = new List<(string Label, string Value)>
        {
            ("Thread ID", thread?.ThreadId ?? "unavailable"),
            ("Role", IsSubagent(thread?.ThreadId) ? "Subagent" : "Root thread"),
            ("Model", thread?.Model ?? session?.Model ?? "unavailable"),
            ("Status", session?.Status ?? "source status unavailable"),
            ("Recency", FormatDate(thread?.RecencyAtUtc ?? thread?.UpdatedAtUtc ?? session?.LastActivityAtUtc)),
            ("Workspace", thread?.Cwd ?? session?.Repository ?? "unavailable"),
            ("Git branch", thread?.GitBranch ?? "unavailable"),
            ("Native tokens", session is null ? "unavailable" : FormatCount(session.NativeTokens.ReportedTotal)),
            ("Peak context", session?.PeakContextPercent is double peak ? $"{peak:0.#}%" : "unavailable"),
            ("History source", result.HistorySourceDescription ?? result.HistorySourcePath ?? "unavailable"),
            ("Source freshness", FormatDate(thread?.SourceLastWriteTimeUtc ?? result.CapturedAtUtc))
        };
        AddFields(DetailPanel, facts.ToArray());

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var messagesOnly = new CheckBox { Content = "Show execution details", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
        messagesOnly.Checked += OnExecutionDetailsChanged;
        messagesOnly.Unchecked += OnExecutionDetailsChanged;
        toolbar.Children.Add(messagesOnly);
        toolbar.Children.Add(new Button { Content = "Jump to start", Tag = "start" });
        var startButton = (Button)toolbar.Children[^1];
        startButton.Click += OnJumpClicked;
        toolbar.Children.Add(new Button { Content = "Jump to latest", Tag = "latest" });
        var latestButton = (Button)toolbar.Children[^1];
        latestButton.Click += OnJumpClicked;
        var inspect = new Button { Content = "Inspect source data", Tag = thread?.ThreadId };
        inspect.Click += OnInspectSourceClicked;
        toolbar.Children.Add(inspect);
        DetailPanel.Children.Add(toolbar);

        var preferredSource = result.HistorySources.FirstOrDefault(source =>
            string.Equals(source.SourcePath, result.HistorySourcePath, StringComparison.OrdinalIgnoreCase)) ?? result.HistorySources.FirstOrDefault();
        var items = preferredSource?.Items ?? result.Items;
        _allPresentations = CodexThreadItemPresenter.PresentMany(items.OrderBy(item => item.RolloutOrdinal));
        _conversationList = new ListView
        {
            ItemTemplate = (DataTemplate)Resources["ConversationItemTemplate"],
            SelectionMode = ListViewSelectionMode.None,
            Height = 680,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        DetailPanel.Children.Add(new TextBlock { Text = $"Conversation · {_allPresentations.Count:N0} item(s)", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        DetailPanel.Children.Add(_conversationList);
        ApplyConversationItems(scrollToLatest: true);

        var evidence = new StackPanel { Spacing = 6 };
        AddFields(evidence,
            ("State source", result.StateSourcePath ?? "unavailable"),
            ("History sources retained", result.HistorySources.Count.ToString("N0", CultureInfo.InvariantCulture)),
            ("History reconciliation", result.HistoryReconciliationPolicy),
            ("History selection rationale", result.HistorySourceSelectionRationale ?? "unavailable"),
            ("Turns", result.Turns.Count.ToString("N0", CultureInfo.InvariantCulture)),
            ("Realtime items", result.RealtimeItems.Count.ToString("N0", CultureInfo.InvariantCulture)),
            ("Spawn edges", result.SpawnEdges.Count.ToString("N0", CultureInfo.InvariantCulture)),
            ("Dynamic tools", result.DynamicTools.Count.ToString("N0", CultureInfo.InvariantCulture)));
        foreach (var source in result.HistorySources)
        {
            evidence.Children.Add(new TextBlock
            {
                Text = $"{source.SourceDescription} · turns={source.Turns.Count:N0} · items={source.Items.Count:N0} · realtime={source.RealtimeItems.Count:N0}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }
        AddWarnings(evidence, "Coverage warnings", result.CoverageWarnings);
        AddWarnings(evidence, "Source read warnings", result.Warnings);
        var evidenceExpander = new Expander { Header = "Evidence and details", Content = evidence, IsExpanded = false };
        DetailPanel.Children.Add(evidenceExpander);
    }

    private void OnExecutionDetailsChanged(object sender, RoutedEventArgs e)
    {
        _showExecutionDetails = sender is CheckBox { IsChecked: true };
        ApplyConversationItems();
    }

    private void ApplyConversationItems(bool scrollToLatest = false)
    {
        if (_conversationList is null) return;
        var items = _showExecutionDetails
            ? _allPresentations
            : _allPresentations.Where(item => item.Kind is CodexThreadItemPresentationKind.Message or CodexThreadItemPresentationKind.Plan).ToArray();
        _conversationList.ItemsSource = items;
        if (scrollToLatest && items.Count > 0)
        {
            DispatcherQueue.TryEnqueue(() => _conversationList?.ScrollIntoView(items[^1]));
        }
    }

    private void OnJumpClicked(object sender, RoutedEventArgs e)
    {
        if (_conversationList?.Items.Count is not > 0 || sender is not Button button) return;
        var item = string.Equals(button.Tag?.ToString(), "start", StringComparison.Ordinal)
            ? _conversationList.Items[0]
            : _conversationList.Items[^1];
        _conversationList.ScrollIntoView(item);
    }

    private void OnLinkedThreadClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string threadId } || string.IsNullOrWhiteSpace(threadId) || !_threadNodes.TryGetValue(threadId, out var node)) return;
        var parents = new Stack<TreeViewNode>();
        for (TreeViewNode? current = node; current is not null; _treeParents.TryGetValue(current, out current)) parents.Push(current);
        foreach (var parent in parents) parent.IsExpanded = true;
        NavigationTree.SelectedNode = node;
    }

    private void OnInspectSourceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string threadId } && !string.IsNullOrWhiteSpace(threadId))
        {
            Frame?.Navigate(typeof(CodexDataExplorerPage), new CodexDataExplorerRequest(threadId));
        }
    }

    private bool IsSubagent(string? threadId) =>
        threadId is not null && _navigation?.PreferredSpawnEdges.Any(edge =>
            string.Equals(edge.ChildThreadId, threadId, StringComparison.OrdinalIgnoreCase)) == true;

    private void SetDetailMessage(string message)
    {
        DetailPanel.Children.Clear();
        AddNotice(DetailPanel, message);
    }

    private void ReplaceCancellation(bool cancelOnly = false)
    {
        var previous = Interlocked.Exchange(ref _cancellation, cancelOnly ? null : new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
    }

    private static IEnumerable<CodexThreadNavigationNode> Flatten(IEnumerable<CodexThreadNavigationNode> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<CodexThreadNavigationNode>(roots.Reverse());
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node.ThreadId)) continue;
            yield return node;
            foreach (var child in node.Children.Reverse()) pending.Push(child);
        }
    }

    private static int CountNodes(CodexThreadNavigationNode node) => Flatten(new[] { node }).Count();

    private static void AddHeading(Panel parent, string title, string subtitle)
    {
        parent.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        parent.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
    }

    private static void AddMetric(Grid parent, int column, string title, string value)
    {
        var border = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = CardBrush() };
        border.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = value, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }
            }
        };
        Grid.SetColumn(border, column);
        parent.Children.Add(border);
    }

    private static void AddFields(Panel parent, params (string Label, string Value)[] fields)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 5 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var field in fields.Where(field => !string.IsNullOrWhiteSpace(field.Label)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = field.Label, Opacity = 0.62, TextWrapping = TextWrapping.Wrap };
            var value = new TextBlock { Text = string.IsNullOrWhiteSpace(field.Value) ? "unavailable" : field.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, row); Grid.SetColumn(label, 0);
            Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            grid.Children.Add(label); grid.Children.Add(value);
            row++;
        }
        parent.Children.Add(grid);
    }

    private static void AddListSection(Panel parent, string title, IReadOnlyList<string> values)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 260, ItemsSource = values };
        parent.Children.Add(new Expander { Header = title, Content = list, IsExpanded = true });
    }

    private static void AddWarnings(Panel parent, string title, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        AddListSection(parent, $"{title} ({warnings.Count:N0})", warnings);
    }

    private static void AddNotice(Panel parent, string message)
    {
        parent.Children.Add(new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush?)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.76 }
        });
    }

    private static Brush? CardBrush() => Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;

    private static string FormatDate(DateTimeOffset? value) => value is null ? "unavailable" : value.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string FormatBucket(DateTimeOffset value, AnalyticsBucketSize size) => size switch
    {
        AnalyticsBucketSize.Minute => value.ToLocalTime().ToString("ddd HH:mm", CultureInfo.CurrentCulture),
        AnalyticsBucketSize.Hour => value.ToLocalTime().ToString("ddd HH:00", CultureInfo.CurrentCulture),
        _ => value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
    };

    private int ParseRangeDays()
    {
        if (RangeCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return Math.Clamp(days, 1, 3650);
        }

        return 7;
    }

    private AnalyticsBucketSize ParseBucket()
    {
        if (BucketCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<AnalyticsBucketSize>(item.Tag?.ToString(), out var bucket))
        {
            return bucket;
        }

        return AnalyticsBucketSize.Hour;
    }

    private static string FormatCount(long value)
    {
        var absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
            >= 1_000 => $"{value / 1_000d:0.0}K",
            _ => value.ToString("N0", CultureInfo.CurrentCulture)
        };
    }

    private static string FormatQuotaDelta(double value) =>
        Math.Abs(value) < 0.05 ? "No observed movement" : $"{value:+0.#;-0.#;0.#} pp";

    private static string Summarize(string message)
    {
        var compact = message.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 260 ? compact : compact[..260] + "…";
    }

    public enum NavigationTreeKind { All, Group, Thread }

    public sealed class NavigationTreeItem(
        string displayName,
        string subtitle,
        NavigationTreeKind kind,
        CodexThreadNavigationGroup? Group = null,
        CodexThreadNavigationNode? Thread = null)
    {
        public string DisplayName { get; } = displayName;
        public string Subtitle { get; } = subtitle;
        public NavigationTreeKind Kind { get; } = kind;
        public CodexThreadNavigationGroup? Group { get; } = Group;
        public CodexThreadNavigationNode? Thread { get; } = Thread;
    }
}

public sealed record CodexDataExplorerRequest(string? ThreadId);
