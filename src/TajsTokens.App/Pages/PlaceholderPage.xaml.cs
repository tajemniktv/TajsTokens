using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace TajsTokens.App.Pages;

public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string section)
        {
            TitleBlock.Text = section;
            DescriptionBlock.Text = section switch
            {
                "Usage" => "Detailed token accounting, time-series, model/reasoning, repository/workspace and provider/account breakdowns live here rather than expanding the Overview forever.",
                "Forecasts" => "Short-window and weekly sustainable pace, burn pressure, confidence, forecast history and later scenario planning live here. Overview keeps only the current decision-oriented summary.",
                "Diagnostics" => "Provider health, app-server/Tokscale/rollout/SQLite freshness, import progress, classified failures and future doctor/incident information live here.",
                "Settings" => "Collection cadence, background/startup behavior, notifications, providers, privacy/storage and other application preferences live here.",
                _ => "This section has an intentional product role, but its dedicated surface is not implemented yet."
            };
        }

        base.OnNavigatedTo(e);
    }
}
