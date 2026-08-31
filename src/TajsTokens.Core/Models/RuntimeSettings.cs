namespace TajsTokens.Core.Models;

public sealed record RuntimeSettings
{
    public bool RunInBackground { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 60;
    public bool NotificationsEnabled { get; init; } = true;
    public int[] LowQuotaThresholds { get; init; } = [30, 20, 10, 5];
    public bool LaunchAtLogin { get; init; }
}
