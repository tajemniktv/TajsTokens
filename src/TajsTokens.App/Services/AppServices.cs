using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.App.Services;

/// <summary>
/// Process-lifetime composition root shared by the dashboard, tray, alert engine, and Phase 3 Codex
/// observatory. Provider surfaces consume normalized local services instead of parsing files directly.
/// </summary>
public sealed class AppServices
{
    public AppServices()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataFolder = Path.Combine(appDataPath, "TajsTokens");
        DatabasePath = Path.Combine(DataFolder, "telemetry.db");
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
        Repository = new SqliteTelemetryRepository(DatabasePath);
        ObservatoryStore = new SqliteCodexObservatoryStore(DatabasePath);
        TokscaleProvider = new TokscaleProvider();
        CodexQuotaProvider = new CodexAppServerQuotaProvider();

        var rolloutProvider = new FileSystemCodexSessionEventProvider();
        CodexSessionIngestion = new CodexSessionIngestionService(rolloutProvider, Repository, ObservatoryStore);
        CodexObservatory = new CodexObservatoryService(CodexSessionIngestion, ObservatoryStore);

        Telemetry = new TelemetryCoordinator(TokscaleProvider, CodexQuotaProvider, Repository, CodexObservatory);
        AlertEngine = new QuotaAlertEngine(Settings.LowQuotaThresholds);
    }

    public string DataFolder { get; }
    public string DatabasePath { get; }
    public RuntimeSettings Settings { get; private set; }
    public RuntimeSettingsStore SettingsStore { get; }
    public SqliteTelemetryRepository Repository { get; }
    public SqliteCodexObservatoryStore ObservatoryStore { get; }
    public ITokscaleProvider TokscaleProvider { get; }
    public ICodexQuotaProvider CodexQuotaProvider { get; }
    public ICodexSessionIngestionService CodexSessionIngestion { get; }
    public ICodexObservatoryService CodexObservatory { get; }
    public TelemetryCoordinator Telemetry { get; }
    public QuotaAlertEngine AlertEngine { get; }
    public Exception? StartupPersistenceError { get; }

    public event Action<RuntimeSettings, RuntimeSettings>? SettingsChanged;

    public void SaveSettings(RuntimeSettings settings)
    {
        var previous = Settings;
        SettingsStore.Save(settings);
        Settings = SettingsStore.Load();
        AlertEngine.UpdateThresholds(Settings.LowQuotaThresholds);
        SettingsChanged?.Invoke(previous, Settings);
    }
}
