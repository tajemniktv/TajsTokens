using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

public sealed record CodexObservatoryRuntime(
    ICodexObservatoryStore Store,
    ICodexSessionIngestionService Ingestion,
    ICodexObservatoryService Service);

/// <summary>
/// Infrastructure composition boundary for the local Codex observatory. UI code receives only the
/// normalized interfaces and never constructs rollout readers or parser/ingestion implementations.
/// </summary>
public static class CodexObservatoryRuntimeFactory
{
    public static CodexObservatoryRuntime Create(
        string databasePath,
        ISessionIngestionCheckpointStore checkpointStore)
    {
        var store = new SqliteCodexObservatoryStore(databasePath);
        var rolloutProvider = new FileSystemCodexSessionEventProvider();
        var rolloutRecordBatchWriter = new SqliteCodexRolloutRecordBatchWriter(databasePath);
        var ingestion = new CodexSessionIngestionService(
            rolloutProvider,
            checkpointStore,
            store,
            rolloutRecordBatchWriter);
        var service = new CodexObservatoryService(ingestion, store);
        return new CodexObservatoryRuntime(store, ingestion, service);
    }
}
