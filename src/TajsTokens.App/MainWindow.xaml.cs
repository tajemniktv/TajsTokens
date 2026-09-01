using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.Pages;

namespace TajsTokens.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(typeof(OverviewPage));
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string page)
        {
            return;
        }

        var pageType = page switch
        {
            "overview" => typeof(OverviewPage),
            "usage" => typeof(UsagePage),
            "observatory" => typeof(ObservatoryPage),
            "forecasts" => typeof(ForecastsPage),
            "analytics" => typeof(AnalyticsPage),
            "diagnostics" => typeof(PlaceholderPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(OverviewPage)
        };

        var parameter = page switch
        {
            "usage" => "Usage",
            "observatory" => "Codex Observatory",
            "forecasts" => "Forecasts",
            "analytics" => "Analytics",
            "diagnostics" => "Diagnostics",
            "settings" => "Settings",
            _ => "Overview"
        };

        ContentFrame.Navigate(pageType, parameter);
    }
}
