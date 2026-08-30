namespace TajsTokens.Core.Models;

public sealed record UsageEvent(
    string EventId,
    string SessionId,
    DateTimeOffset TimestampUtc,
    string EventType,
    string Summary,
    double? TokenDelta);
