namespace TajsTokens.Core.Models;

public sealed record RuntimeSettings
{
    private static readonly int[] DefaultThresholdValues = [30, 20, 10, 5];

    public const int CurrentSchemaVersion = 2;
    public static IReadOnlyList<int> DefaultLowQuotaThresholds { get; } = Array.AsReadOnly(DefaultThresholdValues);

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public bool RunInBackground { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 60;
    public bool NotificationsEnabled { get; init; } = true;
    public int[] LowQuotaThresholds { get; init; } = DefaultThresholdValues.ToArray();
    public bool LaunchAtLogin { get; init; }

    /// <summary>
    /// Runs the optional Tokscale CLI beside the native projection and reports aggregate parity.
    /// Disabled by default so ordinary collection never launches Tokscale/npx.
    /// </summary>
    public bool TokscaleReconciliationEnabled { get; init; }

    /// <summary>
    /// Allows Tokscale to provide the displayed token generation only when native accounting fails.
    /// This is explicitly opt-in because Tokscale is no longer a mandatory runtime dependency.
    /// </summary>
    public bool TokscaleFallbackEnabled { get; init; }

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
