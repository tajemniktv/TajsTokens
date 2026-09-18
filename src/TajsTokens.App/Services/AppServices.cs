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
        if (!Directory.Exists(AppDataLocation.GetDataFolder(appDataPath)) && HasOtherInstanceInSession())
            throw new InvalidOperationException("Exit other TajsTokens instances before migrating application data.");
        DataFolder = AppDataLocation.EnsureMigrated(appDataPath);
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
        CodexNativeSources = new CodexNativeSourcesService(explorer: CodexStateExplorer);
        CodexThreadObservability = new CodexThreadObservabilityService();
        CodexThreadReadModel = new CodexThreadReadModel(CodexThreadObservability);
        CodexCliHarness = new CodexCliHarnessService();
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
        CodexRolloutInspection = observatory.RolloutInspection;

        Intelligence = new SqliteIntelligenceService(DatabasePath, Repository, () => Settings.RolloutAccountAssociations);
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
    public ICodexNativeSourcesReadModel CodexNativeSources { get; }
    /// <summary>Source-reader access retained for compatibility with existing integrations.</summary>
    public CodexThreadObservabilityService CodexThreadObservability { get; }

    /// <summary>Product-facing provider-native Codex thread query boundary.</summary>
    public ICodexThreadReadModel CodexThreadReadModel { get; }
    public ICodexCliHarness CodexCliHarness { get; }
    public ITokscaleProvider TokscaleProvider { get; }
    public ICodexTokenAccountingProvider NativeCodexAccountingProvider { get; }
    public ICodexTokenAccountingProvider CodexTokenAccountingProvider { get; }
    public ICodexQuotaProvider CodexQuotaProvider { get; }
    public ICodexSessionIngestionService CodexSessionIngestion { get; }
    public ICodexObservatoryService CodexObservatory { get; }
    public ICodexRolloutInspection CodexRolloutInspection { get; }
    public IIntelligenceService Intelligence { get; }
    public async Task<RolloutAccountAssociation[]> PrepareRolloutAccountAssociationsAsync(string accountKey,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var data = await new SqliteForecastDatasetReader(DatabasePath).ReadAsync("codex", "default",
            DateTimeOffset.UnixEpoch.AddDays(1), now, cancellationToken);
        return RolloutAccountAssociationPolicy.Create(data, accountKey, now).ToArray();
    }
    public TelemetryCoordinator Telemetry { get; }
    public QuotaAlertEngine AlertEngine { get; }
    public Exception? StartupPersistenceError { get; }

    public event Action<RuntimeSettings, RuntimeSettings>? SettingsChanged;

    private static bool HasOtherInstanceInSession()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var processes = System.Diagnostics.Process.GetProcessesByName("TajsTokens.App");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.SessionId == current.SessionId)
                        return true;
                }
                catch (InvalidOperationException) { } // Exited after enumeration; it is absent.
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public void SaveSettings(RuntimeSettings settings)
    {
        var previous = Settings;
        SettingsStore.Save(settings);
        Settings = SettingsStore.Load();
        AlertEngine.UpdateThresholds(Settings.LowQuotaThresholds);
        SettingsChanged?.Invoke(previous, Settings);
    }
}
