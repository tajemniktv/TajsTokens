using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface IIntelligenceService
{
    Task<IntelligenceRefreshResult> RefreshAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CurrentQuotaForecast>> BuildAndPersistCurrentForecastsAsync(
        IReadOnlyList<QuotaLaneState> quotaLanes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CurrentQuotaForecast>>([]);

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
}
