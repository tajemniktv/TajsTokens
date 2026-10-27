using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexCalibrationService
{
    Task<CodexCalibrationResult?> TryCalibrateAsync(IReadOnlyList<QuotaLaneState> lanes, CancellationToken cancellationToken);
}

public sealed record CodexCalibrationResult(IReadOnlyList<CurrentQuotaForecast> Forecasts,
    TokenWorkloadForecast? Workload, long CacheHits = 0, long CacheMisses = 0);
