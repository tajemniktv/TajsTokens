using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TajsTokens.App.Pages;

namespace TajsTokens.App;

public sealed partial class MainWindow : Window
{
    private bool _synchronizing;
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 850));
        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange || AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
                { State: Microsoft.UI.Windowing.OverlappedPresenterState.Restored }) return;
            var size = AppWindow.Size;
            if (size.Width > 0 && size.Height > 0 && (size.Width < 700 || size.Height < 650))
                AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(700, size.Width), Math.Max(650, size.Height)));
        };
        Navigate(typeof(OverviewPage));
    }

    public void Navigate(Type pageType, object? parameter = null)
    {
        if (ContentFrame is null) return;
        if (ContentFrame.CurrentSourcePageType == pageType && Equals(ContentFrame.Tag, parameter)) return;
        ContentFrame.Tag = parameter;
        ContentFrame.Navigate(pageType, parameter);
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (AppNavigation is null) return;
        _synchronizing = true;
        try
        {
            ContentFrame.Tag = e.Parameter;
            AppNavigation.IsBackEnabled = ContentFrame.CanGoBack;
            var tag = e.SourcePageType == typeof(ForecastsPage) && Equals(e.Parameter, "model-lab") ? "model-lab" :
                e.SourcePageType == typeof(ForecastsPage) ? "forecasts" :
                e.SourcePageType == typeof(OverviewPage) ? "overview" : e.SourcePageType == typeof(CodexPage) ? "codex" :
                e.SourcePageType == typeof(AnalyticsPage) ? "analytics" : e.SourcePageType == typeof(DiagnosticsPage) ? "diagnostics" :
                e.SourcePageType == typeof(CodexRolloutCoveragePage) ? "coverage" : e.SourcePageType == typeof(CodexSourcesPage) ? "sources" :
                e.SourcePageType == typeof(CodexDataExplorerPage) ? "codex-data" : e.SourcePageType == typeof(CodexCliHarnessPage) ? "codex-cli" : "settings";
            var items = AppNavigation.MenuItems.OfType<NavigationViewItem>();
            var selected = tag == "settings" ? AppNavigation.SettingsItem :
                items.Concat(items.SelectMany(x => x.MenuItems.OfType<NavigationViewItem>())).FirstOrDefault(x => Equals(x.Tag, tag));
            foreach (var parent in items.Where(x => x.MenuItems.Contains(selected))) parent.IsExpanded = true;
            AppNavigation.SelectedItem = selected;
        }
        finally { _synchronizing = false; }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_synchronizing) return;
        if (args.IsSettingsSelected)
        {
            Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string page)
        {
            return;
        }

        var pageType = page switch
        {
            "overview" => typeof(OverviewPage),
            "codex-cli" => typeof(CodexCliHarnessPage),
            "codex" => typeof(CodexPage),
            "codex-data" => typeof(CodexDataExplorerPage),
            "forecasts" => typeof(ForecastsPage),
            "model-lab" => typeof(ForecastsPage),
            "coverage" => typeof(CodexRolloutCoveragePage),
            "sources" => typeof(CodexSourcesPage),
            "analytics" => typeof(AnalyticsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage)
        };

        Navigate(pageType, page == "model-lab" ? "model-lab" : null);
    }
}
