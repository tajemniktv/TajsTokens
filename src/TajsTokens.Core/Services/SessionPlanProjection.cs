using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public enum SessionFit { Unknown, Fits, Exceeds, Uncertain }
public sealed record SessionPlanWindow(SessionFit Fit, string Summary, string Detail);

public static class SessionPlanProjection
{
    public static SessionPlanWindow Build(ScenarioWindowEstimate estimate, CurrentQuotaForecast? reading,
        double durationHours, DateTimeOffset now)
    {
        SessionPlanWindow Unknown(string detail) => new(SessionFit.Unknown, "Fit uncertain", detail);
        if (reading is null || !reading.IsFresh || reading.Current.CapturedAtUtc > now ||
            now - reading.Current.CapturedAtUtc > TimeSpan.FromHours(6)) return Unknown("A fresh account meter is required.");
        QuotaSnapshot anchor = reading.Current;
        if (anchor.AccountKey is null || estimate.Cohort is null || QuotaHistoryPolicy.Cohort(anchor) != estimate.Cohort)
            return Unknown("The estimate does not match this meter's account, source, plan and window.");
        if (!estimate.HasEnoughHistory || estimate.ExpectedQuotaDeltaPercent is not { } delta || !double.IsFinite(delta))
            return Unknown(estimate.Explanation);
        if (anchor.UsedPercent is not >= 0 or > 100 || anchor.RemainingPercent is not { } left || !double.IsFinite(left))
            return Unknown("Remaining allowance is unavailable.");
        string consumption = $"~{delta:0.#} pp expected consumption · ~{Math.Max(0, left - delta):0.#}% left after the session. ";
        if (anchor.ResetsAtUtc is not { } reset || reset <= now) return Unknown("A future reported reset is required.");
        if (!double.IsFinite(durationHours) || durationHours <= 0) return Unknown("Enter a positive finite duration.");
        if (now.AddHours(durationHours) >= reset)
            return Unknown($"~{delta:0.#} pp expected consumption. Reset occurs during this session ({reset.ToLocalTime():g}); current-window fit is not established.");
        bool band = estimate.LowerQuotaDeltaPercent is not null && estimate.UpperQuotaDeltaPercent is not null;
        SessionFit fit = band
            ? estimate.LowerQuotaDeltaPercent > left ? SessionFit.Exceeds : estimate.UpperQuotaDeltaPercent < left ? SessionFit.Fits : SessionFit.Uncertain
            : delta < left ? SessionFit.Fits : delta > left ? SessionFit.Exceeds : SessionFit.Uncertain;
        string uncertainty = band
            ? $"Consumption range {estimate.LowerQuotaDeltaPercent:0.#}–{estimate.UpperQuotaDeltaPercent:0.#} pp; remaining range {Math.Max(0, left - estimate.UpperQuotaDeltaPercent!.Value):0.#}–{Math.Max(0, left - estimate.LowerQuotaDeltaPercent!.Value):0.#}%. Empirical 80%-target, not a guarantee."
            : "Conditional point estimate · a calibrated range is not yet supported.";
        return new SessionPlanWindow(fit, fit == SessionFit.Fits ? "Estimated to fit" : fit == SessionFit.Exceeds ? "Estimated to exceed" : "Fit uncertain",
            consumption + uncertainty + "\n" + estimate.Explanation);
    }
}

/// <summary>Page-owned revision: input edits, quota changes and unloading invalidate asynchronous results and autofill.</summary>
public sealed class PlannerRevision
{
    public long Value { get; private set; }
    public long Invalidate() => ++Value;
    public bool IsCurrent(long captured) => captured == Value;
}
