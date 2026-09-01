using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.App.Services;

/// <summary>
/// Process-lifetime application composition root. Codex rollout reader/parser construction remains
/// behind the Infrastructure observatory factory; UI surfaces consume normalized/query boundaries.
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
        ObservatoryReadModel = new SqliteCodexObservatoryReadModel(DatabasePath);
        TokscaleProvider = new TokscaleProvider();
        CodexQuotaProvider = new CodexAppServerQuotaProvider();

        var observatory = CodexObservatoryRuntimeFactory.Create(DatabasePath, Repository);
        ObservatoryStore = observatory.Store;
        CodexSessionIngestion = observatory.Ingestion;
        CodexObservatory = observatory.Service;

        Telemetry = new TelemetryCoordinator(TokscaleProvider, CodexQuotaProvider, Repository, CodexObservatory);
        AlertEngine = new QuotaAlertEngine(Settings.LowQuotaThresholds);
    }

    public string DataFolder { get; }
    public string DatabasePath { get; }
    public RuntimeSettings Settings { get; private set; }
    public RuntimeSettingsStore SettingsStore { get; }
    public SqliteTelemetryRepository Repository { get; }
    public ICodexObservatoryStore ObservatoryStore { get; }
    public SqliteCodexObservatoryReadModel ObservatoryReadModel { get; }
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
