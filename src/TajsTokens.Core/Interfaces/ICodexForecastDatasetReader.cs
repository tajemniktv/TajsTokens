using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexForecastDatasetReader
{
    Task<CodexForecastDataset> ReadAsync(string provider, string profile,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken,
        string? accountKey = null, bool includeQuota = true, bool includeLedger = false);
}
