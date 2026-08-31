using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

public interface ITokscaleProvider
{
    Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken);
}
