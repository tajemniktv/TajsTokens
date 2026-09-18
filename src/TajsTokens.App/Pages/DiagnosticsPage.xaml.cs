using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private bool _loaded;
    private CancellationTokenSource? _refreshCancellation;
    private App App => (App)Application.Current;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        App.Services.Telemetry.SnapshotUpdated += OnSnapshotUpdated;
        Render(App.Services.Telemetry.Latest);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        App.Services.Telemetry.SnapshotUpdated -= OnSnapshotUpdated;
        _refreshCancellation?.Cancel();
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot) => DispatcherQueue.TryEnqueue(() =>
    {
        // Render the latest published generation, not a queued older event.
        if (_loaded) Render(App.Services.Telemetry.Latest);
    });

    private void Render(TelemetrySnapshot snapshot)
    {
        var diagnostics = TelemetryDiagnosticsPresenter.Present(snapshot);
        StatusText.Text = diagnostics.Summary;
        SourceItems.ItemsSource = diagnostics.Sources;
        QuotaItems.ItemsSource = diagnostics.QuotaWindows;
        EmptySourcesText.Visibility = diagnostics.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EventsText.Text = snapshot.Events.Count == 0 ? "No refresh events in this snapshot." :
            string.Join("\n\n", snapshot.Events.OrderByDescending(x => x.TimestampUtc).Take(50)
                .Select(x => $"{x.TimestampUtc.ToLocalTime():g} · {x.Type}\n{x.Description}"));
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _refreshCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        RefreshButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        RefreshInfo.IsOpen = true;
        RefreshInfo.Severity = InfoBarSeverity.Informational;
        RefreshInfo.Title = "Refreshing shared telemetry…";
        RefreshInfo.Message = "Uses the existing collector; no model turn is created.";
        try
        {
            await App.Services.Telemetry.RefreshAsync(RefreshTrigger.Manual, cancellation.Token);
            if (_loaded)
            {
                RefreshInfo.Title = "Refresh finished";
                RefreshInfo.Message = "Check each source's reported state below; a completed refresh does not mean every source succeeded.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (_loaded) { RefreshInfo.Title = "Refresh cancelled"; RefreshInfo.Message = "The last published snapshot remains visible."; }
        }
        catch (Exception exception)
        {
            if (_loaded)
            {
                RefreshInfo.Severity = InfoBarSeverity.Error;
                RefreshInfo.Title = "Refresh failed";
                var message = exception.GetBaseException().Message.ReplaceLineEndings(" ");
                RefreshInfo.Message = message.Length <= 500 ? message : message[..500] + "…";
            }
        }
        finally
        {
            _refreshCancellation = null;
            RefreshButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => _refreshCancellation?.Cancel();
    private async void OnServerReadClicked(object sender, RoutedEventArgs e) => await ReadServerEvidenceAsync(false);
    private async void OnServerCollectClicked(object sender, RoutedEventArgs e) => await ReadServerEvidenceAsync(true);
    private async Task ReadServerEvidenceAsync(bool collect)
    {
        if (!ServerReadButton.IsEnabled) return;
        ServerReadButton.IsEnabled = ServerCollectButton.IsEnabled = false;
        ServerEvidenceText.Text = collect ? "Fetching bounded, read-only server reports…" : "Comparing retained evidence…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var report = await Task.Run(async () =>
            {
                if (collect) await App.Services.ServerEvidence.CollectAsync(true, timeout.Token);
                return await App.Services.ServerEvidence.CompareAsync(timeout.Token);
            });
            if (_loaded) ServerEvidenceText.Text = App.Services.ServerEvidence.Status + "\n\n" + report.ToDisplayText();
        }
        catch (Exception)
        {
            if (_loaded) ServerEvidenceText.Text = "Comparison unavailable or timed out. Retained evidence was not replaced.";
        }
        finally { ServerReadButton.IsEnabled = ServerCollectButton.IsEnabled = true; }
    }
    private void OnCoverageClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).Navigate(typeof(CodexRolloutCoveragePage));
    private void OnSourcesClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).Navigate(typeof(CodexSourcesPage));
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => ((App)Application.Current).Navigate(typeof(SettingsPage));
}
