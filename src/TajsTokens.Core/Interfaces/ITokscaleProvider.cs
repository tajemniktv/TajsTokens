using TajsTokens.Core.Models;

namespace TajsTokens.Core.Interfaces;

/// <summary>
/// Optional Tokscale reference adapter. The two legacy operations remain available for focused
/// parser/CLI tests, while the inherited accounting-provider contract exposes one atomic generation
/// to the runtime coordinator without forcing every Tokscale test double to repeat adapter glue.
/// </summary>
public interface ITokscaleProvider : ICodexTokenAccountingProvider
{
    Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken);

    async Task<CodexTokenAccountingSnapshot> ICodexTokenAccountingProvider.GetSnapshotAsync(CancellationToken cancellationToken)
    {
        // Keep the generation atomic: if either command fails, callers retain the previous complete
        // snapshot rather than combining fresh model totals with stale hourly buckets or vice versa.
        var usage = await GetUsageObservationsAsync(cancellationToken);
        var hourly = await GetHourlyUsageAsync(cancellationToken);
        return new CodexTokenAccountingSnapshot(
            "Tokscale",
            "Local Codex rollout history discovered by Tokscale; remote/cloud-only sessions may be absent.",
            usage,
            hourly);
    }
}
