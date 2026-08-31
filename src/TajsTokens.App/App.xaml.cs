using Microsoft.UI.Xaml;
using TajsTokens.App.Services;

namespace TajsTokens.App;

public partial class App : Application
{
    private Window? _window;
    private readonly NoOpSystemTrayService _trayService = new();

    public App()
    {
        InitializeComponent();
        Services = new AppServices();
    }

    public AppServices Services { get; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _trayService.Initialize();
        _window = new MainWindow();
        _window.Activate();
    }
}
