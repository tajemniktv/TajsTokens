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
/// normalized interfaces and never constructs rollout readers, Codex-private state readers, or
/// parser/ingestion implementations.
/// </summary>
public static class CodexObservatoryRuntimeFactory
{
    public static CodexObservatoryRuntime Create(
        string databasePath,
        ISessionIngestionCheckpointStore checkpointStore)
    {
        var store = new SqliteCodexObservatoryStore(databasePath);
        var rolloutProvider = new FileSystemCodexSessionEventProvider();
        var rolloutRecordBatchWriter = new SqliteCodexRolloutRecordBatchWriter(databasePath, store);
        var semanticBatchWriter = new SqliteCodexSemanticBatchWriter(databasePath, store);
        var ingestion = new CodexSessionIngestionService(
            rolloutProvider,
            checkpointStore,
            store,
            rolloutRecordBatchWriter,
            semanticBatchWriter);

        var stateIndexStore = new SqliteCodexStateIndexStore(databasePath);
        var stateCatalog = new CodexStateCatalog(CodexObservatoryService.GetCodexHome());
        var service = new CodexObservatoryService(
            ingestion,
            store,
            stateCatalog,
            stateIndexStore);
        return new CodexObservatoryRuntime(store, ingestion, service);
    }
}
