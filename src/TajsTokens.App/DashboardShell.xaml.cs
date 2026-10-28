using Microsoft.UI.Xaml.Controls;

namespace TajsTokens.App;

public sealed partial class DashboardShell : UserControl
{
    public DashboardShell() => InitializeComponent();
    internal NavigationView Navigation => AppNavigation;
    internal Frame InitialFrame => (Frame)AppNavigation.Content;
}
