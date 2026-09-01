using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class ObservatoryPage : Page
{
    private readonly Dictionary<string, CodexSessionOverview> _sessionsById = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CodexSessionOverview> _allSessions = [];
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private bool _loading;
    private bool _reloadRequested;
    private long _selectionGeneration;
    private long _detailGeneration;

    public ObservatoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        var previous = Interlocked.Exchange(ref _pageCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        App.Services.Telemetry.SnapshotUpdated -= OnSnapshotUpdated;
        App.Services.Telemetry.SnapshotUpdated += OnSnapshotUpdated;

        try
        {
            await LoadAsync(_pageCancellation.Token);
        }
        catch (OperationCanceledException) when (_pageCancellation?.IsCancellationRequested != false)
        {
            // Navigation can cancel a page-scoped load after it leaves the visual tree.
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        App.Services.Telemetry.SnapshotUpdated -= OnSnapshotUpdated;
        Interlocked.Increment(ref _selectionGeneration);
        Interlocked.Increment(ref _detailGeneration);

        var cancellation = Interlocked.Exchange(ref _pageCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        if (!_isLoaded || !snapshot.Sources.Any(source =>
                string.Equals(source.Provider, "Codex rollouts", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var cancellation = _pageCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isLoaded || cancellation.IsCancellationRequested || !ReferenceEquals(_pageCancellation, cancellation))
            {
                return;
            }

            try
            {
                await LoadAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // A queued snapshot callback may outlive navigation; detached pages do no work.
            }
        });
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (_loading)
        {
            _reloadRequested = true;
            return;
        }

        try
        {
            StatusText.Text = "Refreshing providers and local rollouts…";
            await App.Services.Telemetry.RefreshAsync(RefreshTrigger.Manual, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Refresh was superseded by another request.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Refresh failed: {Summarize(exception.Message)}";
        }

        if (_isLoaded && !cancellation.IsCancellationRequested && ReferenceEquals(_pageCancellation, cancellation))
        {
            await LoadAsync(cancellation.Token);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isLoaded)
        {
            return;
        }

        if (_loading)
        {
            _reloadRequested = true;
            return;
        }

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_isLoaded)
            {
                return;
            }

            _reloadRequested = false;
            _loading = true;
            try
            {
                var store = App.Services.ObservatoryStore;
                var repository = App.Services.Repository;

                // Microsoft.Data.Sqlite still performs synchronous native work behind async-shaped
                // calls. Keep aggregate/session reads away from the dispatcher. Detail domains are no
                // longer loaded here; the selected tab requests only the data it needs.
                var data = await Task.Run(async () =>
                {
                    await repository.InitializeAsync(cancellationToken);
                    await store.InitializeAsync(cancellationToken);

                    var summaryTask = store.GetSummaryAsync(cancellationToken);
                    var sessionsTask = store.GetSessionOverviewsAsync(750, cancellationToken);
                    await Task.WhenAll(summaryTask, sessionsTask);

                    return (
                        Summary: await summaryTask,
                        Sessions: await sessionsTask);
                }, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!_isLoaded)
                {
                    return;
                }

                var selectedId = (SessionList.SelectedItem as SessionRow)?.SessionId;
                _allSessions = data.Sessions;
                _sessionsById.Clear();
                foreach (var session in _allSessions)
                {
                    _sessionsById[session.SessionId] = session;
                }

                SessionCountText.Text = data.Summary.SessionCount.ToString("N0");
                NativeTokensText.Text = FormatCount(data.Summary.NativeTokens.ReportedTotal);
                StorageText.Text = FormatBytes(data.Summary.RolloutBytes);
                ApplySessionFilter(selectedId);

                var rolloutSource = App.Services.Telemetry.Latest.Sources.FirstOrDefault(source =>
                    string.Equals(source.Provider, "Codex rollouts", StringComparison.OrdinalIgnoreCase));
                var scanning = rolloutSource?.Detail.Contains("Scanning local Codex rollout history", StringComparison.OrdinalIgnoreCase) == true;
                StatusText.Text = scanning
                    ? data.Summary.SessionCount == 0
                        ? "Importing local Codex history in the background. Sessions will appear as batches commit."
                        : $"Importing local Codex history · {data.Summary.SessionCount:N0} session(s) available so far."
                    : data.Summary.SessionCount == 0
                        ? "No normalized Codex sessions yet. The background collector ingests discovered rollout JSONL without storing transcript content."
                        : $"{data.Summary.SessionCount:N0} normalized session(s) · refreshed {DateTimeOffset.Now:t}.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (_isLoaded)
                {
                    StatusText.Text = $"Observatory data unavailable: {Summarize(exception.Message)}";
                }
            }
            finally
            {
                _loading = false;
            }
        }
        while (_reloadRequested && _isLoaded && !cancellationToken.IsCancellationRequested);
    }

    private void OnSessionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded)
        {
            return;
        }

        var selectedId = (SessionList.SelectedItem as SessionRow)?.SessionId;
        ApplySessionFilter(selectedId);
    }

    private void ApplySessionFilter(string? preferredSelectionId)
    {
        var search = SessionSearch.Text.Trim();
        IEnumerable<CodexSessionOverview> filtered = _allSessions;
        if (search.Length > 0)
        {
            filtered = filtered.Where(session =>
                Contains(session.DisplayName, search) ||
                Contains(session.Repository, search) ||
                Contains(session.Model, search) ||
                Contains(session.Status, search) ||
                Contains(session.SessionId, search));
        }

        var rows = filtered.Select(ToSessionRow).ToArray();
        SessionList.ItemsSource = rows;

        if (preferredSelectionId is not null)
        {
            SessionList.SelectedItem = rows.FirstOrDefault(row =>
                string.Equals(row.SessionId, preferredSelectionId, StringComparison.OrdinalIgnoreCase));
        }

        if (SessionList.SelectedItem is null && rows.Length > 0)
        {
            SessionList.SelectedIndex = 0;
        }

        if (rows.Length == 0)
        {
            SelectedSessionTitle.Text = search.Length == 0 ? "No sessions" : "No matching sessions";
            SelectedSessionSummary.Text = search.Length == 0
                ? "No normalized session data is available yet."
                : "Change or clear the search filter to show more sessions.";
            ClearDetailSurfaces();
        }
    }

    private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var generation = Interlocked.Increment(ref _selectionGeneration);
        Interlocked.Increment(ref _detailGeneration);
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested ||
            SessionList.SelectedItem is not SessionRow row || !_sessionsById.TryGetValue(row.SessionId, out var session))
        {
            return;
        }

        RenderSelectedSessionSummary(session);
        ClearDetailSurfaces();

        try
        {
            await LoadSelectedTabAsync(session, generation, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Navigation cancels detached session-detail queries.
        }
    }

    private async void OnDetailTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested ||
            SessionList.SelectedItem is not SessionRow row || !_sessionsById.TryGetValue(row.SessionId, out var session))
        {
            return;
        }

        var generation = Volatile.Read(ref _selectionGeneration);
        try
        {
            await LoadSelectedTabAsync(session, generation, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Page lifecycle owns cancellation.
        }
    }

    private async Task LoadSelectedTabAsync(
        CodexSessionOverview session,
        long selectionGeneration,
        CancellationTokenSource cancellation)
    {
        var detailGeneration = Interlocked.Increment(ref _detailGeneration);
        var selectedTab = DetailTabs.SelectedIndex;

        if (selectedTab == 0)
        {
            RenderOverviewDetail(session);
            return;
        }

        if (selectedTab == 4)
        {
            RenderTokenDetail(session);
            return;
        }

        try
        {
            var store = App.Services.ObservatoryStore;
            switch (selectedTab)
            {
                case 1:
                {
                    AgentTreeList.ItemsSource = new[] { new AgentTreeRow("Loading agent topology…") };
                    var data = await Task.Run(async () =>
                    {
                        var agentsTask = store.GetAgentsAsync(null, cancellation.Token);
                        var relationshipsTask = store.GetAgentRelationshipsAsync(cancellation.Token);
                        await Task.WhenAll(agentsTask, relationshipsTask);
                        return (Agents: await agentsTask, Relationships: await relationshipsTask);
                    }, cancellation.Token);

                    if (!CanApplyDetail(session.SessionId, selectionGeneration, detailGeneration, cancellation))
                    {
                        return;
                    }
                    AgentTreeList.ItemsSource = BuildAgentTree(session.SessionId, data.Agents, data.Relationships);
                    break;
                }
                case 2:
                {
                    TimelineList.ItemsSource = new[] { new TimelineRow("Loading…", "Recent normalized events are being queried.") };
                    var timeline = await Task.Run(
                        () => store.GetTimelineAsync(session.SessionId, 200, cancellation.Token),
                        cancellation.Token);
                    if (!CanApplyDetail(session.SessionId, selectionGeneration, detailGeneration, cancellation))
                    {
                        return;
                    }
                    TimelineList.ItemsSource = timeline.Count == 0
                        ? new[] { new TimelineRow("No timeline events", "No normalized content-free events are available for this session.") }
                        : timeline.Select(item => new TimelineRow(
                            $"{item.TimestampUtc.ToLocalTime():g} · {item.EventType}",
                            item.Summary)).ToArray();
                    break;
                }
                case 3:
                {
                    ContextList.ItemsSource = new[] { new ContextRow("Loading…", "Context observations are being queried.") };
                    var context = await Task.Run(
                        () => store.GetContextObservationsAsync(session.SessionId, 240, cancellation.Token),
                        cancellation.Token);
                    if (!CanApplyDetail(session.SessionId, selectionGeneration, detailGeneration, cancellation))
                    {
                        return;
                    }
                    ContextList.ItemsSource = context.Count == 0
                        ? new[] { new ContextRow("No context observations", "No content-free context/compaction telemetry is available for this session.") }
                        : context.Select(ToContextRow).ToArray();
                    break;
                }
                case 5:
                {
                    StorageList.ItemsSource = new[] { new StorageRow("Loading…", "Querying rollout storage metadata") };
                    var storage = await Task.Run(
                        () => store.GetRolloutStorageAsync(500, cancellation.Token),
                        cancellation.Token);
                    if (!CanApplyDetail(session.SessionId, selectionGeneration, detailGeneration, cancellation))
                    {
                        return;
                    }
                    var matching = storage
                        .Where(item => string.Equals(item.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                        .Select(item => new StorageRow(
                            Path.GetFileName(item.FilePath),
                            $"{FormatBytes(item.SizeBytes)} · {item.RecordsSeen:N0} records · max {FormatBytes(item.LargestRecordBytes)}"))
                        .ToArray();
                    StorageList.ItemsSource = matching.Length == 0
                        ? new[] { new StorageRow("No rollout source", "No current storage row is associated with this session.") }
                        : matching;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (CanApplyDetail(session.SessionId, selectionGeneration, detailGeneration, cancellation))
            {
                SelectedSessionSummary.Text = $"Session detail unavailable: {Summarize(exception.Message)}";
            }
        }
    }

    private bool CanApplyDetail(
        string sessionId,
        long selectionGeneration,
        long detailGeneration,
        CancellationTokenSource cancellation) =>
        _isLoaded &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(_pageCancellation, cancellation) &&
        selectionGeneration == Volatile.Read(ref _selectionGeneration) &&
        detailGeneration == Volatile.Read(ref _detailGeneration) &&
        SessionList.SelectedItem is SessionRow current &&
        string.Equals(current.SessionId, sessionId, StringComparison.OrdinalIgnoreCase);

    private void RenderSelectedSessionSummary(CodexSessionOverview session)
    {
        SelectedSessionTitle.Text = session.DisplayName;
        var role = session.ParentSessionId is null ? "root" : "subagent";
        var peak = session.PeakContextPercent is double peakValue ? $" · peak ctx {peakValue:0.0}%" : string.Empty;
        SelectedSessionSummary.Text =
            $"{role} · {session.Status} · {session.Repository} · {FormatCount(session.NativeTokens.ReportedTotal)} native shadow tokens · " +
            $"{session.CompactionCount:N0} compaction(s){peak} · {FormatBytes(session.RolloutBytes)} normalized rollout records";
    }

    private void RenderOverviewDetail(CodexSessionOverview session)
    {
        var lastActivity = session.LastActivityAtUtc?.ToLocalTime().ToString("g") ?? "unknown";
        var model = string.IsNullOrWhiteSpace(session.Model) ? "unknown" : session.Model;
        var context = session.PeakContextPercent is double peak ? $"{peak:0.0}% peak" : "unavailable";
        OverviewDetailText.Text =
            $"Role: {(session.ParentSessionId is null ? "root" : "subagent")}\n" +
            $"State: {session.Status}\n" +
            $"Repository/workspace: {session.Repository}\n" +
            $"Model: {model}\n" +
            $"Started: {session.StartedAtUtc.ToLocalTime():g}\n" +
            $"Last activity: {lastActivity}\n" +
            $"Native shadow total: {FormatCount(session.NativeTokens.ReportedTotal)}\n" +
            $"Context: {context} · {session.CompactionCount:N0} compaction(s)\n" +
            $"Timeline events: {session.TimelineEventCount:N0}\n" +
            $"Normalized rollout bytes: {FormatBytes(session.RolloutBytes)}";
    }

    private void RenderTokenDetail(CodexSessionOverview session)
    {
        var tokens = session.NativeTokens;
        TokenDetailText.Text =
            "Native Codex accounting is shadow/reconciliation telemetry; Tokscale remains the broad default until parity is demonstrated.\n\n" +
            $"Uncached input: {FormatCount(tokens.UncachedInput)}\n" +
            $"Cache read: {FormatCount(tokens.CacheRead)}\n" +
            $"Cache write: {FormatCount(tokens.CacheWrite)}\n" +
            $"Non-reasoning output: {FormatCount(tokens.NonReasoningOutput)}\n" +
            $"Reasoning output: {FormatCount(tokens.ReasoningOutput)}\n" +
            $"Disjoint total: {FormatCount(tokens.DisjointTotal)}\n" +
            $"Provider-reported/native cumulative total: {FormatCount(tokens.ReportedTotal)}";
    }

    private void ClearDetailSurfaces()
    {
        Interlocked.Increment(ref _detailGeneration);
        OverviewDetailText.Text = "Select a session.";
        TokenDetailText.Text = "Select a session.";
        AgentTreeList.ItemsSource = null;
        TimelineList.ItemsSource = null;
        ContextList.ItemsSource = null;
        StorageList.ItemsSource = null;
    }

    private static SessionRow ToSessionRow(CodexSessionOverview session)
    {
        var role = session.ParentSessionId is null ? "root" : "subagent";
        var model = string.IsNullOrWhiteSpace(session.Model) ? "model unknown" : session.Model;
        var context = session.PeakContextPercent is double value ? $" · ctx {value:0}%" : string.Empty;
        return new SessionRow(
            session.SessionId,
            session.DisplayName,
            $"{role} · {session.Status} · {model} · {Path.GetFileName(session.Repository.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}",
            $"native {FormatCount(session.NativeTokens.ReportedTotal)} · {session.CompactionCount} compact · {FormatBytes(session.RolloutBytes)}{context}");
    }

    private static ContextRow ToContextRow(CodexContextObservation item)
    {
        var label = item.IsCompaction
            ? "compaction"
            : item.UtilizationPercent is double utilization
                ? $"context {utilization:0.0}%"
                : "context observation";
        var input = item.InputTokens is long inputTokens ? FormatCount(inputTokens) : "?";
        var window = item.ContextWindowTokens is long windowTokens ? FormatCount(windowTokens) : "?";
        var model = string.IsNullOrWhiteSpace(item.Model) ? "model unknown" : item.Model;
        return new ContextRow(
            $"{item.ObservedAtUtc.ToLocalTime():g} · {label}",
            $"{model} · input {input} / window {window}" +
            (item.RecordBytes is long bytes ? $" · record {FormatBytes(bytes)}" : string.Empty));
    }

    private static IReadOnlyList<AgentTreeRow> BuildAgentTree(
        string selectedSessionId,
        IReadOnlyList<Agent> agents,
        IReadOnlyList<AgentRelationship> relationships)
    {
        var byId = agents.ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
        var children = relationships
            .GroupBy(relation => relation.ParentAgentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.ChildAgentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var parentByChild = relationships
            .GroupBy(relation => relation.ChildAgentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ParentAgentId, StringComparer.OrdinalIgnoreCase);

        var root = selectedSessionId;
        var ancestorVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ancestorVisited.Add(root) && parentByChild.TryGetValue(root, out var parentId))
        {
            root = parentId;
        }

        var rows = new List<AgentTreeRow>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Append(root, 0);
        if (rows.Count == 0)
        {
            rows.Add(new AgentTreeRow("No linked agent topology available for this session."));
        }
        return rows;

        void Append(string id, int depth)
        {
            if (depth > 32 || !visited.Add(id))
            {
                return;
            }

            if (byId.TryGetValue(id, out var agent))
            {
                var marker = string.Equals(id, selectedSessionId, StringComparison.OrdinalIgnoreCase) ? "  ← selected" : string.Empty;
                var model = string.IsNullOrWhiteSpace(agent.Model) ? string.Empty : $" · {agent.Model}";
                rows.Add(new AgentTreeRow($"{new string(' ', depth * 3)}{(depth == 0 ? "●" : "↳")} {agent.Name} · {agent.State}{model}{marker}"));
            }
            else
            {
                rows.Add(new AgentTreeRow($"{new string(' ', depth * 3)}{(depth == 0 ? "●" : "↳")} {id[..Math.Min(8, id.Length)]} · metadata pending"));
            }

            if (children.TryGetValue(id, out var childIds))
            {
                foreach (var childId in childIds)
                {
                    Append(childId, depth + 1);
                }
            }
        }
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static string FormatCount(long value)
    {
        var absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
            >= 1_000 => $"{value / 1_000d:0.0}K",
            _ => value.ToString("N0")
        };
    }

    private static string FormatBytes(long value) => value switch
    {
        >= 1_073_741_824 => $"{value / 1_073_741_824d:0.00} GiB",
        >= 1_048_576 => $"{value / 1_048_576d:0.0} MiB",
        >= 1_024 => $"{value / 1_024d:0.0} KiB",
        _ => $"{value:N0} B"
    };

    private static string Summarize(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 260 ? value : value[..260] + "…";
    }

    private sealed record SessionRow(string SessionId, string Title, string Subtitle, string Metrics);
    private sealed record TimelineRow(string Header, string Detail);
    private sealed record ContextRow(string Header, string Detail);
    private sealed record AgentTreeRow(string Text);
    private sealed record StorageRow(string FileName, string Detail);
}
