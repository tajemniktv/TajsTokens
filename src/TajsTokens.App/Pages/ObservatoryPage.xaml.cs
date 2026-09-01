using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class ObservatoryPage : Page
{
    private readonly Dictionary<string, CodexSessionOverview> _sessionsById = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _pageCancellation;
    private bool _isLoaded;
    private bool _loading;
    private bool _reloadRequested;
    private long _selectionGeneration;

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

                // Microsoft.Data.Sqlite's async API still performs SQLite calls synchronously. Keep
                // aggregate/session/storage queries on the thread pool so a large telemetry database
                // cannot stall WinUI while Observatory is rendering or a scan is committing records.
                var data = await Task.Run(async () =>
                {
                    await repository.InitializeAsync(cancellationToken);
                    await store.InitializeAsync(cancellationToken);

                    var summaryTask = store.GetSummaryAsync(cancellationToken);
                    var sessionsTask = store.GetSessionOverviewsAsync(500, cancellationToken);
                    var storageTask = store.GetRolloutStorageAsync(40, cancellationToken);
                    await Task.WhenAll(summaryTask, sessionsTask, storageTask);

                    return (
                        Summary: await summaryTask,
                        Sessions: await sessionsTask,
                        Storage: await storageTask);
                }, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!_isLoaded)
                {
                    return;
                }

                var summary = data.Summary;
                var sessions = data.Sessions;
                var storage = data.Storage;
                _sessionsById.Clear();
                foreach (var session in sessions)
                {
                    _sessionsById[session.SessionId] = session;
                }

                var selectedId = (SessionList.SelectedItem as SessionRow)?.SessionId;
                var rows = sessions.Select(ToSessionRow).ToArray();
                SessionList.ItemsSource = rows;
                SessionCountText.Text = summary.SessionCount.ToString("N0");
                NativeTokensText.Text = FormatCount(summary.NativeTokens.ReportedTotal);
                StorageText.Text = FormatBytes(summary.RolloutBytes);
                StorageList.ItemsSource = storage.Select(item => new StorageRow(
                    Path.GetFileName(item.FilePath),
                    $"{FormatBytes(item.SizeBytes)} · {item.RecordsSeen:N0} records · max {FormatBytes(item.LargestRecordBytes)}")).ToArray();

                var rolloutSource = App.Services.Telemetry.Latest.Sources.FirstOrDefault(source =>
                    string.Equals(source.Provider, "Codex rollouts", StringComparison.OrdinalIgnoreCase));
                var scanning = rolloutSource?.Detail.Contains("Scanning local Codex rollout history", StringComparison.OrdinalIgnoreCase) == true;
                StatusText.Text = scanning
                    ? summary.SessionCount == 0
                        ? "Importing local Codex history in the background. Sessions will appear here as the scan commits them."
                        : $"Importing local Codex history in the background · {summary.SessionCount:N0} session(s) available so far."
                    : summary.SessionCount == 0
                        ? "No normalized Codex sessions yet. The background collector will ingest discovered rollout JSONL sources without storing transcript content."
                        : $"{summary.SessionCount:N0} normalized session(s). Last view refresh {DateTimeOffset.Now:t}.";

                if (selectedId is not null)
                {
                    SessionList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.SessionId, selectedId, StringComparison.OrdinalIgnoreCase));
                }
                if (SessionList.SelectedItem is null && rows.Length > 0)
                {
                    SessionList.SelectedIndex = 0;
                }
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

    private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var generation = Interlocked.Increment(ref _selectionGeneration);
        var cancellation = _pageCancellation;
        if (!_isLoaded || cancellation is null || cancellation.IsCancellationRequested ||
            SessionList.SelectedItem is not SessionRow row || !_sessionsById.TryGetValue(row.SessionId, out var session))
        {
            return;
        }

        try
        {
            var store = App.Services.ObservatoryStore;
            var data = await Task.Run(async () =>
            {
                var timelineTask = store.GetTimelineAsync(session.SessionId, 80, cancellation.Token);
                var contextTask = store.GetContextObservationsAsync(session.SessionId, 80, cancellation.Token);
                var agentsTask = store.GetAgentsAsync(null, cancellation.Token);
                var relationshipsTask = store.GetAgentRelationshipsAsync(cancellation.Token);
                await Task.WhenAll(timelineTask, contextTask, agentsTask, relationshipsTask);

                return (
                    Timeline: await timelineTask,
                    Context: await contextTask,
                    Agents: await agentsTask,
                    Relationships: await relationshipsTask);
            }, cancellation.Token);

            if (!_isLoaded || cancellation.IsCancellationRequested || !ReferenceEquals(_pageCancellation, cancellation) ||
                generation != Volatile.Read(ref _selectionGeneration) ||
                SessionList.SelectedItem is not SessionRow current ||
                !string.Equals(current.SessionId, row.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var timeline = data.Timeline;
            var context = data.Context;
            var agents = data.Agents;
            var relationships = data.Relationships;

            SelectedSessionTitle.Text = session.DisplayName;
            var peak = session.PeakContextPercent is double peakValue ? $" · peak context {peakValue:0.0}%" : string.Empty;
            SelectedSessionSummary.Text =
                $"{session.Status} · {session.Repository} · native shadow {FormatCount(session.NativeTokens.ReportedTotal)} tokens · " +
                $"{session.CompactionCount:N0} compaction(s){peak} · {FormatBytes(session.RolloutBytes)} normalized source records";

            AgentTreeList.ItemsSource = BuildAgentTree(session.SessionId, agents, relationships);
            TimelineList.ItemsSource = timeline.Select(item => new TimelineRow(
                $"{item.TimestampUtc.ToLocalTime():g} · {item.EventType}",
                item.Summary)).ToArray();

            if (context.Count > 0)
            {
                var latest = context[0];
                var detail = latest.IsCompaction
                    ? "latest context event is a compaction"
                    : latest.UtilizationPercent is double utilization
                        ? $"latest context utilization {utilization:0.0}%"
                        : "latest context utilization unavailable";
                SelectedSessionSummary.Text += $" · {detail}";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Navigation cancels detached session-detail queries.
        }
        catch (Exception exception)
        {
            if (_isLoaded && ReferenceEquals(_pageCancellation, cancellation) &&
                generation == Volatile.Read(ref _selectionGeneration))
            {
                SelectedSessionSummary.Text = $"Session detail unavailable: {Summarize(exception.Message)}";
            }
        }
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
    private sealed record AgentTreeRow(string Text);
    private sealed record StorageRow(string FileName, string Detail);
}
