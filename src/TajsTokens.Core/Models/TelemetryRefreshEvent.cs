namespace TajsTokens.Core.Models;

public sealed record TelemetryRefreshEvent(
    DateTimeOffset TimestampUtc,
    string Type,
    string Description);
