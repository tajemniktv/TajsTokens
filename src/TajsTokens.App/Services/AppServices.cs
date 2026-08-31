using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.App.Services;

/// <summary>
/// Tiny composition root for the early local-only application. A full DI container would add more
/// ceremony than value at this stage; provider interfaces still keep the UI isolated from adapters.
/// </summary>
public sealed class AppServices
{
    public AppServices()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataFolder = Path.Combine(appDataPath, "TajsTokens");
        var databasePath = Path.Combine(dataFolder, "telemetry.db");

        // Service construction must never prevent the WinUI shell from launching. If the directory
        // cannot be created, repository initialization will fail during refresh and the Overview can
        // report SQLite as unavailable while still showing live Codex/Tokscale data.
        try
        {
            Directory.CreateDirectory(dataFolder);
        }
        catch (Exception exception)
        {
            StartupPersistenceError = exception;
        }

        Repository = new SqliteTelemetryRepository(databasePath);
        TokscaleProvider = new TokscaleProvider();
        CodexQuotaProvider = new CodexAppServerQuotaProvider();
    }

    public SqliteTelemetryRepository Repository { get; }
    public ITokscaleProvider TokscaleProvider { get; }
    public ICodexQuotaProvider CodexQuotaProvider { get; }
    public Exception? StartupPersistenceError { get; }
}
