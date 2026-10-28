using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Uses existing continuation, reference scoring and live pace owners. No fitting or promotion.</summary>
public static class RecentWorkPlanBuilder
{
    public static void Validate(RecentWorkPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.DurationHours) || request.DurationHours <= 0 || request.DurationHours > 168 ||
            request.ContextHours is not (.5 or 2)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Model?.Length > 256 || request.ReasoningEffort?.Length > 128) throw new ArgumentException("Planning label is too long.", nameof(request));
    }

    public static RecentWorkPlan Build(CodexForecastDataset data, CodexIntelligenceSnapshot live,
        RecentWorkPlanRequest request, WorkloadReferenceState references, DateTimeOffset now)
    {
        Validate(request);
        request = request with { Model = Optional(request.Model), ReasoningEffort = Optional(request.ReasoningEffort) };
        var continuation = WorkloadCompositionPrediction.Continue(data, now, request.ContextHours, request.DurationHours);
        var joint = continuation.Workload?.JointWorkload;
        bool edits = (request.Model is not null || request.ReasoningEffort is not null) &&
            (joint is null || joint.Overflow?.ReportedTotal > 0 || joint.Cells.Any(x =>
                request.Model is not null && x.Coordinate!.Model != request.Model ||
                request.ReasoningEffort is not null && x.Coordinate!.ReasoningEffort != request.ReasoningEffort));
        WorkloadReferenceResult? reference = joint is null ? null : WorkloadReferenceProjection.BuildConditional(references, joint, now);
        var revalued = edits && joint is not null ? Revalue(joint, request) : null;
        WorkloadReferenceResult? revaluation = revalued is null ? null : WorkloadReferenceProjection.BuildConditional(references, revalued, now);
        string editStatus = edits
            ? revalued is null ? "Reference edit unavailable · coordinate overflow or missing joint work. Task/quota prediction for edits is unsupported."
                : "Same-token reference revaluation only · changed-model task and quota predictions are unsupported."
            : "Recent native mixture · no model or effort multiplier.";
        CurrentQuotaForecast[] readings = live.Current.Where(x => request.AccountKey is null || x.Current.AccountKey == request.AccountKey).ToArray();
        string?[] accounts = readings.Select(x => x.Current.AccountKey).Distinct().ToArray();
        bool oneAccount = accounts.Length == 1 && !string.IsNullOrWhiteSpace(accounts[0]);
        var windows = new[] { QuotaWindowKind.FiveHour, QuotaWindowKind.Weekly }.Select(kind =>
        {
            var lanes = readings.Where(x => x.Current.Kind == kind).ToArray();
            CurrentQuotaForecast? reading = lanes.Length == 1 ? lanes[0] : null;
            bool absent = live.CurrentState.QuotaLanes.Any(x => x.Kind == kind && x.NotReportedByProvider &&
                (request.AccountKey is null || x.Snapshot?.AccountKey == request.AccountKey ||
                    x.Snapshot is null && readings.Length > 0));
            ScenarioWindowEstimate Unknown(string why) => new(kind, false, 0, null, null, null, 0, why);
            ScenarioWindowEstimate pace = absent ? Unknown("Codex did not report this allowance.")
                : !oneAccount ? Unknown("Select one known account for quota fit. Local work remains installation-wide.")
                : edits ? Unknown("Quota fit for model/effort edits is unsupported; reference revaluation does not predict task usage.")
                : Pace(reading, live.CurrentState, request.DurationHours, now, Unknown);
            var assessment = absent ? new SessionPlanWindow(SessionFit.Unknown, "Not reported", pace.Explanation)
                : !pace.HasEnoughHistory ? new SessionPlanWindow(SessionFit.Unknown, "Fit unavailable", pace.Explanation)
                : SessionPlanProjection.Build(pace, reading, request.DurationHours, now);
            return new RecentWorkPlanWindow(kind, pace, reading, assessment);
        }).ToArray();
        return new(now, request, continuation, reference, revaluation, edits, editStatus, Array.AsReadOnly(windows));
    }

    private static ScenarioWindowEstimate Pace(CurrentQuotaForecast? reading, CodexCurrentState state,
        double hours, DateTimeOffset now, Func<string, ScenarioWindowEstimate> unknown)
    {
        if (reading is null) return unknown("One matching provider meter is required for this window.");
        var anchor = reading.Current;
        if (!reading.IsFresh || !state.IsQuotaSnapshotFresh(anchor) || anchor.Authority != QuotaObservationAuthority.ProviderAuthoritative ||
            anchor.CapturedAtUtc > now || now - anchor.CapturedAtUtc > TimeSpan.FromHours(6))
            return unknown("A fresh provider meter is required for this window.");
        if (anchor.ResetsAtUtc is not { } reset || reset <= now) return unknown("A future reported reset is required.");
        var forecast = reading.Forecast;
        var receipt = forecast?.Evidence?.PaceReceipt;
        if (forecast is null || forecast.IsQuantizedFlat || forecast.State == ForecastState.IdleWithinMeterPrecision ||
            receipt is not { CalculationSupported: true, RatePercentPerHour: { } rate } || !double.IsFinite(rate) || rate <= 0 ||
            receipt.Contract != QuotaPaceModels.CalculationVersion || !QuotaPaceModels.IsProductionEligible(receipt.Estimator) ||
            receipt.DenominatorHours is not > 0 || receipt.Intervals < 1 || receipt.ToUtc > now || receipt.GeneratedAtUtc > now ||
            receipt.Cohort != QuotaHistoryPolicy.Cohort(anchor) || receipt.AnchorEffectiveAtUtc != anchor.CapturedAtUtc ||
            forecast.GeneratedAtUtc > now || forecast.GeneratedAtUtc < anchor.CapturedAtUtc)
            return unknown("Recent account pace is not established; flat readings do not prove zero burn.");
        return new(anchor.Kind, true, receipt.Intervals, rate * hours, null, null, 0,
            "At the current account pace, held constant for this duration. Separate from local work and TT; uncalibrated for this requested horizon.")
            { Cohort = receipt.Cohort, SupportReasons = ForecastSupportReason.InsufficientIntervalEvidence };
    }

    private static CodexJointWorkload? Revalue(CodexJointWorkload joint, RecentWorkPlanRequest request)
    {
        if (joint.Overflow?.ReportedTotal > 0) return null;
        var cells = joint.Cells.GroupBy(x => x.Coordinate! with { Model = request.Model ?? x.Coordinate!.Model,
            ReasoningEffort = request.ReasoningEffort ?? x.Coordinate!.ReasoningEffort }).Select(g => new CodexJointWorkloadCell(g.Key,
                g.Sum(x => x.UncachedInput), g.Sum(x => x.CacheRead), g.Sum(x => x.CacheWrite), g.Sum(x => x.NonReasoningOutput),
                g.Sum(x => x.ReasoningOutput), g.Sum(x => x.ReportedTotal), 0, 0, 0)).ToArray();
        return new(CodexJointWorkload.Version, CodexJointQuantityKind.ConditionalProjection, cells, null,
            cells.Where(x => x.Coordinate!.Model is null).Sum(x => x.ReportedTotal),
            cells.Where(x => x.Coordinate!.ReasoningEffort is null).Sum(x => x.ReportedTotal));
    }

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
