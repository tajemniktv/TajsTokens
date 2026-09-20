// Taj's Tokens | CodexSourcesPage.xaml.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.App.Pages;

public sealed partial class CodexSourcesPage : Page
{
    private readonly Dictionary<CodexNativeSourceKind, string> _selectedPaths = [];
    private CodexAuxiliaryQuery _auxiliaryQuery = new();
    private CancellationTokenSource? _cancellation;
    private string? _initialThreadId;
    private bool _loaded;
    private bool _loading;
    private CodexLogsSource? _logs;
    private long _logsGeneration;
    private bool _logsLoading;
    private bool _logsReloadRequested;
    private string? _logsSnapshotId;
    private bool _refreshLogsSnapshot;
    private bool _reloadRequested;
    private int _requestedLogsPageIndex;
    private CodexNativeSourcesSnapshot? _snapshot;
    private long _sourceGeneration;
    private bool _updatingSourceSelection;

    public CodexSourcesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private App App => (App)Application.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initialThreadId = e.Parameter switch
        {
            CodexDataExplorerRequest request => request.ThreadId,
            string text when text.StartsWith("thread:", StringComparison.OrdinalIgnoreCase) => text[7..],
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => null,
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_initialThreadId))
        {
            FilterTextBox.Text = _initialThreadId;
            LogThreadTextBox.Text = _initialThreadId;
            AuxiliaryThreadBox.Text = _initialThreadId;
            _auxiliaryQuery = new CodexAuxiliaryQuery(0, _initialThreadId);
        }
        CancellationTokenSource? previous = Interlocked.Exchange(ref _cancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        await LoadAsync(_cancellation.Token);
        if (!string.IsNullOrWhiteSpace(_initialThreadId))
        {
            await LoadLogsForPageAsync(0);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReleaseLogsSnapshot();
        _loaded = false;
        _reloadRequested = false;
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_cancellation is { IsCancellationRequested: false } cancellation)
        {
            if (_loading)
            {
                _reloadRequested = true;
                StatusText.Text = "Refresh queued; the current inspection will finish first…";
                return;
            }

            await LoadAsync(cancellation.Token);
        }
    }

    private async void OnLogApplyClicked(object sender, RoutedEventArgs e)
    {
        _refreshLogsSnapshot = true;
        ReleaseLogsSnapshot();
        await LoadLogsForPageAsync(0);
    }

    private async void OnLogPreviousClicked(object sender, RoutedEventArgs e)
    {
        int pageIndex = Math.Max(0, (_logs?.Query.PageIndex ?? 0) - 1);
        await LoadLogsForPageAsync(pageIndex);
    }

    private async void OnLogNextClicked(object sender, RoutedEventArgs e)
    {
        if (_logs is null || !_logs.HasMoreRows)
        {
            return;
        }

        await LoadLogsForPageAsync(_logs.Query.PageIndex + 1);
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceList.SelectedItem is SourceRow row)
        {
            ShowSourcePane(row.Key);
            UpdateSourceSelection(row.Kind);
        }
    }

    private void UpdateSourceSelection(CodexNativeSourceKind kind)
    {
        CodexNativeSourceSelection? selection = _snapshot?.Selections.FirstOrDefault(value => value.Kind == kind);
        _updatingSourceSelection = true;
        try
        {
            var choices = new List<SourceInstanceChoice> { new(null, "Automatic discovery preference") };
            if (selection is not null)
            {
                choices.AddRange(
                    selection.Candidates.Select(candidate => new SourceInstanceChoice(
                        candidate.DatabasePath,
                        $"{candidate.DiscoveryKind} · {candidate.DatabasePath}")));
            }
            _selectedPaths.TryGetValue(kind, out string? selected);
            if (selected is not null && choices.All(choice => choice.Path != selected))
                choices.Add(new SourceInstanceChoice(selected, $"No longer discovered · {selected}"));
            SourceInstanceComboBox.ItemsSource = choices;
            SourceInstanceComboBox.SelectedItem = choices.First(choice => choice.Path == selected);
            SourceSelectionText.Text = selection is null
                ? "No source selection evidence yet."
                : $"{selection.Policy}: {selection.Rationale}\nSelected: {selection.SelectedPath ?? "none"} · {selection.Candidates.Count} discovered instance(s).";
        }
        finally
        {
            _updatingSourceSelection = false;
        }
    }

    private async void OnSourceInstanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSourceSelection || !_loaded || SourceList.SelectedItem is not SourceRow row ||
            SourceInstanceComboBox.SelectedItem is not SourceInstanceChoice choice || _cancellation is null) return;
        if (choice.Path is null) _selectedPaths.Remove(row.Kind);
        else _selectedPaths[row.Kind] = choice.Path;
        _auxiliaryQuery = _auxiliaryQuery with { PageIndex = 0 };
        Interlocked.Increment(ref _sourceGeneration);
        await LoadAsync(_cancellation.Token);
    }

    private async void OnAuxiliaryApplyClicked(object sender, RoutedEventArgs e)
    {
        _auxiliaryQuery = new CodexAuxiliaryQuery(
            0,
            string.IsNullOrWhiteSpace(AuxiliaryThreadBox.Text) ? null : AuxiliaryThreadBox.Text.Trim());
        Interlocked.Increment(ref _sourceGeneration);
        if (_cancellation is { } cancellation) await LoadAsync(cancellation.Token);
    }

    private async void OnAuxiliaryPreviousClicked(object sender, RoutedEventArgs e)
    {
        _auxiliaryQuery = _auxiliaryQuery with { PageIndex = Math.Max(0, _auxiliaryQuery.PageIndex - 1) };
        Interlocked.Increment(ref _sourceGeneration);
        if (_cancellation is { } cancellation) await LoadAsync(cancellation.Token);
    }

    private async void OnAuxiliaryNextClicked(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || !_snapshot.Sources.Any(x => x.Kind != CodexNativeSourceKind.Logs && x.HasMoreRows)) return;
        _auxiliaryQuery = _auxiliaryQuery with { PageIndex = _auxiliaryQuery.PageIndex + 1 };
        Interlocked.Increment(ref _sourceGeneration);
        if (_cancellation is { } cancellation) await LoadAsync(cancellation.Token);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_snapshot is not null)
        {
            Apply(_snapshot);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
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

        ReleaseLogsSnapshot();
        _loading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                long generation = Volatile.Read(ref _sourceGeneration);
                Interlocked.Increment(ref _logsGeneration);
                if (!TryBuildLogsQuery(0, out CodexLogsQuery logsQuery, out string? queryError))
                {
                    // Invalid log controls must not prevent other source families from refreshing.
                    logsQuery = _logs is { } previousLogs
                        ? previousLogs.Query with { PageIndex = 0, SnapshotId = null }
                        : new CodexLogsQuery { KeepSnapshot = true };
                }
                var query = new CodexNativeSourcesQuery
                {
                    SelectedPaths = new Dictionary<CodexNativeSourceKind, string>(_selectedPaths),
                    Auxiliary = _auxiliaryQuery,
                    Logs = logsQuery with { DatabasePath = _selectedPaths.GetValueOrDefault(CodexNativeSourceKind.Logs) },
                };
                StatusText.Text = "Inspecting Codex source capabilities…";
                CodexNativeSourcesSnapshot snapshot = await Task.Run(
                    () => App.Services.CodexNativeSources.ReadAsync(query, cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _sourceGeneration)) continue;
                Apply(snapshot);
                if (queryError is not null) LogsQueryStatus.Text = queryError + " Showing the last valid log query.";
            }
            while (_reloadRequested && _loaded && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_loaded && !cancellationToken.IsCancellationRequested)
                StatusText.Text = $"Codex source inspection unavailable: {Summarize(exception.Message)}";
        }
        finally
        {
            _loading = false;
            if (_reloadRequested && _loaded && _cancellation is { IsCancellationRequested: false } current &&
                current.Token != cancellationToken)
                await LoadAsync(current.Token);
            if (_logsReloadRequested && _loaded && !cancellationToken.IsCancellationRequested)
                await LoadLogsForPageAsync(_requestedLogsPageIndex);
        }
    }

    private async Task LoadLogsForPageAsync(int pageIndex)
    {
        CancellationTokenSource? cancellation = _cancellation;
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (_logsLoading || _loading)
        {
            _requestedLogsPageIndex = Math.Max(0, pageIndex);
            _logsReloadRequested = true;
            return;
        }

        if (!TryBuildLogsQuery(pageIndex, out CodexLogsQuery query, out string? validationError))
        {
            LogsQueryStatus.Text = validationError!;
            return;
        }

        _logsLoading = true;
        try
        {
            do
            {
                _logsReloadRequested = false;
                long generation = Interlocked.Increment(ref _logsGeneration);
                LogsQueryStatus.Text = "Reading the bounded Codex logs page…";
                CodexLogsSource result = await Task.Run(
                    () => App.Services.CodexNativeSources.ReadLogsAsync(query, cancellation.Token),
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_loaded || !ReferenceEquals(_cancellation, cancellation))
                {
                    return;
                }

                if (generation == Volatile.Read(ref _logsGeneration)) ApplyLogs(result);
                if (_logsReloadRequested)
                {
                    if (!TryBuildLogsQuery(_requestedLogsPageIndex, out query, out validationError))
                    {
                        LogsQueryStatus.Text = validationError!;
                        return;
                    }
                }
            }
            while (_logsReloadRequested && _loaded && !cancellation.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_loaded && ReferenceEquals(_cancellation, cancellation) && !cancellation.IsCancellationRequested)
            {
                LogsQueryStatus.Text = $"Codex logs query unavailable: {Summarize(exception.Message)}";
            }
        }
        finally
        {
            _logsLoading = false;
        }
    }

    private void Apply(CodexNativeSourcesSnapshot snapshot)
    {
        AuxiliaryStatusText.Text = $"Batch {snapshot.Auxiliary.PageIndex + 1} · up to 250 rows per primary table · " +
                                   (snapshot.Auxiliary.ThreadId is null ? "all threads. " : "exact thread scope. ") +
                                   "Live pages may shift as Codex changes data. Missing thread columns are unavailable, not unfiltered results. Memory jobs are omitted in thread scope. Logs use their own query.";
        AuxiliaryPreviousButton.IsEnabled = snapshot.Auxiliary.PageIndex > 0;
        AuxiliaryNextButton.IsEnabled = snapshot.Sources.Any(x => x.Kind != CodexNativeSourceKind.Logs && x.HasMoreRows);
        _snapshot = snapshot;
        string filter = FilterTextBox.Text.Trim();
        SourceStatusList.ItemsSource = snapshot.Sources.Select(source => new SourceStatusRow(
            source.DisplayName,
            source.StatusText)).ToArray();

        UpdateSourceRows(snapshot);

        ApplySource(MemorySourceText, snapshot.Memory.Source);
        ApplySource(GoalsSourceText, snapshot.Goals.Source);
        ApplySource(QueueSourceText, snapshot.Queue.Source);
        ApplySource(ArtifactsSourceText, snapshot.Artifacts.Source);
        ApplySource(CatalogSourceText, snapshot.DesktopCatalog.Source);
        ApplySource(SummariesSourceText, snapshot.ThreadSummaries.Source);

        MemoryList.ItemsSource = snapshot.Memory.Jobs
            .Select(job =>
                $"Job {job.Kind}/{job.JobKey} · status={job.Status} · retries remaining={Known(job.RetryRemaining)} · worker={job.WorkerId ?? "(null)"}")
            .Concat(
                snapshot.Memory.Stage1Outputs.Select(output =>
                    $"Stage1 {output.ThreadId} · source_updated_at={Known(output.SourceUpdatedAt)} · generated_at={Known(output.GeneratedAt)} · selected_for_phase2={Known(output.SelectedForPhase2)} · content: raw_memory={Known(output.HasRawMemory)}, rollout_summary={Known(output.HasRolloutSummary)}"))
            .Where(value => Matches(value, filter))
            .ToArray();

        GoalsList.ItemsSource = snapshot.Goals.Goals.Select(goal =>
                $"{goal.GoalId} · thread={goal.ThreadId} · status={goal.Status} · tokens={Known(goal.TokensUsed)} · time_seconds={Known(goal.TimeUsedSeconds)} · deferral={Known(goal.HasContinuationDeferral)} · objective present={goal.Objective is not null}")
            .Where(value => Matches(value, filter)).ToArray();

        QueueList.ItemsSource = snapshot.Queue.Items.Select(item =>
                $"{item.Id} · thread={item.ThreadId} · order={Known(item.QueueOrder)} · revision={item.Revision?.ToString() ?? "(unknown)"} · payload present={Known(item.HasPayload)}")
            .Concat(
                snapshot.Queue.Revisions.Select(revision =>
                    $"Revision observation · thread={revision.ThreadId} · revision={revision.Revision}"))
            .Where(value => Matches(value, filter)).ToArray();

        ArtifactsList.ItemsSource = snapshot.Artifacts.Artifacts.Select(artifact =>
                $"{artifact.Id} · thread={artifact.ThreadId} · type={artifact.ArtifactType} · identity={artifact.IdentityKey} · created_at={Known(artifact.CreatedAt)} · payload present={Known(artifact.HasPayload)}")
            .Where(value => Matches(value, filter)).ToArray();

        CatalogList.ItemsSource = snapshot.DesktopCatalog.Entries.Select(entry =>
                $"{entry.HostId}/{entry.ThreadId} · {entry.DisplayTitle} · source={entry.SourceKind} · updated={Known(entry.SourceUpdatedAt)} · missing_candidate={Known(entry.MissingCandidate)} · observation={Known(entry.ObservationSequence)}")
            .Where(value => Matches(value, filter)).ToArray();

        SummariesList.ItemsSource = snapshot.ThreadSummaries.Summaries.Select(summary =>
                $"{summary.PrincipalKey}/{summary.HostKey}/{summary.ThreadId} · revision={Known(summary.Revision)} · updated={Known(summary.UpdatedAt)} · summary present (local-only)")
            .Where(value => Matches(value, filter)).ToArray();

        ApplyLogs(snapshot.Logs);

        StatusText.Text =
            $"Captured {snapshot.CapturedAtUtc.ToLocalTime():g}. Source rows are bounded to 250 per table. Text search filters only these loaded rows, not the complete source. Unknown means missing, null or undecodable; inspect source warnings for schema gaps.";
    }

    private void ApplyLogs(CodexLogsSource logs)
    {
        _logsSnapshotId = logs.Query.SnapshotId;
        _logs = logs;
        if (_snapshot is not null && !ReferenceEquals(_snapshot.Logs, logs))
        {
            _snapshot = _snapshot with { Logs = logs };
            SourceStatusList.ItemsSource = _snapshot.Sources.Select(source => new SourceStatusRow(
                source.DisplayName,
                source.StatusText)).ToArray();
            UpdateSourceRows(_snapshot);
        }

        ApplySource(LogsSourceText, logs.Source);
        string optionalColumns = logs.Capabilities.AvailableOptionalColumns.Count == 0
            ? "none observed"
            : string.Join(", ", logs.Capabilities.AvailableOptionalColumns);
        LogsSourceText.Text += $" · optional columns={optionalColumns}";

        string filter = FilterTextBox.Text.Trim();
        LogsList.ItemsSource = logs.Entries
            .Select(FormatLog)
            .Where(value => Matches(value, filter))
            .ToArray();

        string total = logs.TotalMatchingRows?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";
        int page = logs.Query.PageIndex + 1;
        int pageSize = logs.Query.PageSize;
        string status = logs.Source.Availability switch
        {
            CodexNativeSourceAvailability.Unavailable => "The dedicated logs source was not discovered.",
            CodexNativeSourceAvailability.Unsupported => "The discovered logs source does not expose the supported schema.",
            CodexNativeSourceAvailability.Error =>
                $"The dedicated logs source could not be queried: {Summarize(logs.Source.Error ?? "unknown error")}",
            CodexNativeSourceAvailability.Empty => "The dedicated logs source is supported but empty.",
            _ => $"Showing page {page} · {logs.Entries.Count:N0} row(s) of {total} matching rows",
        };
        string warnings = logs.Warnings.Count == 0
            ? string.Empty
            : $" · {string.Join(" ", logs.Warnings)}";
        LogsQueryStatus.Text = $"{status} · page size {pageSize}{warnings}";
        LogPreviousButton.IsEnabled = logs.Query.PageIndex > 0 && logs.Source.IsInspectable;
        LogNextButton.IsEnabled = logs.HasMoreRows && logs.Source.IsInspectable;
    }

    private void ShowSourcePane(string key)
    {
        LogsPane.Visibility = Visibility.Collapsed;
        MemoryPane.Visibility = Visibility.Collapsed;
        GoalsPane.Visibility = Visibility.Collapsed;
        QueuePane.Visibility = Visibility.Collapsed;
        ArtifactsPane.Visibility = Visibility.Collapsed;
        CatalogPane.Visibility = Visibility.Collapsed;
        SummariesPane.Visibility = Visibility.Collapsed;
        SourceDetailEmptyText.Visibility = Visibility.Collapsed;
        Grid? pane = key switch
        {
            "logs" => LogsPane,
            "memory" => MemoryPane,
            "goals" => GoalsPane,
            "queue" => QueuePane,
            "artifacts" => ArtifactsPane,
            "catalog" => CatalogPane,
            "summaries" => SummariesPane,
            _ => null,
        };
        if (pane is null)
        {
            SourceDetailEmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            pane.Visibility = Visibility.Visible;
        }
    }

    private static IReadOnlyList<SourceRow> BuildSourceRows(CodexNativeSourcesSnapshot snapshot)
    {
        return
        [
            new SourceRow("logs", "Logs", snapshot.Logs.Source.StatusText),
            new SourceRow("memory", "Memory", snapshot.Memory.Source.StatusText),
            new SourceRow("goals", "Goals", snapshot.Goals.Source.StatusText),
            new SourceRow("queue", "Queue", snapshot.Queue.Source.StatusText),
            new SourceRow("artifacts", "Artifacts", snapshot.Artifacts.Source.StatusText),
            new SourceRow("catalog", "Desktop catalog", snapshot.DesktopCatalog.Source.StatusText),
            new SourceRow("summaries", "Thread summaries", snapshot.ThreadSummaries.Source.StatusText),
        ];
    }

    private bool TryBuildLogsQuery(
        int pageIndex,
        out CodexLogsQuery query,
        out string? validationError)
    {
        validationError = null;
        if (_refreshLogsSnapshot)
        {
            ReleaseLogsSnapshot();
            _refreshLogsSnapshot = false;
        }
        query = new CodexLogsQuery();
        if (!TryParseLogInstant(LogFromTextBox.Text, out DateTimeOffset? fromUtc) ||
            !TryParseLogInstant(LogToTextBox.Text, out DateTimeOffset? toUtcExclusive))
        {
            validationError = "Use an ISO-8601 UTC value or Unix seconds for the log time range.";
            return false;
        }

        if (fromUtc is not null && toUtcExclusive is not null && fromUtc >= toUtcExclusive)
        {
            validationError = "The log range is empty: the end must be later than the start.";
            return false;
        }

        query = new CodexLogsQuery
        {
            DatabasePath = _selectedPaths.GetValueOrDefault(CodexNativeSourceKind.Logs) ?? _snapshot?.Logs.Source.DatabasePath,
            PageIndex = Math.Max(0, pageIndex),
            PageSize = 100,
            KeepSnapshot = true,
            SnapshotId = _logsSnapshotId,
            Levels = LogLevelsTextBox.Text
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TargetContains = LogTargetTextBox.Text,
            ModulePathContains = LogModuleTextBox.Text,
            ThreadId = LogThreadTextBox.Text,
            ProcessUuid = LogProcessTextBox.Text,
            FromUtc = fromUtc,
            ToUtcExclusive = toUtcExclusive,
            IncludeThreadless = LogIncludeThreadlessCheckBox.IsChecked == true,
            IncludeMessages = LogIncludeMessagesCheckBox.IsChecked == true,
        };
        return true;
    }

    private void ReleaseLogsSnapshot()
    {
        string? id = _logsSnapshotId;
        _logsSnapshotId = null;
        if (id is not null) _ = App.Services.CodexNativeSources.ReleaseLogsSnapshotAsync(id);
    }

    private static bool TryParseLogInstant(string text, out DateTimeOffset? value)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            value = null;
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            try
            {
                value = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                value = null;
                return false;
            }
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static string FormatLog(CodexLogEntry entry)
    {
        string timestamp = entry.TimestampUtc is { } timestampUtc
            ? timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
            : $"ts={entry.TimestampUnixSeconds}.{entry.TimestampNanoseconds:D9} UTC";
        string location = entry.File is null
            ? string.Empty
            : $" · {entry.File}{(entry.Line is { } line ? $":{line}" : string.Empty)}";
        string correlation = string.Join(
            " · ",
            new[]
            {
                entry.ThreadId is null ? null : $"thread={entry.ThreadId}",
                entry.ProcessUuid is null ? null : $"process={entry.ProcessUuid}",
                entry.ModulePath is null ? null : $"module={entry.ModulePath}",
            }.Where(value => value is not null)!);
        string header = $"{timestamp} · {entry.Level} · {entry.Target} · id={entry.Id}{location}";
        if (entry.EstimatedBytes is { } estimatedBytes)
        {
            header += $" · estimated_bytes={estimatedBytes}";
        }
        string metadata = correlation.Length == 0 ? string.Empty : $"\n{correlation}";
        string body = entry.HasMessage
            ? entry.Message is null
                ? "\nbody present · enable 'Show log bodies' to inspect locally"
                : $"\n{PreviewLogBody(entry.Message)}"
            : "\nbody unavailable in this schema";
        return header + metadata + body;
    }

    private static string PreviewLogBody(string body)
    {
        const int maxPreviewCharacters = 4_000;
        return body.Length <= maxPreviewCharacters
            ? body
            : body[..maxPreviewCharacters] + "… [display preview truncated]";
    }

    private static void ApplySource(TextBlock target, CodexNativeSourceInfo source)
    {
        target.Text =
            $"{source.StatusText} · {source.DatabasePath ?? "No matching source file"} · captured={source.CapturedAtUtc:O} · discovery={source.DiscoveryKind ?? "unknown"} · schema={source.SchemaFingerprint?[..Math.Min(12, source.SchemaFingerprint.Length)] ?? "(unknown)"} · version={source.SourceVersion ?? "(unknown)"} · corroboration={source.UpstreamCorroborationCommit ?? "(none)"}" +
            (source.Warnings.Count == 0 ? string.Empty : "\n" + string.Join("\n", source.Warnings));
    }

    private void UpdateSourceRows(CodexNativeSourcesSnapshot snapshot)
    {
        string? key = (SourceList.SelectedItem as SourceRow)?.Key;
        IReadOnlyList<SourceRow> rows = BuildSourceRows(snapshot);
        SourceList.ItemsSource = rows;
        SourceList.SelectedItem = rows.FirstOrDefault(row => row.Key == key) ?? rows.FirstOrDefault();
    }

    private static string Known(object? value)
    {
        return value is null ? "unknown" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static string Summarize(string message)
    {
        return string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Length <= 240 ? message : message[..240] + "…";
    }

    private static bool Matches(string value, string filter)
    {
        return filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SourceInstanceChoice(string? Path, string Label);

    private sealed record SourceStatusRow(string Name, string Status);

    private sealed record SourceRow(string Key, string Name, string Status)
    {
        public CodexNativeSourceKind Kind => Key switch
        {
            "logs" => CodexNativeSourceKind.Logs,
            "memory" => CodexNativeSourceKind.Memory,
            "goals" => CodexNativeSourceKind.Goals,
            "queue" => CodexNativeSourceKind.Queue,
            "artifacts" => CodexNativeSourceKind.Artifacts,
            "catalog" => CodexNativeSourceKind.DesktopCatalog,
            _ => CodexNativeSourceKind.ThreadSummaries,
        };
    }
}