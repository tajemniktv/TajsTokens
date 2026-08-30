namespace TajsTokens.Core.Models;

public sealed record TokenUsage(
    string Model,
    string Scope,
    DateTimeOffset ObservedAtUtc,
    TokenBreakdown Breakdown);
