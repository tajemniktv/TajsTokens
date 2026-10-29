using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public enum QuotaForecastComputationPhase { CompactPace, Detailed }
public enum QuotaForecastPointRole { SelectedReset, SelectedHorizon, DirectBaseline }
public enum QuotaForecastRecordingTiming { BeforeTarget, AtOrAfterTarget, InvalidClock, Unconfirmed }

/// <summary>Closed numeric output, not a claim that the forecast was displayed or an alert sent.</summary>
public sealed record QuotaForecastComputedPoint(QuotaForecastPointRole Role, DateTimeOffset TargetUtc,
    double HorizonHours, string Model, double? RemainingPercent, double? ExpectedUsagePercent,
    double? LowerRemainingPercent, double? UpperRemainingPercent, int TrainingSamples,
    int ValidationSamples, int IntervalSamples, bool UsesWorkload, ForecastSupportReason SupportReasons,
    DateTimeOffset? ExhaustionAtUtc = null, bool? SurvivesUntilReset = null,
    CodexNumericInference? Inference = null);

/// <summary>Immutable computation inputs/outputs. The repository supplies the actual recording clock.</summary>
public sealed record QuotaForecastComputation(QuotaForecastComputationPhase Phase, QuotaHistoryCohort Cohort,
    DateTimeOffset AnchorUtc, DateTimeOffset? AnchorMeasuredUtc, DateTimeOffset? AnchorCollectedUtc,
    string? AnchorObservationId, double? AnchorUsedPercent, DateTimeOffset ResetUtc,
    DateTimeOffset GeneratedAtUtc, string PolicyVersion, string InputHash,
    QuotaPaceReceipt? PaceReceipt, IReadOnlyList<QuotaForecastComputedPoint> Points)
{
    public const string Version = "quota-forecast-computation/v1";
    public string Contract { get; init; } = Version;
}

public sealed record QuotaForecastComputationEntry(string Id, DateTimeOffset? RecordedAtUtc,
    QuotaForecastComputation? Computation, string? Problem, DateTimeOffset? ConfirmedAtUtc = null)
{
    public QuotaForecastRecordingTiming Timing(QuotaForecastComputedPoint point) =>
        Computation is not { } c || RecordedAtUtc is not { } started
            ? QuotaForecastRecordingTiming.InvalidClock
            : ConfirmedAtUtc is not { } at ? QuotaForecastRecordingTiming.Unconfirmed
            : at < started || c.GeneratedAtUtc > at || c.AnchorUtc > c.GeneratedAtUtc || c.AnchorCollectedUtc > at
            ? QuotaForecastRecordingTiming.InvalidClock
            : point.TargetUtc <= at ? QuotaForecastRecordingTiming.AtOrAfterTarget
            : QuotaForecastRecordingTiming.BeforeTarget;
}
