namespace TajsTokens.Core.Models;

/// <summary>
/// Disjoint token buckets. Provider adapters are responsible for converting provider-specific
/// counters (for example Codex cached-input-as-a-subset-of-input) into these non-overlapping values.
/// </summary>
public sealed record TokenBreakdown(
    long UncachedInput,
    long CacheRead,
    long CacheWrite,
    long NonReasoningOutput,
    long ReasoningOutput,
    long? ReportedTotal = null)
{
    public long ComputedTotal => checked(UncachedInput + CacheRead + CacheWrite + NonReasoningOutput + ReasoningOutput);
    public long Total => ReportedTotal ?? ComputedTotal;

    /// <summary>
    /// Difference between the provider/native reported-total counter and the independently normalized
    /// disjoint classes. Null means no independent reported total was available for comparison.
    /// </summary>
    public long? IntegrityDelta => ReportedTotal is long reported ? checked(reported - ComputedTotal) : null;
    public bool? IntegrityExact => IntegrityDelta is long delta ? delta == 0 : null;
}
