using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record ResetEvent(
    string EventId,
    QuotaWindowKind Kind,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset EffectiveAtUtc,
    string Source);
