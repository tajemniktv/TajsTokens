using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class ObservatoryPage : Page
{
    private readonly Dictionary<string, CodexSessionOverview> _sessionsById = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public ObservatoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await LoadAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            StatusText.Text = "Refreshing providers and local rollouts…";
            await App.Services.Telemetry.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Refresh was superseded by another request.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Refresh failed: {Summarize(exception.Message)}";
        }

        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var store = App.Services.ObservatoryStore;
            await App.Services.Repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            var sessions = await store.GetSessionOverviewsAsync(500, CancellationToken.None);
            var storage = await store.GetRolloutStorageAsync(40, CancellationToken.None);
            _sessionsById.Clear();
            foreach (var session in sessions)
            {
                _sessionsById[session.SessionId] = session;
            }

            var selectedId = (SessionList.SelectedItem as SessionRow)?.SessionId;
            var rows = sessions.Select(ToSessionRow).ToArray();
            SessionList.ItemsSource = rows;
            SessionCountText.Text = sessions.Count.ToString("N0");
            NativeTokensText.Text = FormatCount(sessions.Sum(session => session.NativeTokens.ReportedTotal));
            StorageText.Text = FormatBytes(storage.Sum(item => item.SizeBytes));
            StorageList.ItemsSource = storage.Select(item => new StorageRow(
                Path.GetFileName(item.FilePath),
                $"{FormatBytes(item.SizeBytes)} · {item.RecordsSeen:N0} records · max {FormatBytes(item.LargestRecordBytes)}")).ToArray();

            StatusText.Text = sessions.Count == 0
                ? "No normalized Codex sessions yet. The background collector will ingest discovered rollout JSONL sources without storing transcript content."
                : $"{sessions.Count:N0} normalized session(s). Last view refresh {DateTimeOffset.Now:t}.";

            if (selectedId is not null)
            {
                SessionList.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.SessionId, selectedId, StringComparison.OrdinalIgnoreCase));
            }
            if (SessionList.SelectedItem is null && rows.Length > 0)
            {
                SessionList.SelectedIndex = 0;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Observatory data unavailable: {Summarize(exception.Message)}";
        }
        finally
        {
            _loading = false;
        }
    }

    private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRow row || !_sessionsById.TryGetValue(row.SessionId, out var session))
        {
            return;
        }

        try
        {
            var store = App.Services.ObservatoryStore;
            var timelineTask = store.GetTimelineAsync(session.SessionId, 80, CancellationToken.None);
            var contextTask = store.GetContextObservationsAsync(session.SessionId, 80, CancellationToken.None);
            var agentsTask = store.GetAgentsAsync(null, CancellationToken.None);
            var relationshipsTask = store.GetAgentRelationshipsAsync(CancellationToken.None);
            await Task.WhenAll(timelineTask, contextTask, agentsTask, relationshipsTask);

            var timeline = await timelineTask;
            var context = await contextTask;
            var agents = await agentsTask;
            var relationships = await relationshipsTask;

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
        catch (Exception exception)
        {
            SelectedSessionSummary.Text = $"Session detail unavailable: {Summarize(exception.Message)}";
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
            .ToDictionary(group => group.Key, group => group.Select(item => item.ChildAgentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

        var root = selectedSessionId;
        var parent = relationships.FirstOrDefault(relation => string.Equals(relation.ChildAgentId, selectedSessionId, StringComparison.OrdinalIgnoreCase));
        if (parent is not null)
        {
            root = parent.ParentAgentId;
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
            if (depth > 16 || !visited.Add(id))
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
