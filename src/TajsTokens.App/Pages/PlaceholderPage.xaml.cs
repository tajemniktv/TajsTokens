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
            TitleBlock.Text = $"{section} (planned)";
        }

        base.OnNavigatedTo(e);
    }
}
