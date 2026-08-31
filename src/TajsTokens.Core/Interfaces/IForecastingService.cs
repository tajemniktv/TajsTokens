using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface IForecastingService
{
    Forecast BuildForecast(IReadOnlyList<QuotaSnapshot> snapshots, DateTimeOffset nowUtc);
}
