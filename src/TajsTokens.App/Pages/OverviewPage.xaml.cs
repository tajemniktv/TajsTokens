using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
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
        ViewModel = new OverviewViewModel(_app.Services.Telemetry);
        DataContext = ViewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        OverviewViewport.SizeChanged += OnViewportSizeChanged;
    }

    public OverviewViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout(OverviewViewport.ActualWidth);
        _app.Services.Telemetry.SnapshotUpdated += OnSnapshotUpdated;

        var latest = _app.Services.Telemetry.Latest;
        if (latest.CapturedAtUtc != DateTimeOffset.MinValue)
        {
            try
            {
                await ViewModel.ApplySnapshotAsync(latest, CancellationToken.None);
                RenderChart();
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
            RenderChart();
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

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    private void ApplyLayout(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        // Constrain the scroll content to its actual viewport, not its children's desired width.
        // Use the available page width (after navigation), as the Codex browser does.
        OverviewContent.Width = width;
        var wide = width >= (double)Application.Current.Resources["WideContentBreakpoint"];
        var both = ViewModel.FiveHourQuota.IsReported && ViewModel.WeeklyQuota.IsReported;
        FiveHourCard.Visibility = ViewModel.FiveHourQuota.IsReported && !ViewModel.ShowSetup ? Visibility.Visible : Visibility.Collapsed;
        WeeklyCard.Visibility = ViewModel.WeeklyQuota.IsReported && !ViewModel.ShowSetup ? Visibility.Visible : Visibility.Collapsed;
        UnreportedStrip.Visibility = both ? Visibility.Collapsed : Visibility.Visible;
        UnreportedText.Text = string.Join("\n", new[] { ViewModel.FiveHourQuota, ViewModel.WeeklyQuota }.Where(x => !x.IsReported)
            .Select(x => $"{x.Title}: not currently reported by Codex. This does not mean unlimited usage."));
        QuotaSecondColumn.Width = wide && both ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        UsageSecondColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(WeeklyCard, both && !wide ? 1 : 0);
        Grid.SetColumn(WeeklyCard, both && wide ? 1 : 0);
        Grid.SetRow(TokenCard, wide ? 0 : 1);
        Grid.SetColumn(TokenCard, wide ? 1 : 0);
        QuotaLayout.ColumnSpacing = UsageLayout.ColumnSpacing = wide ? 16 : 0;
        QuotaLayout.RowSpacing = UsageLayout.RowSpacing = wide ? 0 : 16;
    }

    private void RenderChart()
    {
        ApplyLayout(OverviewViewport.ActualWidth);
        HourlyChart.Children.Clear();
        HourlyChart.ColumnDefinitions.Clear();
        foreach (var point in ViewModel.HistoryPoints)
        {
            var column = HourlyChart.ColumnDefinitions.Count;
            HourlyChart.ColumnDefinitions.Add(new ColumnDefinition());
            var item = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 8 };
            item.Children.Add(new TextBlock { Text = point.Amount, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            item.Children.Add(new Border
            {
                Height = point.Value, MaxWidth = 44, CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            });
            item.Children.Add(new TextBlock { Text = point.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            ToolTipService.SetToolTip(item, point.Tooltip);
            AutomationProperties.SetName(item, point.Tooltip);
            Grid.SetColumn(item, column);
            HourlyChart.Children.Add(item);
        }
    }
}
