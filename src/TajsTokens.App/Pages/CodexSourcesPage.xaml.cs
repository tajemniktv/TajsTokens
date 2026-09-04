using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public CodexSourcesPage()
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
        await LoadAsync(_cancellation.Token);
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
                StatusText.Text = "Inspecting Codex source capabilities…";
                var snapshot = await Task.Run(
                    () => App.Services.CodexNativeSources.ReadAsync(cancellationToken),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Apply(snapshot);
            }
            while (_reloadRequested && _loaded && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Codex source inspection unavailable: {Summarize(exception.Message)}";
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadLogsForPageAsync(int pageIndex)
    {
        var cancellation = _cancellation;
        if (!_loaded || cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (_logsLoading)
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
                LogsQueryStatus.Text = "Reading the bounded Codex logs page…";
                var result = await Task.Run(
                    () => App.Services.CodexNativeSources.ReadLogsAsync(query, cancellation.Token),
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_loaded || !ReferenceEquals(_cancellation, cancellation))
                {
                    return;
                }

                ApplyLogs(result);
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
            if (_loaded)
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

        ApplySource(MemorySourceText, snapshot.Memory.Source);
        ApplySource(GoalsSourceText, snapshot.Goals.Source);
        ApplySource(QueueSourceText, snapshot.Queue.Source);
        ApplySource(ArtifactsSourceText, snapshot.Artifacts.Source);
        ApplySource(CatalogSourceText, snapshot.DesktopCatalog.Source);
        ApplySource(SummariesSourceText, snapshot.ThreadSummaries.Source);

        MemoryList.ItemsSource = snapshot.Memory.Jobs
            .Select(job => $"Job {job.Kind}/{job.JobKey} · status={job.Status} · retries remaining={job.RetryRemaining} · worker={job.WorkerId ?? "(null)"}")
            .Concat(snapshot.Memory.Stage1Outputs.Select(output =>
                $"Stage1 {output.ThreadId} · source_updated_at={output.SourceUpdatedAt} · generated_at={output.GeneratedAt} · selected_for_phase2={output.SelectedForPhase2} · content: raw_memory={output.HasRawMemory}, rollout_summary={output.HasRolloutSummary}"))
            .Where(value => Matches(value, filter))
            .ToArray();

        GoalsList.ItemsSource = snapshot.Goals.Goals.Select(goal =>
            $"{goal.GoalId} · thread={goal.ThreadId} · status={goal.Status} · tokens={goal.TokensUsed} · time_seconds={goal.TimeUsedSeconds} · deferral={goal.HasContinuationDeferral} · objective present={goal.Objective is not null}")
            .Where(value => Matches(value, filter)).ToArray();

        QueueList.ItemsSource = snapshot.Queue.Items.Select(item =>
            $"{item.Id} · thread={item.ThreadId} · order={item.QueueOrder} · revision={item.Revision?.ToString() ?? "(unknown)"} · payload present={item.HasPayload}")
            .Where(value => Matches(value, filter)).ToArray();

        ArtifactsList.ItemsSource = snapshot.Artifacts.Artifacts.Select(artifact =>
            $"{artifact.Id} · thread={artifact.ThreadId} · type={artifact.ArtifactType} · identity={artifact.IdentityKey} · created_at={artifact.CreatedAt} · payload present={artifact.HasPayload}")
            .Where(value => Matches(value, filter)).ToArray();

        CatalogList.ItemsSource = snapshot.DesktopCatalog.Entries.Select(entry =>
            $"{entry.HostId}/{entry.ThreadId} · {entry.DisplayTitle} · source={entry.SourceKind} · updated={entry.SourceUpdatedAt.ToString(CultureInfo.InvariantCulture)} · missing_candidate={entry.MissingCandidate} · observation={entry.ObservationSequence}")
            .Where(value => Matches(value, filter)).ToArray();

        SummariesList.ItemsSource = snapshot.ThreadSummaries.Summaries.Select(summary =>
            $"{summary.PrincipalKey}/{summary.HostKey}/{summary.ThreadId} · revision={summary.Revision} · updated={summary.UpdatedAt} · summary present (local-only)")
            .Where(value => Matches(value, filter)).ToArray();

        ApplyLogs(snapshot.Logs);

        StatusText.Text = $"Captured {snapshot.CapturedAtUtc.ToLocalTime():g}. Source rows are bounded to 250 per table; raw content remains available only through local inspection.";
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
        target.Text = $"{source.StatusText} · {source.DatabasePath ?? "No matching source file"} · schema={source.SchemaFingerprint?[..Math.Min(12, source.SchemaFingerprint.Length)] ?? "(unknown)"} · version={source.SourceVersion ?? "(unknown)"} · corroboration={source.UpstreamCorroborationCommit ?? "(none)"}";
    }

    private static string Summarize(string message) =>
        string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Length <= 240 ? message : message[..240] + "…";

    private static bool Matches(string value, string filter) =>
        filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private sealed record SourceStatusRow(string Name, string Status);
}
