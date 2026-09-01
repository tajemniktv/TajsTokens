namespace TajsTokens.Core.Models;

public sealed record RuntimeSettings
{
    private static readonly int[] DefaultThresholdValues = [30, 20, 10, 5];

    public const int CurrentSchemaVersion = 1;
    public static IReadOnlyList<int> DefaultLowQuotaThresholds { get; } = Array.AsReadOnly(DefaultThresholdValues);

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool RunInBackground { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 60;
    public bool NotificationsEnabled { get; init; } = true;
    public int[] LowQuotaThresholds { get; init; } = DefaultThresholdValues.ToArray();
    public bool LaunchAtLogin { get; init; }

    public static int[] NormalizeLowQuotaThresholds(IEnumerable<int>? thresholds)
    {
        var normalized = (thresholds ?? [])
            .Where(value => value is > 0 and < 100)
            .Distinct()
            .OrderByDescending(value => value)
            .ToArray();

        return normalized.Length == 0 ? DefaultThresholdValues.ToArray() : normalized;
    }
}
