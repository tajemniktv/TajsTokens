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
        Directory.CreateDirectory(dataFolder);

        Repository = new SqliteTelemetryRepository(Path.Combine(dataFolder, "telemetry.db"));
        TokscaleProvider = new TokscaleProvider();
        CodexQuotaProvider = new CodexAppServerQuotaProvider();
    }

    public SqliteTelemetryRepository Repository { get; }
    public ITokscaleProvider TokscaleProvider { get; }
    public ICodexQuotaProvider CodexQuotaProvider { get; }
}
