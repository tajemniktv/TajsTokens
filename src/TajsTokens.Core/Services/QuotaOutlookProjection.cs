using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed record AllowanceOutlook(
    QuotaWindowKind Kind, CurrentQuotaForecast? Reading, bool NotReported,
    bool Supported, bool Exhausted, string Status, string Conclusion, string Detail);

public sealed record AccountOutlook(IReadOnlyList<AllowanceOutlook> Windows, AllowanceOutlook? Limiting, string Summary);

/// <summary>Pure presentation of existing forecasts; never fits or extrapolates a new model.</summary>
public static class QuotaOutlookProjection
{
    public static string Name(QuotaWindowKind kind) => kind == QuotaWindowKind.Weekly ? "Weekly" : "5-hour";

    public static AccountOutlook Build(CodexIntelligenceSnapshot live, DateTimeOffset now)
    {
        AllowanceOutlook[] windows = new[] { QuotaWindowKind.FiveHour, QuotaWindowKind.Weekly }.Select(kind =>
        {
            CurrentQuotaForecast? reading = live.Current.Where(x => x.Current.Kind == kind)
                .MaxBy(x => x.Current.CapturedAtUtc);
            bool absent = live.CurrentState.QuotaLanes.Any(x => x.Kind == kind && x.NotReportedByProvider);
            return Window(kind, absent ? null : reading, absent, now);
        }).ToArray();
        if (live.Current.Select(x => x.Current.AccountKey).Distinct().Count() > 1)
            return new AccountOutlook(windows.Select(x => x with { Reading = null, Supported = false, Exhausted = false,
                NotReported = false, Status = "Select account", Conclusion = "Select one account",
                Detail = "Multiple accounts are present; windows are not borrowed across accounts." }).ToArray(), null,
                "Multiple account readings · select an account before comparing allowances");
        AllowanceOutlook? limiting = windows.Where(x => x.Supported)
            .OrderBy(x => x.Exhausted ? 0 : x.Reading?.Forecast?.SurvivesUntilReset == false ? 1 : 2)
            .ThenBy(x => !x.Exhausted && x.Reading?.Forecast?.SurvivesUntilReset == false
                ? x.Reading.Forecast.EstimatedExhaustionAtUtc ?? DateTimeOffset.MaxValue : DateTimeOffset.MaxValue)
            .ThenByDescending(x => !x.Exhausted && x.Reading?.Forecast?.SurvivesUntilReset == true
                ? x.Reading.Forecast.BurnPressure ?? double.NegativeInfinity : double.NegativeInfinity)
            .ThenBy(x => x.Reading?.Current.ResetsAtUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Kind).FirstOrDefault();
        AllowanceOutlook[] reported = windows.Where(x => !x.NotReported).ToArray();
        string summary = limiting?.Exhausted == true ? $"{Name(limiting.Kind)} allowance exhausted"
            : limiting?.Reading?.Forecast?.SurvivesUntilReset == false ? $"{Name(limiting.Kind)} allowance may run out first"
            : reported.Length > 0 && reported.All(x => x.Supported)
                ? reported.Length == 2 ? $"At this pace, both allowances last until reset · {Name(limiting!.Kind)} is tightest"
                    : $"At this pace, {Name(limiting!.Kind).ToLowerInvariant()} allowance lasts until reset"
                : "Reset outlook is incomplete";
        if (reported.Any(x => !x.Supported) || windows.Any(x => x.NotReported))
            summary += ". " + string.Join(" · ", windows.Where(x => !x.Supported || limiting?.Reading?.Forecast?.SurvivesUntilReset != false && reported.Any(r => !r.Supported)).Select(x => $"{Name(x.Kind)}: {x.Conclusion}"));
        return new AccountOutlook(windows, limiting, summary);
    }

    public static AllowanceOutlook Window(QuotaWindowKind kind, CurrentQuotaForecast? reading, bool absent, DateTimeOffset now)
    {
        AllowanceOutlook Unknown(string status, string detail) => new(kind, reading, absent, false, false, status, status, detail);
        if (absent) return Unknown("Not reported", "This window is absent from the current provider response.");
        if (reading is null) return Unknown("Meter unavailable", "No current reading for this account. Work filters do not allocate account quota.");
        QuotaSnapshot q = reading.Current;
        if (!reading.IsFresh || now - q.CapturedAtUtc > TimeSpan.FromHours(6) || q.CapturedAtUtc > now)
            return Unknown("Last known reading", "Current pace assessment is paused until a fresh provider reading is available.");
        if (q.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100 || q.RemainingPercent is not { } remaining)
            return Unknown("Meter unavailable", "The current meter is missing or invalid.");
        if (remaining <= 0)
            return new(kind, reading, false, true, true, "Exhausted", "Provider meter exhausted", "Observed allowance is exhausted; this is not a predicted ETA.");
        if (string.IsNullOrWhiteSpace(q.AccountKey))
            return Unknown("Account unknown", "The meter is visible, but compatible account history cannot be established.");
        if (q.ResetsAtUtc is not { } reset || reset <= now)
            return Unknown("Reset unavailable", "A future reported reset is needed for a reset outlook.");
        Forecast? f = reading.Forecast;
        if (f is not null && (f.Kind != kind || f.GeneratedAtUtc > now || f.GeneratedAtUtc < q.CapturedAtUtc ||
                f.Evidence?.PaceReceipt is { } receipt && (receipt.Cohort != QuotaHistoryPolicy.Cohort(q) || receipt.AnchorEffectiveAtUtc != q.CapturedAtUtc)))
            return Unknown("Forecast anchor changed", "The calculation does not match the current meter; a fresh anchored forecast is required.");
        if (f?.IsQuantizedFlat == true || f?.State == ForecastState.IdleWithinMeterPrecision)
            return Unknown("Below meter precision", "No movement visible at meter precision. Zero burn and survival until reset are not established.");
        if (f is null || f.State == ForecastState.Learning || f.SurvivesUntilReset is null)
            return Unknown("Pace not established", f?.Evidence?.UncertaintyDescription ?? reading.Diagnostic ??
                "More compatible observations are needed; a trained workload model is not required.");
        string conclusion = f.SurvivesUntilReset == false && f.EstimatedExhaustionAtUtc is { } eta
            ? $"At this pace, exhausted {eta.ToLocalTime():ddd HH:mm}"
            : f.ProjectedRemainingAtResetPercent is { } margin ? $"At this pace, ~{margin:0.#}% left at reset"
                : "Reset estimate unavailable";
        bool supported = f.SurvivesUntilReset == false && f.EstimatedExhaustionAtUtc is { } exhaustion && exhaustion < reset ||
                         f.SurvivesUntilReset == true && f.ProjectedRemainingAtResetPercent is not null;
        string band = f.Evidence is { RemainingAtResetLowerPercent: { } low, RemainingAtResetUpperPercent: { } high }
            ? $"Reset range {low:0.#}–{high:0.#}% · empirical 80%-target, not a guarantee."
            : "Reset range not yet supported; the pace estimate remains conditional.";
        string pace = f.BurnRatePercentPerHour is { } rate && f.SustainablePercentPerHour is { } sustainable
            ? $"{rate:0.#} pp/h · sustainable {sustainable:0.#} pp/h. " : "";
        string model = string.Join(" ", (f.Evidence?.HorizonPredictions ?? []).SelectMany(h => Describe(h.SupportReasons)).Distinct());
        return new(kind, reading, false, supported, false,
            !supported ? "Pace not established" : f.SurvivesUntilReset == false ? "At risk" :
            f.State == ForecastState.NearSustainablePace ? "Near limit" : "On track", conclusion, pace + band + " " + model);
    }

    public static IEnumerable<string> Describe(ForecastSupportReason reasons)
    {
        if (reasons.HasFlag(ForecastSupportReason.WorkloadFeaturesUnavailable)) yield return "Using recent pace; compatible workload evidence is unavailable.";
        if (reasons.HasFlag(ForecastSupportReason.ChallengerNotBetter)) yield return "Using recent pace; the workload model has not improved later predictions.";
        if (reasons.HasFlag(ForecastSupportReason.InsufficientHistory)) yield return "More compatible completed observations are needed.";
        if (reasons.HasFlag(ForecastSupportReason.InsufficientIntervalEvidence)) yield return "A calibrated range is not yet supported.";
    }
}
