namespace TajsTokens.Core.Models;

/// <summary>Closed, replayable numeric calculation provenance; not a probability or new provider observation.</summary>
public sealed record QuotaPaceReceipt(string Contract, string Estimator, string Fallback, string SelectionReason,
    QuotaHistoryCohort Cohort, string? AnchorObservationId, DateTimeOffset AnchorEffectiveAtUtc,
    DateTimeOffset? AnchorMeasuredAtUtc, DateTimeOffset? AnchorCollectedAtUtc, DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Observations, int Intervals,
    int UnknownCollectionObservations, int LaterCollectedObservations, bool CalculationSupported,
    double? NumeratorPercent, double? DenominatorHours, double? RatePercentPerHour,
    double? ReportedRemainingPercent, double? ClockHoursToReset, double? SustainablePercentPerHour, string Coverage)
{
    public string Describe() => $"Pace receipt · {Contract}\n{Estimator} · fallback {Fallback}\n{SelectionReason}\n" +
        $"Observed interval {FromUtc:O} → {ToUtc:O}; {Observations} readings / {Intervals} intervals.\n" +
        (NumeratorPercent is { } numerator && DenominatorHours is > 0
            ? $"{numerator:G17} weighted pp / {DenominatorHours:G17} exposure hours = {RatePercentPerHour:G17} pp/h.\n"
            : $"No measured rate quotient ({(CalculationSupported ? "persistence point" : "calculation withheld")}).\n") +
        $"Anchor observed {AnchorMeasuredAtUtc?.ToString("O") ?? "unknown"}; collected {AnchorCollectedAtUtc?.ToString("O") ?? "unknown"}; effective {AnchorEffectiveAtUtc:O}.\n" +
        (ReportedRemainingPercent is not null && ClockHoursToReset is > 0
            ? $"Budget: reported {ReportedRemainingPercent:G17}% / {ClockHoursToReset:G17} clock hours = {SustainablePercentPerHour:G17} pp/h.\n"
            : "Budget unavailable: valid remaining allowance and a future reset are required.\n") +
        $"{UnknownCollectionObservations} unknown collection times; {LaterCollectedObservations} collected after measurement. {Coverage}";
}
