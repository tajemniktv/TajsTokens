using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed class ForecastingService : IForecastingService
{
    public const string PolicyVersion = "quota-walk-forward/v2";

    public Forecast BuildForecast(IReadOnlyList<QuotaSnapshot> snapshots, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var eligible = snapshots.Where(x => x.CapturedAtUtc <= nowUtc).OrderBy(x => x.CapturedAtUtc).ToArray();
        if (eligible.Length == 0)
            throw new ArgumentException("At least one snapshot must be captured at or before the forecast time.", nameof(snapshots));
        var latest = eligible[^1];
        if (eligible.Any(x => x.Kind != latest.Kind || x.Provider != latest.Provider || x.Profile != latest.Profile))
            throw new ArgumentException("Forecast snapshots must belong to one quota window/provider/profile.", nameof(snapshots));
        var model = QuotaForecastBacktester.DefaultModel(latest);
        ForecastEvidence Evidence(int count, double hours, string description) => new(PolicyVersion, model,
            latest.Source, count, hours, 0, null, null, null, null, description);
        Forecast Unknown(string reason, double? sustainable = null) => new(latest.Kind, nowUtc, null, null,
            null, sustainable, 0, ForecastState.Learning, Evidence: Evidence(0, 0, reason));
        if (latest.UsedPercent is not double used || !double.IsFinite(used) || used is < 0 or > 100)
            return Unknown("The current meter is missing or invalid.");
        if (latest.ResetsAtUtc is not DateTimeOffset reset || reset <= nowUtc)
            return Unknown("A future authoritative reset is required; expired generations are not extended.");
        if (nowUtc - latest.CapturedAtUtc > TimeSpan.FromHours(6))
            return Unknown("The anchor is too old for a current pace projection.");
        var remaining = latest.RemainingPercent!.Value;
        var hoursToReset = (reset - nowUtc).TotalHours;
        var sustainable = remaining / hoursToReset;

        // A rate never bridges distinct source lanes. Drops/invalid values/metadata changes
        // terminate the whole segment. Reused reset identities cannot resurrect old slopes.
        var stream = eligible.Where(x => x.Source == latest.Source).ToArray();
        var epoch = QuotaForecastBacktester.SplitEpochs(stream).LastOrDefault();
        if (epoch is null || epoch[^1] != latest) return Unknown("No unambiguous current segment is available.", sustainable);
        var observedHours = (latest.CapturedAtUtc - epoch[0].CapturedAtUtc).TotalHours;
        var evidence = Evidence(epoch.Count, observedHours,
            "Conditional pace projection, not an exhaustion probability. Too few completed same-source reset generations at comparable lead to calibrate uncertainty.");
        if (remaining <= 0)
            return new Forecast(latest.Kind, nowUtc, null, latest.CapturedAtUtc, false, 0, 0,
                ForecastState.ExhaustionLikelyBeforeReset, ProjectedRemainingAtResetPercent: 0,
                Evidence: evidence with { UncertaintyDescription = "The current provider meter is already exhausted; this is not a predicted future ETA." });
        if (epoch.Count < 2 || observedHours < 0.25)
            return new Forecast(latest.Kind, nowUtc, null, null, null, sustainable, 0, ForecastState.Learning,
                Evidence: evidence with { UncertaintyDescription = "Learning: at least 15 minutes of compatible same-source observations are needed. Poll count alone is not independent evidence." });
        var flat = epoch.All(x => Math.Abs(x.UsedPercent!.Value - used) < 0.000001);
        if (flat)
            return new Forecast(latest.Kind, nowUtc, null, null, null, sustainable, 0,
                ForecastState.IdleWithinMeterPrecision, Trend: "flat within meter precision", IsQuantizedFlat: true,
                Evidence: evidence with { UncertaintyDescription = "No movement is visible at meter precision. Neither exact zero burn nor survival until reset is established." });

        var lead = (reset - latest.CapturedAtUtc).TotalHours;
        var candidates = QuotaPaceModels.Candidates.ToDictionary(candidate => candidate,
            candidate => QuotaForecastBacktester.Replay(stream, candidate, null));
        model = QuotaForecastBacktester.SelectModel(candidates, latest.CapturedAtUtc, reset, lead, model);
        var rate = QuotaPaceModels.Estimate(epoch, model);
        var elapsed = Math.Max(0, (nowUtc - latest.CapturedAtUtc).TotalHours);
        var remainingNow = QuotaPaceModels.ProjectRemaining(epoch, model, elapsed);
        sustainable = remainingNow / hoursToReset;
        var pressure = sustainable > 0 ? rate / sustainable : (double?)null;
        var margin = QuotaPaceModels.ProjectRemaining(epoch, model, lead);
        var survives = margin > 0 || rate <= 0;
        DateTimeOffset? eta = null;
        if (!survives && rate > 0)
        {
            var etaHours = remaining / rate;
            var decay = model switch { "damped-2h" => 2d, "damped-6h" => 6d, _ => 0d };
            if (decay > 0) etaHours = -decay * Math.Log(1 - etaHours / decay);
            if (double.IsFinite(etaHours) && etaHours < lead) eta = latest.CapturedAtUtc.AddHours(etaHours);
            else survives = true; // Exhaustion exactly at reset is not before reset.
        }
        // Calibrate the actual prequential selection policy, not its in-sample winning candidate.
        var adaptiveTrials = QuotaForecastBacktester.ReplayAdaptive(candidates, QuotaForecastBacktester.DefaultModel(latest));
        var errors = QuotaForecastBacktester.CalibrationErrors(adaptiveTrials, latest.CapturedAtUtc, reset, lead);
        var radius = QuotaForecastBacktester.ErrorRadius(errors);
        evidence = evidence with
        {
            Model = model,
            CalibrationEpochs = errors.Count,
            HistoricalAbsoluteErrorPercent = errors.Count > 0 ? errors.Average() : null,
            RemainingAtResetLowerPercent = radius is double r ? Math.Max(0, margin - r) : null,
            RemainingAtResetUpperPercent = radius is double r2 ? Math.Min(100, margin + r2) : null,
            NominalIntervalCoverage = radius is not null ? QuotaForecastBacktester.NominalCoverage : null,
            UncertaintyDescription = radius is not null
                ? "Empirical 80%-target band from prior completed same-source generations at similar lead; one error per generation. Outcomes are within 5 minutes of reset, not exact reset truth. Changing workload can change coverage. This is not an exhaustion probability."
                : evidence.UncertaintyDescription
        };
        var state = !survives ? ForecastState.ExhaustionLikelyBeforeReset : pressure >= 0.85
            ? ForecastState.NearSustainablePace : ForecastState.SafeUntilReset;
        var split = Math.Max(1, epoch.Count / 2);
        var earlier = QuotaPaceModels.Estimate(epoch.Take(split + 1).ToArray(), "epoch");
        var recent = QuotaPaceModels.Estimate(epoch.Skip(split).ToArray(), "epoch");
        var trend = epoch.Count < 4 ? "insufficient trend history" : recent > earlier * 1.25 ? "accelerating"
            : recent < earlier * 0.75 ? "slowing" : "stable";
        return new Forecast(latest.Kind, nowUtc, rate, eta, survives, sustainable, 0, state,
            pressure, margin, trend, false, evidence);
    }
}
