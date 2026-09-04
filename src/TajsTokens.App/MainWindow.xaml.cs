using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.Pages;

namespace TajsTokens.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(typeof(CodexCliHarnessPage));
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage), "Settings");
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
            "analytics" => typeof(AnalyticsPage),
            "diagnostics" => typeof(PlaceholderPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage)
        };

        var parameter = page switch
        {
            "codex-cli" => "Codex CLI Harness",
            "codex" => "Codex",
            "codex-data" => "Codex Data Explorer",
            "forecasts" => "Forecasts",
            "analytics" => "Analytics",
            "diagnostics" => "Diagnostics",
            "settings" => "Settings",
            _ => "Overview"
        };

        ContentFrame.Navigate(pageType, parameter);
    }
}
