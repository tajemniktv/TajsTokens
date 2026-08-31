using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record Agent(
    string AgentId,
    string SessionId,
    string Name,
    AgentRuntimeState State,
    DateTimeOffset LastSeenUtc,
    string? Model);
