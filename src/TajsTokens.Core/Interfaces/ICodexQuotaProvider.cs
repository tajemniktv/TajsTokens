using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ICodexQuotaProvider
{
    Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken);

    // Compatibility for providers implementing the original snapshot-only contract. No windows
    // means unknown scope, not an inferred identity. The native adapter preserves response scope.
    async Task<CodexQuotaResponse> GetQuotaResponseAsync(CancellationToken cancellationToken)
    {
        var snapshots = await GetQuotaSnapshotsAsync(cancellationToken);
        return new(snapshots, snapshots.Select(snapshot => snapshot.AccountKey).Distinct().SingleOrDefault());
    }
}
