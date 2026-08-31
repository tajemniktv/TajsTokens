using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.ViewModels;

namespace TajsTokens.App.Pages;

public sealed partial class OverviewPage : Page
{
    private CancellationTokenSource? _loadCancellation;

    public OverviewPage()
    {
        InitializeComponent();

        var app = (App)Application.Current;
        ViewModel = new OverviewViewModel(
            app.Services.TokscaleProvider,
            app.Services.CodexQuotaProvider,
            app.Services.Repository);
        DataContext = ViewModel;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public OverviewViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();

        try
        {
            await ViewModel.RefreshAsync(_loadCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Navigation away from the page cancels an in-flight provider scan.
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCancellation?.Cancel();
    }
}
