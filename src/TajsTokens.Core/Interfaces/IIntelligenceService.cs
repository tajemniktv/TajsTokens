// Taj's Tokens | IIntelligenceService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Interfaces;

public interface IIntelligenceService
{
    Task<IntelligenceRefreshResult> RefreshAsync(CancellationToken cancellationToken);

    Task<TokenWorkloadForecast> ForecastTokenWorkloadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<CurrentQuotaForecast>> BuildAndPersistCurrentForecastsAsync(
        IReadOnlyList<QuotaLaneState> quotaLanes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<IntelligenceDashboard> QueryAsync(
        IntelligenceQuery query,
        CancellationToken cancellationToken);

    Task<QuotaBurnDetail> GetQuotaBurnDetailAsync(
        QuotaBurnInterval interval,
        int take,
        CancellationToken cancellationToken);

    Task<ScenarioEstimate> EstimateScenarioAsync(
        ScenarioRequest request,
        DateTimeOffset historyFromUtc,
        CancellationToken cancellationToken);

    Task<ForecastEvaluationReport> EvaluateForecastsAsync(
        string provider,
        string profile,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    Task<RecentScenarioPattern> GetRecentScenarioPatternAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<TtEvaluationArchiveEntry>> GetTtEvaluationHistoryAsync(
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken);
}