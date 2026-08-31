namespace TajsTokens.Core.Models;

public sealed record SystemTrayStatus(
    string Tooltip,
    int? ConstrainedRemainingPercent,
    bool IsFresh,
    string HealthLabel);
