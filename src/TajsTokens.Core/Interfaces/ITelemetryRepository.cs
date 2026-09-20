// Taj's Tokens | ITelemetryRepository.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface ITelemetryRepository
{
    Task UpsertQuotaSnapshotAsync(QuotaSnapshot snapshot, CancellationToken cancellationToken);
    Task SaveServerEvidenceAsync(CodexServerCollection collection, CancellationToken cancellationToken);
    Task AddTokenUsageAsync(TokenUsage usage, CancellationToken cancellationToken);
    Task AddSessionAsync(CodexSession session, CancellationToken cancellationToken);
    Task AddOrUpdateAgentAsync(Agent agent, CancellationToken cancellationToken);
    Task AddAgentRelationshipAsync(AgentRelationship relationship, CancellationToken cancellationToken);
    Task AddUsageEventAsync(UsageEvent usageEvent, CancellationToken cancellationToken);
    Task AddResetEventAsync(ResetEvent resetEvent, CancellationToken cancellationToken);
    Task AddAnnouncementAsync(Announcement announcement, CancellationToken cancellationToken);
    Task UpsertRepositoryAsync(RepositoryIdentity repository, CancellationToken cancellationToken);
    Task UpsertWorkspaceAsync(WorkspaceIdentity workspace, CancellationToken cancellationToken);
    Task UpsertForecastSnapshotAsync(ForecastSnapshot snapshot, CancellationToken cancellationToken);

    // Null accountKey selects unknown scope; it is not an all-accounts wildcard.
    Task<IReadOnlyList<QuotaSnapshot>> GetRecentQuotaSnapshotsAsync(
        QuotaWindowKind kind,
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken,
        DateTimeOffset? capturedAtUpperBoundUtc = null,
        string? source = null,
        string? accountKey = null);

    Task<IReadOnlyList<ForecastSnapshot>> GetRecentForecastSnapshotsAsync(
        QuotaWindowKind kind,
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken);
}