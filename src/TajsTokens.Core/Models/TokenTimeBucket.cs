namespace TajsTokens.Core.Models;

/// <summary>
/// Provider-normalized token usage for one time bucket. The display label is preserved even when
/// the provider does not expose an unambiguous offset-aware timestamp.
/// </summary>
public sealed record TokenTimeBucket(
    string Label,
    DateTimeOffset? StartUtc,
    TokenBreakdown Breakdown);
