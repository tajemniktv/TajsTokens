namespace TajsTokens.Core.Models;

/// <summary>
/// Provider-normalized token usage for one time bucket. Provider provenance is retained explicitly;
/// the display label is preserved even when the provider does not expose an unambiguous
/// offset-aware timestamp.
/// </summary>
public sealed record TokenTimeBucket(
    string Provider,
    string Label,
    DateTimeOffset? StartUtc,
    TokenBreakdown Breakdown);
