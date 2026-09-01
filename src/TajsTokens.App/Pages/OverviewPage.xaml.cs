using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.ViewModels;
using TajsTokens.Core.Models;

namespace TajsTokens.App.Pages;

public sealed partial class OverviewPage : Page
{
    private readonly App _app;

    public OverviewPage()
    {
        InitializeComponent();

        _app = (App)Application.Current;
        ViewModel = new OverviewViewModel(
            _app.Services.Telemetry,
            _app.Services.Repository);
        DataContext = ViewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public OverviewViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.Services.Telemetry.SnapshotUpdated += OnSnapshotUpdated;

        var latest = _app.Services.Telemetry.Latest;
        if (latest.CapturedAtUtc != DateTimeOffset.MinValue)
        {
            try
            {
                await ViewModel.ApplySnapshotAsync(latest, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // No page-scoped provider request exists anymore; this is defensive only.
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _app.Services.Telemetry.SnapshotUpdated -= OnSnapshotUpdated;
        ViewModel.RefreshCommand.Cancel();
    }

    private void OnSnapshotUpdated(TelemetrySnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(() => _ = ApplySnapshotUpdateAsync(snapshot));
    }

    private async Task ApplySnapshotUpdateAsync(TelemetrySnapshot snapshot)
    {
        try
        {
            await ViewModel.ApplySnapshotAsync(snapshot, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown/navigation can abandon a UI-only render safely.
        }
        catch (Exception exception)
        {
            var detail = exception.GetBaseException().Message.ReplaceLineEndings(" ").Trim();
            ViewModel.StatusText = $"Snapshot render failed: {(detail.Length <= 240 ? detail : detail[..240] + "…")}";
        }
    }
}
