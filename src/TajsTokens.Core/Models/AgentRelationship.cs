namespace TajsTokens.Core.Models;

public sealed record AgentRelationship(
    string ParentAgentId,
    string ChildAgentId,
    DateTimeOffset LinkedAtUtc);
