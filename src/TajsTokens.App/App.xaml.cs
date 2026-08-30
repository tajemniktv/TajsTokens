using Microsoft.UI.Xaml;
using TajsTokens.App.Services;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.App;

public partial class App : Application
{
    private Window? _window;
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

    private static async Task InitializeDataLayerAsync()
    {
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dbFolder = Path.Combine(appDataPath, "TajsTokens");
            Directory.CreateDirectory(dbFolder);

            var repository = new SqliteTelemetryRepository(Path.Combine(dbFolder, "telemetry.db"));
            await repository.InitializeAsync(CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(
                new QuotaSnapshot(
                    QuotaWindowKind.FiveHour,
                    DateTimeOffset.UtcNow,
                    54_000,
                    200_000,
                    DateTimeOffset.UtcNow.AddHours(2)),
                CancellationToken.None);
        }
        catch
        {
            // Keep launch resilient while persistence/provider foundation is still being scaffolded.
        }
    }
}
