using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class CodexSourcesPage : Page
{
    private CancellationTokenSource? _cancellation;
    private CodexNativeSourcesSnapshot? _snapshot;
    private bool _loaded;
    private bool _loading;
    private bool _reloadRequested;
    private bool _logsLoading;
    private bool _logsReloadRequested;
    private int _requestedLogsPageIndex;
    private CodexLogsSource? _logs;
    private string? _initialThreadId;
    private readonly Dictionary<CodexNativeSourceKind, string> _selectedPaths = [];
    private bool _updatingSourceSelection;
    private long _sourceGeneration;
    private long _logsGeneration;

    public CodexSourcesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initialThreadId = e.Parameter switch
        {
            CodexDataExplorerRequest request => request.ThreadId,
            string text when text.StartsWith("thread:", StringComparison.OrdinalIgnoreCase) => text[7..],
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => null
        };
    }

    private App App => (App)Application.Current;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        if (!string.IsNullOrWhiteSpace(_initialThreadId))
        {
            FilterTextBox.Text = _initialThreadId;
            LogThreadTextBox.Text = _initialThreadId;
        }
        var previous = Interlocked.Exchange(ref _cancellation, new CancellationTokenSource());
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
        _loaded = false;
        _reloadRequested = false;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
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
        await LoadLogsForPageAsync(0);
    }

    private async void OnLogPreviousClicked(object sender, RoutedEventArgs e)
    {
        var pageIndex = Math.Max(0, (_logs?.Query.PageIndex ?? 0) - 1);
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
        var selection = _snapshot?.Selections.FirstOrDefault(value => value.Kind == kind);
        _updatingSourceSelection = true;
        try
        {
            var choices = new List<SourceInstanceChoice> { new(null, "Automatic discovery preference") };
            if (selection is not null)
            {
                choices.AddRange(selection.Candidates.Select(candidate => new SourceInstanceChoice(
                    candidate.DatabasePath, $"{candidate.DiscoveryKind} · {candidate.DatabasePath}")));
            }
            _selectedPaths.TryGetValue(kind, out var selected);
            if (selected is not null && choices.All(choice => choice.Path != selected))
                choices.Add(new SourceInstanceChoice(selected, $"No longer discovered · {selected}"));
            SourceInstanceComboBox.ItemsSource = choices;
            SourceInstanceComboBox.SelectedItem = choices.First(choice => choice.Path == selected);
            SourceSelectionText.Text = selection is null ? "No source selection evidence yet." :
                $"{selection.Policy}: {selection.Rationale}\nSelected: {selection.SelectedPath ?? "none"} · {selection.Candidates.Count} discovered instance(s).";
        }
        finally { _updatingSourceSelection = false; }
    }

    private async void OnSourceInstanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSourceSelection || !_loaded || SourceList.SelectedItem is not SourceRow row ||
            SourceInstanceComboBox.SelectedItem is not SourceInstanceChoice choice || _cancellation is null) return;
        if (choice.Path is null) _selectedPaths.Remove(row.Kind);
        else _selectedPaths[row.Kind] = choice.Path;
        Interlocked.Increment(ref _sourceGeneration);
        await LoadAsync(_cancellation.Token);
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

        _loading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                var generation = Volatile.Read(ref _sourceGeneration);
                Interlocked.Increment(ref _logsGeneration);
                if (!TryBuildLogsQuery(0, out var logsQuery, out var queryError))
                {
                    // Invalid log controls must not prevent other source families from refreshing.
                    logsQuery = _logs is { } previousLogs ? previousLogs.Query with { PageIndex = 0 } : new CodexLogsQuery();
                }
                var query = new CodexNativeSourcesQuery
                {
                    SelectedPaths = new Dictionary<CodexNativeSourceKind, string>(_selectedPaths),
                    Logs = logsQuery with { DatabasePath = _selectedPaths.GetValueOrDefault(CodexNativeSourceKind.Logs) }
                };
                StatusText.Text = "Inspecting Codex source capabilities…";
                var snapshot = await Task.Run(
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
            if (_reloadRequested && _loaded && _cancellation is { IsCancellationRequested: false } current && current.Token != cancellationToken)
                await LoadAsync(current.Token);
            if (_logsReloadRequested && _loaded && !cancellationToken.IsCancellationRequested)
                await LoadLogsForPageAsync(_requestedLogsPageIndex);
        }
    }

    private async Task LoadLogsForPageAsync(int pageIndex)
    {
        var cancellation = _cancellation;
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

        if (!TryBuildLogsQuery(pageIndex, out var query, out var validationError))
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
                var generation = Interlocked.Increment(ref _logsGeneration);
                LogsQueryStatus.Text = "Reading the bounded Codex logs page…";
                var result = await Task.Run(
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
        _snapshot = snapshot;
        var filter = FilterTextBox.Text.Trim();
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
            .Select(job => $"Job {job.Kind}/{job.JobKey} · status={job.Status} · retries remaining={Known(job.RetryRemaining)} · worker={job.WorkerId ?? "(null)"}")
            .Concat(snapshot.Memory.Stage1Outputs.Select(output =>
                $"Stage1 {output.ThreadId} · source_updated_at={Known(output.SourceUpdatedAt)} · generated_at={Known(output.GeneratedAt)} · selected_for_phase2={Known(output.SelectedForPhase2)} · content: raw_memory={Known(output.HasRawMemory)}, rollout_summary={Known(output.HasRolloutSummary)}"))
            .Where(value => Matches(value, filter))
            .ToArray();

        GoalsList.ItemsSource = snapshot.Goals.Goals.Select(goal =>
            $"{goal.GoalId} · thread={goal.ThreadId} · status={goal.Status} · tokens={Known(goal.TokensUsed)} · time_seconds={Known(goal.TimeUsedSeconds)} · deferral={Known(goal.HasContinuationDeferral)} · objective present={goal.Objective is not null}")
            .Where(value => Matches(value, filter)).ToArray();

        QueueList.ItemsSource = snapshot.Queue.Items.Select(item =>
            $"{item.Id} · thread={item.ThreadId} · order={Known(item.QueueOrder)} · revision={item.Revision?.ToString() ?? "(unknown)"} · payload present={Known(item.HasPayload)}")
            .Concat(snapshot.Queue.Revisions.Select(revision => $"Revision observation · thread={revision.ThreadId} · revision={revision.Revision}"))
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

        StatusText.Text = $"Captured {snapshot.CapturedAtUtc.ToLocalTime():g}. Source rows are bounded to 250 per table. Text search filters only these loaded rows, not the complete source. Unknown means missing, null or undecodable; inspect source warnings for schema gaps.";
    }

    private void ApplyLogs(CodexLogsSource logs)
    {
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
        var optionalColumns = logs.Capabilities.AvailableOptionalColumns.Count == 0
            ? "none observed"
            : string.Join(", ", logs.Capabilities.AvailableOptionalColumns);
        LogsSourceText.Text += $" · optional columns={optionalColumns}";

        var filter = FilterTextBox.Text.Trim();
        LogsList.ItemsSource = logs.Entries
            .Select(FormatLog)
            .Where(value => Matches(value, filter))
            .ToArray();

        var total = logs.TotalMatchingRows?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";
        var page = logs.Query.PageIndex + 1;
        var pageSize = logs.Query.PageSize;
        var status = logs.Source.Availability switch
        {
            CodexNativeSourceAvailability.Unavailable => "The dedicated logs source was not discovered.",
            CodexNativeSourceAvailability.Unsupported => "The discovered logs source does not expose the supported schema.",
            CodexNativeSourceAvailability.Error => $"The dedicated logs source could not be queried: {Summarize(logs.Source.Error ?? "unknown error")}",
            CodexNativeSourceAvailability.Empty => "The dedicated logs source is supported but empty.",
            _ => $"Showing page {page} · {logs.Entries.Count:N0} row(s) of {total} matching rows"
        };
        var warnings = logs.Warnings.Count == 0
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
        var pane = key switch
        {
            "logs" => LogsPane,
            "memory" => MemoryPane,
            "goals" => GoalsPane,
            "queue" => QueuePane,
            "artifacts" => ArtifactsPane,
            "catalog" => CatalogPane,
            "summaries" => SummariesPane,
            _ => null
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

    private static IReadOnlyList<SourceRow> BuildSourceRows(CodexNativeSourcesSnapshot snapshot) =>
    [
        new("logs", "Logs", snapshot.Logs.Source.StatusText),
        new("memory", "Memory", snapshot.Memory.Source.StatusText),
        new("goals", "Goals", snapshot.Goals.Source.StatusText),
        new("queue", "Queue", snapshot.Queue.Source.StatusText),
        new("artifacts", "Artifacts", snapshot.Artifacts.Source.StatusText),
        new("catalog", "Desktop catalog", snapshot.DesktopCatalog.Source.StatusText),
        new("summaries", "Thread summaries", snapshot.ThreadSummaries.Source.StatusText)
    ];

    private bool TryBuildLogsQuery(
        int pageIndex,
        out CodexLogsQuery query,
        out string? validationError)
    {
        validationError = null;
        query = new CodexLogsQuery();
        if (!TryParseLogInstant(LogFromTextBox.Text, out var fromUtc) ||
            !TryParseLogInstant(LogToTextBox.Text, out var toUtcExclusive))
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
            Levels = LogLevelsTextBox.Text
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TargetContains = LogTargetTextBox.Text,
            ModulePathContains = LogModuleTextBox.Text,
            ThreadId = LogThreadTextBox.Text,
            ProcessUuid = LogProcessTextBox.Text,
            FromUtc = fromUtc,
            ToUtcExclusive = toUtcExclusive,
            IncludeThreadless = LogIncludeThreadlessCheckBox.IsChecked == true,
            IncludeMessages = LogIncludeMessagesCheckBox.IsChecked == true
        };
        return true;
    }

    private static bool TryParseLogInstant(string text, out DateTimeOffset? value)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            value = null;
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
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
                out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static string FormatLog(CodexLogEntry entry)
    {
        var timestamp = entry.TimestampUtc is { } timestampUtc
            ? timestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
            : $"ts={entry.TimestampUnixSeconds}.{entry.TimestampNanoseconds:D9} UTC";
        var location = entry.File is null
            ? string.Empty
            : $" · {entry.File}{(entry.Line is { } line ? $":{line}" : string.Empty)}";
        var correlation = string.Join(
            " · ",
            new[]
            {
                entry.ThreadId is null ? null : $"thread={entry.ThreadId}",
                entry.ProcessUuid is null ? null : $"process={entry.ProcessUuid}",
                entry.ModulePath is null ? null : $"module={entry.ModulePath}"
            }.Where(value => value is not null)!);
        var header = $"{timestamp} · {entry.Level} · {entry.Target} · id={entry.Id}{location}";
        if (entry.EstimatedBytes is { } estimatedBytes)
        {
            header += $" · estimated_bytes={estimatedBytes}";
        }
        var metadata = correlation.Length == 0 ? string.Empty : $"\n{correlation}";
        var body = entry.HasMessage
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
        target.Text = $"{source.StatusText} · {source.DatabasePath ?? "No matching source file"} · captured={source.CapturedAtUtc:O} · discovery={source.DiscoveryKind ?? "unknown"} · schema={source.SchemaFingerprint?[..Math.Min(12, source.SchemaFingerprint.Length)] ?? "(unknown)"} · version={source.SourceVersion ?? "(unknown)"} · corroboration={source.UpstreamCorroborationCommit ?? "(none)"}" +
            (source.Warnings.Count == 0 ? string.Empty : "\n" + string.Join("\n", source.Warnings));
    }

    private void UpdateSourceRows(CodexNativeSourcesSnapshot snapshot)
    {
        var key = (SourceList.SelectedItem as SourceRow)?.Key;
        var rows = BuildSourceRows(snapshot);
        SourceList.ItemsSource = rows;
        SourceList.SelectedItem = rows.FirstOrDefault(row => row.Key == key) ?? rows.FirstOrDefault();
    }

    private static string Known(object? value) => value is null ? "unknown" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "unknown";
    private sealed record SourceInstanceChoice(string? Path, string Label);

    private static string Summarize(string message) =>
        string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Length <= 240 ? message : message[..240] + "…";

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

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
            _ => CodexNativeSourceKind.ThreadSummaries
        };
    }
}
