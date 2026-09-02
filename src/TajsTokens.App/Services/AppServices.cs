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
        CodexStateExplorer = new CodexStateDbExplorerService();
        CodexThreadObservability = new CodexThreadObservabilityService();
        TokscaleProvider = new TokscaleProvider();
        NativeCodexAccountingProvider = new SqliteNativeCodexAccountingProvider(DatabasePath);
        CodexTokenAccountingProvider = new NativeFirstCodexAccountingProvider(
            NativeCodexAccountingProvider,
            TokscaleProvider,
            () => Settings.TokscaleReconciliationEnabled,
            () => Settings.TokscaleFallbackEnabled);
        CodexQuotaProvider = new CodexAppServerQuotaProvider();

        var observatory = CodexObservatoryRuntimeFactory.Create(DatabasePath, Repository);
        ObservatoryStore = observatory.Store;
        CodexSessionIngestion = observatory.Ingestion;
        CodexObservatory = observatory.Service;

        Intelligence = new SqliteIntelligenceService(DatabasePath, Repository);
        Telemetry = new TelemetryCoordinator(
            CodexTokenAccountingProvider,
            CodexQuotaProvider,
            Repository,
            CodexObservatory,
            Intelligence);
        AlertEngine = new QuotaAlertEngine(Settings.LowQuotaThresholds);
    }

    public string DataFolder { get; }
    public string DatabasePath { get; }
    public RuntimeSettings Settings { get; private set; }
    public RuntimeSettingsStore SettingsStore { get; }
    public SqliteTelemetryRepository Repository { get; }
    public ICodexObservatoryStore ObservatoryStore { get; }
    public SqliteCodexObservatoryReadModel ObservatoryReadModel { get; }
    public CodexStateDbExplorerService CodexStateExplorer { get; }
    public CodexThreadObservabilityService CodexThreadObservability { get; }
    public ITokscaleProvider TokscaleProvider { get; }
    public ICodexTokenAccountingProvider NativeCodexAccountingProvider { get; }
    public ICodexTokenAccountingProvider CodexTokenAccountingProvider { get; }
    public ICodexQuotaProvider CodexQuotaProvider { get; }
    public ICodexSessionIngestionService CodexSessionIngestion { get; }
    public ICodexObservatoryService CodexObservatory { get; }
    public IIntelligenceService Intelligence { get; }
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
