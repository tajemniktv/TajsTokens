using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexQuotaProvider
{
    Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken);
}
