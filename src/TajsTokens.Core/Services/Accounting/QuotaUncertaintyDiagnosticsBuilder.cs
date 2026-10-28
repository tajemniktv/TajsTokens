using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class QuotaUncertaintyDiagnosticsBuilder
{
    public const string Version = "quota-uncertainty-diagnostics/v1";
    public const string Methodology = "Measurement envelopes, conditional identification ranges and predictive residual ranges are different targets. " +
        "For point p and declared measurement envelope [L,U], minimum absolute error is distance(p,[L,U]); maximum is max(|p-L|,|p-U|). " +
        "Those are bounds on realized point error only if the latent outcome belongs to its declared envelope. " +
        "For reported range P, containment means [L,U] is inside P; intersection means P overlaps [L,U]. " +
        "On the same valid range samples, containment rate <= realized empirical latent coverage <= intersection rate, conditional on every latent outcome belonging to its declared envelope. " +
        "This is not future predictive calibration or survival probability. Mean widths and counts are separate; missing ranges are not failures or zero-width certainty. " +
        "Unknown reporting lag, account attribution and model mismatch are not removed by rounding envelopes. Invalid or one-sided ranges are excluded and counted. " +
        "The existing residual-range construction/80% envelope-intersection target remains unchanged and does not establish 80% latent coverage.";

    public static QuotaUncertaintyDiagnostics Build(IReadOnlyList<QuotaCostTrial> trials,
        QuotaUncertaintyKind reportedRangeKind = QuotaUncertaintyKind.PredictiveResidualRange)
    {
        if (reportedRangeKind is not (QuotaUncertaintyKind.PredictiveResidualRange or QuotaUncertaintyKind.ConditionalResampledPathRange))
            throw new ArgumentOutOfRangeException(nameof(reportedRangeKind));
        var points = trials.Where(x => double.IsFinite(x.Prediction) && double.IsFinite(x.LowerDelta) &&
            double.IsFinite(x.UpperDelta) && x.LowerDelta <= x.UpperDelta).ToArray();
        var requested = trials.Where(x => x.LowerPrediction is not null || x.UpperPrediction is not null).ToArray();
        var bands = points.Where(x => x.LowerPrediction is { } lower && double.IsFinite(lower) &&
            x.UpperPrediction is { } upper && double.IsFinite(upper) && lower <= upper).ToArray();
        double? Mean(IEnumerable<double> values) { var array = values.ToArray(); return array.Length == 0 ? null : array.Average(); }
        return new(points.Length, trials.Count - points.Length, bands.Length, requested.Length - bands.Length,
            Mean(points.Select(x => x.UpperDelta - x.LowerDelta)),
            Mean(points.Select(x => Math.Max(0, Math.Max(x.LowerDelta - x.Prediction, x.Prediction - x.UpperDelta)))),
            Mean(points.Select(x => Math.Max(Math.Abs(x.Prediction - x.LowerDelta), Math.Abs(x.Prediction - x.UpperDelta)))),
            Mean(bands.Select(x => x.UpperPrediction!.Value - x.LowerPrediction!.Value)),
            Mean(bands.Select(x => x.UpperPrediction >= x.LowerDelta && x.LowerPrediction <= x.UpperDelta ? 1d : 0d)),
            Mean(bands.Select(x => x.LowerPrediction <= x.LowerDelta && x.UpperPrediction >= x.UpperDelta ? 1d : 0d)), Methodology +
                (reportedRangeKind == QuotaUncertaintyKind.ConditionalResampledPathRange ? " Raw finite-window path quantiles remain model-conditional; no predictive calibration is inferred." : ""))
            { ReportedRangeKind = reportedRangeKind };
    }
}
