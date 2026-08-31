using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.App.Services;

/// <summary>
/// Small process-lifetime composition root. Phase 2 keeps one telemetry coordinator alive for the
/// dashboard, tray and alert engine rather than letting each surface create its own provider loop.
/// </summary>
public sealed class AppServices
{
    public AppServices()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataFolder = Path.Combine(appDataPath, "TajsTokens");
        var databasePath = Path.Combine(DataFolder, "telemetry.db");
        var settingsPath = Path.Combine(DataFolder, "settings.json");

        try
        {
            Directory.CreateDirectory(DataFolder);
        }
        catch (Exception exception)
        {
            StartupPersistenceError = exception;
        }

        SettingsStore = new RuntimeSettingsStore(settingsPath);
        Settings = SettingsStore.Load();
        Repository = new SqliteTelemetryRepository(databasePath);
        TokscaleProvider = new TokscaleProvider();
        CodexQuotaProvider = new CodexAppServerQuotaProvider();
        Telemetry = new TelemetryCoordinator(TokscaleProvider, CodexQuotaProvider, Repository);
        AlertEngine = new QuotaAlertEngine(Settings.LowQuotaThresholds);
    }

    public string DataFolder { get; }
    public RuntimeSettings Settings { get; private set; }
    public RuntimeSettingsStore SettingsStore { get; }
    public SqliteTelemetryRepository Repository { get; }
    public ITokscaleProvider TokscaleProvider { get; }
    public ICodexQuotaProvider CodexQuotaProvider { get; }
    public TelemetryCoordinator Telemetry { get; }
    public QuotaAlertEngine AlertEngine { get; }
    public Exception? StartupPersistenceError { get; }

    public void SaveSettings(RuntimeSettings settings)
    {
        SettingsStore.Save(settings);
        Settings = SettingsStore.Load();
    }
}
