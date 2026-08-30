using System.Diagnostics;
using Microsoft.UI.Xaml;
using TajsTokens.App.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.App;

public partial class App : Application
{
    private Window? _window;
    private SqliteTelemetryRepository? _repository;
    private readonly NoOpSystemTrayService _trayService = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _trayService.Initialize();
        _ = InitializeDataLayerAsync();
        _window = new MainWindow();
        _window.Activate();
    }

    private async Task InitializeDataLayerAsync()
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dbFolder = Path.Combine(appDataPath, "TajsTokens");
            Directory.CreateDirectory(dbFolder);

            _repository = new SqliteTelemetryRepository(Path.Combine(dbFolder, "telemetry.db"));
            await _repository.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Keep bootstrap launch resilient, but never silently erase persistence failures.
            Debug.WriteLine($"TajsTokens telemetry initialization failed: {exception}");
        }
    }
}
