namespace TajsTokens.Core.Models;

public sealed record CodexSession(
    string SessionId,
    string? ThreadId,
    string Repository,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    string Status);
