// Taj's Tokens | CodexFleetPage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.App.Pages;

public sealed partial class CodexFleetPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _cancellation;
    private bool _loading;
    private Dictionary<string, long> _previousTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CodexRemoteFleetHostResult> _lastRemote = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _remoteErrors = new(StringComparer.OrdinalIgnoreCase);
    private bool _savedRemoteLoaded;
    private bool _savedHistoryLoaded;
    private bool _remotePersistenceWarning;

    public CodexFleetPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler<string>? ThreadRequested;

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cancellation = new CancellationTokenSource();
        _savedRemoteLoaded = false;
        _savedHistoryLoaded = false;
        _remotePersistenceWarning = false;
        _timer.Start();
        await RefreshAsync(_cancellation.Token, false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _previousTotals.Clear();
    }

    private async void OnTimerTick(object? sender, object e)
    {
        if (_cancellation is { IsCancellationRequested: false } cancellation)
            await RefreshAsync(cancellation.Token, false);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_cancellation is { IsCancellationRequested: false } cancellation)
            await RefreshAsync(cancellation.Token, false);
    }

    private async void OnRemoteRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_cancellation is { IsCancellationRequested: false } cancellation)
            await RefreshAsync(cancellation.Token, true);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken, bool checkRemote)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (checkRemote || !_savedRemoteLoaded || !_savedHistoryLoaded)
                _remotePersistenceWarning = false;
            Task<CodexLiveFleetSnapshot> fleetTask = Task.Run(
                () => App.Services.CodexThreadReadModel.ReadLiveFleetAsync(cancellationToken), cancellationToken);
            Task<IReadOnlyList<CodexSessionOverview>> sessionsTask = Task.Run(
                () => App.Services.ObservatoryReadModel.SearchSessionsAsync(string.Empty, 2_000, cancellationToken),
                cancellationToken);
            string[] remoteHosts = App.Services.Settings.RemoteFleetSshHosts;
            if (!_savedRemoteLoaded)
            {
                bool loadFailed = false;
                foreach (string host in remoteHosts)
                {
                    try
                    {
                        CodexRemoteFleetHostResult? saved = await App.Services.Repository
                            .ReadLatestRemoteFleetSnapshotAsync(host, cancellationToken);
                        if (saved is not null && (!_lastRemote.TryGetValue(host, out CodexRemoteFleetHostResult? held) ||
                            held.CollectedAtUtc is null || saved.CollectedAtUtc > held.CollectedAtUtc)) _lastRemote[host] = saved;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        _remotePersistenceWarning = true;
                        loadFailed = true;
                    }
                }
                _savedRemoteLoaded = !loadFailed;
            }
            Task<CodexRemoteFleetHostResult>[] remoteTasks = checkRemote
                ? remoteHosts.Select(host => App.Services.RemoteFleet.ReadHostAsync(host, cancellationToken)).ToArray()
                : [];
            CodexLiveFleetSnapshot fleet = await fleetTask;
            Task<IReadOnlyDictionary<string, CodexNativeTokenTotals>> recentTask = Task.Run(
                () => App.Services.ObservatoryReadModel.GetRecentSessionTokensAsync(
                    DateTimeOffset.UtcNow.AddMinutes(-15),
                    fleet.Threads.Select(thread => thread.Thread.ThreadId).ToArray(), cancellationToken), cancellationToken);
            await Task.WhenAll(new Task[] { sessionsTask, recentTask }.Concat(remoteTasks));
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLoaded) return;

            Dictionary<string, CodexSessionOverview> sessions = sessionsTask.Result
                .ToDictionary(session => session.SessionId, StringComparer.OrdinalIgnoreCase);
            IReadOnlyDictionary<string, CodexNativeTokenTotals> recent = recentTask.Result;
            var currentTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            FleetRow[] localRows = fleet.Threads.Select(thread =>
            {
                sessions.TryGetValue(thread.Thread.ThreadId, out CodexSessionOverview? session);
                recent.TryGetValue(thread.Thread.ThreadId, out CodexNativeTokenTotals? recentTokens);
                long? delta = null;
                if (session is not null)
                {
                    long total = session.NativeTokens.ReportedTotal;
                    currentTotals[thread.Thread.ThreadId] = total;
                    if (_previousTotals.TryGetValue(thread.Thread.ThreadId, out long previous) && total >= previous)
                        delta = total - previous;
                }
                string state = thread.State switch
                {
                    CodexFleetState.RecentlyWorking => "Recent in-progress evidence",
                    CodexFleetState.StaleInProgress => "In-progress record; freshness unverified",
                    CodexFleetState.ReadyForPrompt => "Last turn completed · ready for a prompt",
                    CodexFleetState.Ended => $"Last turn {thread.NativeTurnStatus}",
                    _ => "Turn state unavailable",
                };
                string usage = session is null
                    ? "Collected usage unavailable"
                    : $"Recorded last 15m {recentTokens?.ReportedTotal.ToString("N0") ?? "none"} tokens" +
                      (recentTokens is null ? string.Empty : $" · cache read {recentTokens.CacheRead:N0} · cache write {recentTokens.CacheWrite:N0}") +
                      (CodexInputComposition.From(recentTokens) is { } composition
                          ? $" · input mix {composition.CacheReadPercent:0.#}% cache read, {composition.CacheWritePercent:0.#}% cache write"
                          : string.Empty) +
                      $" · session total {session.NativeTokens.ReportedTotal:N0}" +
                      (delta is > 0 ? $" · +{delta:N0} since previous refresh" : string.Empty) +
                      (session.PeakContextPercent is { } peak ? $" · peak context {peak:0.#}%" : string.Empty);
                string role = session?.ParentSessionId is not null
                    ? "subagent"
                    : thread.Thread.AgentRole ?? "role unknown";
                string elapsed = thread.State == CodexFleetState.RecentlyWorking && thread.TurnStartedAtUtc is { } started
                    ? $" · turn began {(fleet.CapturedAtUtc - started).TotalMinutes:0.#}m ago"
                    : string.Empty;
                return new FleetRow(
                    thread.Thread.ThreadId,
                    thread.Thread.DisplayName,
                    $"{state} · {role} · {thread.Thread.Model ?? session?.Model ?? "model unknown"}{elapsed}",
                    usage,
                    $"Observed {thread.LastObservedAtUtc?.ToLocalTime().ToString("g") ?? "unknown"} · latest item {thread.LastItemType ?? "unknown"} · {thread.HistorySourcePath ?? "history unavailable"}");
            }).ToArray();
            var remoteRows = new List<FleetRow>();
            int remoteErrors = 0;
            if (checkRemote)
            {
                foreach (Task<CodexRemoteFleetHostResult> task in remoteTasks)
                {
                    CodexRemoteFleetHostResult source = task.Result;
                    if (source.Error is null)
                    {
                        _lastRemote[source.Host] = source;
                        _remoteErrors.Remove(source.Host);
                        try { await App.Services.Repository.SaveRemoteFleetSnapshotAsync(source, cancellationToken); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) when (exception is not OutOfMemoryException) { _remotePersistenceWarning = true; }
                    }
                    else _remoteErrors[source.Host] = source.Error;
                }
            }
            if (checkRemote || !_savedHistoryLoaded)
            {
                var historyLines = new List<string>();
                bool historyFailed = false;
                foreach (string host in remoteHosts)
                {
                    try
                    {
                        IReadOnlyList<CodexRemoteFleetSnapshotSummary> history = await App.Services.Repository
                            .ReadRemoteFleetHistoryAsync(host, 5, cancellationToken);
                        historyLines.AddRange(history.Select(item =>
                            $"{item.Host} · checked {item.CollectedAtUtc.ToLocalTime():g} · remote clock {item.RemoteCapturedAtUtc.ToLocalTime():g} · {item.ThreadCount:N0} thread(s)" +
                            (item.Truncated ? " · bounded" : string.Empty) +
                            (item.SourceErrors > 0 ? $" · {item.SourceErrors:N0} source warning(s)" : string.Empty)));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        _remotePersistenceWarning = true;
                        historyFailed = true;
                    }
                }
                RemoteHistoryText.Text = historyLines.Count == 0
                    ? "No saved SSH checks yet. Use Check SSH hosts to collect status metadata."
                    : string.Join(Environment.NewLine, historyLines);
                _savedHistoryLoaded = !historyFailed;
            }
            foreach (string host in remoteHosts)
            {
                _lastRemote.TryGetValue(host, out CodexRemoteFleetHostResult? shown);
                _remoteErrors.TryGetValue(host, out string? error);
                if (error is not null) remoteErrors++;
                if (shown is null)
                {
                    remoteRows.Add(new FleetRow(null, host,
                        error ?? "Not checked yet; select Check SSH hosts to read remote turn status",
                        "Remote usage not collected",
                        $"SSH host {host} · manual check only"));
                    continue;
                }
                remoteErrors += shown.SourceErrors;
                string stale = error is null ? string.Empty : $" · unavailable now: {error}";
                string checkedAt = shown.CollectedAtUtc is { } collected
                    ? $" · checked {collected.ToLocalTime():g}" : " · check time unavailable";
                bool recentlyChecked = CodexRemoteFleetFreshness.WasCheckedRecently(shown, DateTimeOffset.UtcNow);
                if (!recentlyChecked)
                    stale += " · saved status; current state unverified";
                string sourceWarning = shown.SourceErrors > 0 ? $" · {shown.SourceErrors:N0} source warning(s)" : string.Empty;
                if (shown.Threads.Count == 0)
                {
                    remoteRows.Add(new FleetRow(null, host,
                        error ?? (shown.HistoryCapabilityAvailable
                            ? "No recent Codex turns on this host" : "Codex turn history unavailable on this host"),
                        "Remote usage not collected",
                        $"SSH host {host}{checkedAt} · remote clock {shown.CapturedAtUtc.ToLocalTime():g}"));
                    continue;
                }
                foreach (CodexRemoteFleetThread thread in shown.Threads)
                {
                    CodexFleetState state = CodexRemoteFleetFreshness.Classify(shown, thread, DateTimeOffset.UtcNow);
                    string stateLabel = state switch
                    {
                        CodexFleetState.RecentlyWorking => "Recent in-progress evidence",
                        CodexFleetState.StaleInProgress => "In-progress record; freshness unverified",
                        CodexFleetState.ReadyForPrompt => recentlyChecked
                            ? "Last turn completed · ready for a prompt at check time"
                            : "Saved completed turn; current readiness unknown",
                        CodexFleetState.Ended => $"Last turn {thread.NativeStatus}",
                        _ => "Turn state unavailable",
                    };
                    remoteRows.Add(new FleetRow(null, $"{host} · {thread.ThreadId}",
                        $"{stateLabel} · native {thread.NativeStatus}{stale}{sourceWarning}",
                        "Remote usage and quota not collected or merged",
                        $"SSH host {host}{checkedAt} · observed {thread.LastObservedAtUtc?.ToLocalTime().ToString("g") ?? "unknown"} · remote clock {shown.CapturedAtUtc.ToLocalTime():g}"));
                }
            }
            FleetRow[] rows = localRows.Concat(remoteRows).ToArray();
            _previousTotals = currentTotals;
            FleetList.ItemsSource = rows;
            StatusText.Text = $"{localRows.Length:N0} local thread(s) · {remoteRows.Count:N0} remote row(s) across {remoteHosts.Length:N0} SSH host(s) · captured {fleet.CapturedAtUtc.ToLocalTime():T}" +
                              (fleet.CoverageTruncated ? " · bounded coverage" : string.Empty) +
                              (fleet.HistoryCapabilityAvailable ? string.Empty : " · turn history unavailable") +
                              (fleet.Warnings.Count > 0 ? $" · {fleet.Warnings.Count:N0} local source warning(s)" : string.Empty) +
                              (remoteErrors > 0 ? $" · {remoteErrors:N0} SSH/source warning(s)" : string.Empty) +
                              (_remotePersistenceWarning ? " · saved SSH history unavailable" : string.Empty) +
                              (_lastRemote.Values.Any(snapshot => snapshot.Truncated) ? " · remote coverage bounded" : string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded) StatusText.Text = $"Local fleet unavailable: {exception.Message}";
        }
        finally { _loading = false; }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FleetList.SelectedItem is FleetRow row)
        {
            FleetList.SelectedItem = null;
            if (row.LocalThreadId is not null) ThreadRequested?.Invoke(this, row.LocalThreadId);
        }
    }

    private sealed record FleetRow(string? LocalThreadId, string Name, string StateLine, string UsageLine, string EvidenceLine);
}
