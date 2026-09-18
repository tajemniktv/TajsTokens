namespace TajsTokens.Core.Models;

/// <summary>Rebuildable retrospective cost evidence, never a live forecast input.</summary>
public sealed record QuotaCostObservation(
    QuotaHistoryCohort Cohort, DateTimeOffset GenerationStartUtc, DateTimeOffset ResetUtc,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, double HorizonHours,
    double StartUsed, double EndUsed, double LowerDelta, double UpperDelta,
    string PrecisionPolicy, double PaceDelta,
    IReadOnlyList<double> TokenCategories, CodexForecastFeatures Features,
    double? MeanTtftMilliseconds, int RuntimeSamples,
    IReadOnlyList<string> QualityFlags)
{
    public DateTimeOffset? EvidenceAvailableAtUtc { get; init; }
    public DateTimeOffset? OriginCollectedAtUtc { get; init; }
    public DateTimeOffset? OriginEvidenceAvailableAtUtc { get; init; }
    public DateTimeOffset? OutcomeCollectedAtUtc { get; init; }
    public string? EffectiveAccountKey { get; init; }
    public QuotaAccountAttribution Attribution { get; init; }
    public string? AccountAssociationId { get; init; }
    public double ObservedDelta => EndUsed - StartUsed;
    public double IntervalLoss(double prediction) => Math.Max(0, Math.Max(LowerDelta - prediction, prediction - UpperDelta));
}
