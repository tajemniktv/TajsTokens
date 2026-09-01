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
            "agents" => typeof(ObservatoryPage),
            "usage" => typeof(PlaceholderPage),
            "forecasts" => typeof(PlaceholderPage),
            "events" => typeof(PlaceholderPage),
            "settings" => typeof(PlaceholderPage),
            _ => typeof(OverviewPage)
        };

        var parameter = page switch
        {
            "usage" => "Usage",
            "agents" => "Observatory",
            "forecasts" => "Forecasts",
            "events" => "Events",
            "settings" => "Settings",
            _ => "Overview"
        };

        ContentFrame.Navigate(pageType, parameter);
    }
}
