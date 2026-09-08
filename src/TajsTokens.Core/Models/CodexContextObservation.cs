namespace TajsTokens.Core.Models;

public sealed record CodexContextObservation(
    string EventId,
    string SessionId,
    string? AgentId,
    DateTimeOffset ObservedAtUtc,
    string? Model,
    long? InputTokens,
    long? ContextWindowTokens,
    bool IsCompaction,
    long? RecordBytes = null,
    DateTimeOffset? CapturedAtUtc = null)
{
    public double? UtilizationPercent => InputTokens is > 0 && ContextWindowTokens is > 0
        ? Math.Clamp(InputTokens.Value * 100d / ContextWindowTokens.Value, 0d, 100d)
        : null;
}
