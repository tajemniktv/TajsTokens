namespace TajsTokens.Core.Models;

/// <summary>
/// One coherent Codex token-accounting generation. Model totals and hourly buckets must come from
/// the same provider generation so a partial provider failure cannot mix incompatible snapshots.
/// Coverage describes what the source can actually observe; native rollout accounting is local-only
/// until remote/cloud-only collection exists.
/// </summary>
public sealed record CodexTokenAccountingSnapshot(
    string Source,
    string Coverage,
    IReadOnlyList<TokenUsage> Usage,
    IReadOnlyList<TokenTimeBucket> Hourly,
    CodexAccountingReconciliation? Reconciliation = null,
    bool IsFallback = false,
    string? Diagnostic = null);

/// <summary>
/// Content-free comparison between the native projection and the optional Tokscale reference.
/// Differences are evidence for parser/attribution work, not a reason to silently replace native data.
/// </summary>
public sealed record CodexAccountingReconciliation(
    TokenBreakdown NativeTotals,
    TokenBreakdown ReferenceTotals,
    long TotalDifference,
    double TotalDifferencePercent,
    int ModelBucketsCompared,
    int ModelBucketsDifferent,
    int HourBucketsCompared,
    int HourBucketsDifferent)
{
    public bool Exact =>
        TotalDifference == 0 &&
        NativeTotals.UncachedInput == ReferenceTotals.UncachedInput &&
        NativeTotals.CacheRead == ReferenceTotals.CacheRead &&
        NativeTotals.CacheWrite == ReferenceTotals.CacheWrite &&
        NativeTotals.NonReasoningOutput == ReferenceTotals.NonReasoningOutput &&
        NativeTotals.ReasoningOutput == ReferenceTotals.ReasoningOutput &&
        ModelBucketsDifferent == 0 &&
        HourBucketsDifferent == 0;
}
