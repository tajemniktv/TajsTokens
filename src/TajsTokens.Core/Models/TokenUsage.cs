namespace TajsTokens.Core.Models;

public sealed record TokenUsage(
    string Provider,
    string Client,
    string Model,
    DateTimeOffset ObservedAtUtc,
    TokenBreakdown Breakdown,
    string? Profile = null,
    string? SessionId = null,
    string? ThreadId = null,
    string? Repository = null,
    string? AgentId = null);
