// Taj's Tokens | CodexObservatoryRuntimeFactory.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Infrastructure.Services;

public sealed record CodexObservatoryRuntime(
    ICodexObservatoryStore Store,
    ICodexSessionIngestionService Ingestion,
    ICodexObservatoryService Service,
    ICodexRolloutInspection RolloutInspection);

/// <summary>
///     Infrastructure composition boundary for the local Codex observatory. UI code receives only the
///     normalized interfaces and never constructs rollout readers, Codex-private state readers, or
///     parser/ingestion implementations.
/// </summary>
public static class CodexObservatoryRuntimeFactory
{
    public static CodexObservatoryRuntime Create(
        string databasePath,
        ISessionIngestionCheckpointStore checkpointStore)
    {
        var store = new SqliteCodexObservatoryStore(databasePath);
        var rolloutProvider = new FileSystemCodexSessionEventProvider();
        var ingestionBatchWriter = new SqliteCodexIngestionBatchWriter(databasePath, store);
        var ingestion = new CodexSessionIngestionService(
            rolloutProvider,
            checkpointStore,
            store,
            ingestionBatchWriter);

        var stateIndexStore = new SqliteCodexStateIndexStore(databasePath);
        var stateCatalog = new CodexStateCatalog(
            CodexSqliteHome.Resolve(
                CodexObservatoryService.GetCodexHome(),
                Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME")));
        var service = new CodexObservatoryService(
            ingestion,
            store,
            stateCatalog,
            stateIndexStore);
        return new CodexObservatoryRuntime(store, ingestion, service, service);
    }
}