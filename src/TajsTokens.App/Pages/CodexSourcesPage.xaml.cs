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
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_cancellation is { IsCancellationRequested: false } cancellation)
        {
            await LoadAsync(cancellation.Token);
        }
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
        if (!_loaded || _loading)
        {
            return;
        }

        _loading = true;
        try
        {
            StatusText.Text = "Inspecting Codex source capabilities…";
            var snapshot = await Task.Run(
                () => App.Services.CodexNativeSources.ReadAsync(cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Apply(snapshot);
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

        StatusText.Text = $"Captured {snapshot.CapturedAtUtc.ToLocalTime():g}. Source rows are bounded to 250 per table; raw content remains available only through local inspection.";
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
