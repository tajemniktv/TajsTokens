using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ITelemetryRepository
{
    Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken);
    Task AddTokenUsageAsync(TokenUsage usage, CancellationToken cancellationToken);
    Task AddSessionAsync(CodexSession session, CancellationToken cancellationToken);
    Task AddOrUpdateAgentAsync(Agent agent, CancellationToken cancellationToken);
    Task AddAgentRelationshipAsync(AgentRelationship relationship, CancellationToken cancellationToken);
    Task AddUsageEventAsync(UsageEvent usageEvent, CancellationToken cancellationToken);
    Task AddResetEventAsync(ResetEvent resetEvent, CancellationToken cancellationToken);
    Task AddAnnouncementAsync(Announcement announcement, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuotaSnapshot>> GetRecentQuotaSnapshotsAsync(
        QuotaWindowKind kind,
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken);
}
