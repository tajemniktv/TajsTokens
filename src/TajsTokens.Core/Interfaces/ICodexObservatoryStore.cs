using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexObservatoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task UpsertSessionAsync(CodexSession session, CancellationToken cancellationToken);
    Task UpsertAgentAsync(Agent agent, CancellationToken cancellationToken);
    Task UpsertAgentRelationshipAsync(AgentRelationship relationship, CancellationToken cancellationToken);
    Task UpsertUsageEventAsync(UsageEvent usageEvent, CancellationToken cancellationToken);
    Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken);
    Task ApplyCumulativeTokenObservationAsync(CodexTokenCountObservation observation, CancellationToken cancellationToken);
    Task UpsertContextObservationAsync(CodexContextObservation observation, CancellationToken cancellationToken);
    Task UpsertWorkloadObservationAsync(CodexWorkloadObservation observation, CancellationToken cancellationToken);
    Task<CodexParserResumeState?> GetParserResumeStateAsync(string sourceIdentity, CancellationToken cancellationToken);
    Task UpsertParserResumeStateAsync(CodexParserResumeState state, CancellationToken cancellationToken);
    Task UpsertRolloutFileAsync(
        string sourceIdentity,
        string filePath,
        string? sessionId,
        long fileSizeBytes,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
    Task RecordRolloutRecordAsync(
        string sourceRecordId,
        string sourceIdentity,
        string filePath,
        string? sessionId,
        string eventClass,
        long recordBytes,
        long fileSizeBytes,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    Task<CodexObservatorySummary> GetSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CodexSessionOverview>> GetSessionOverviewsAsync(int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<Agent>> GetAgentsAsync(string? sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentRelationship>> GetAgentRelationshipsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<UsageEvent>> GetTimelineAsync(string sessionId, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodexContextObservation>> GetContextObservationsAsync(string sessionId, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodexRolloutStorageSummary>> GetRolloutStorageAsync(int take, CancellationToken cancellationToken);
}
