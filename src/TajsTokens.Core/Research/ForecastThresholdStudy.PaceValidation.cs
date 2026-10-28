using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record PaceValidationScore(string Cohort, string Window, string Target, string Period, string LocalState,
    string ResetLead, string Partition, string Model, int CandidateOrigins, int PointOrigins, int OutcomeOrigins, int PairedOutcomes,
    int NonOverlappingOutcomes, int BandOrigins, int BandedOutcomes, int ZeroUsagePointOrigins, int MissedObservedMovement,
    double? MeanSignedError, double? MeanAbsoluteError, double? RootMeanSquaredError, double? IntervalCoverage, double? MeanIntervalWidth);
public sealed record PaceValidationReport(string Version, string DatasetIdentity, DateTimeOffset CapturedAtUtc,
    string ResetPolicy, string FixedHorizonPolicy, IReadOnlyList<PaceValidationScore> Scores, string Boundary);

public static partial class ForecastThresholdStudy
{
    private sealed record PacePoint(DateTimeOffset Origin, DateTimeOffset Reset, DateTimeOffset? Outcome,
        string Period, string State, string Lead, string Model, double? Prediction, double? Actual, double? Low, double? High,
        bool ZeroUsage, bool MissedMovement);

    public static PaceValidationReport ValidatePace(CodexForecastDataset data, CancellationToken token = default)
    {
        var scores = new List<PaceValidationScore>();
        string[] comparators = ["legacy-ewma", "time-ewma-2h", "time-ewma-6h", "recent-2h", "recent-6h", "persistence"];
        foreach (OriginInputs input in PrepareOrigins(data))
        {
            var features = new CodexForecastFeatureIndex(data);
            foreach (double? horizon in input.Cohort.Key.Kind == QuotaWindowKind.Weekly
                         ? new double?[] { 5d / 60, .25, .5, 2, 24, null } : [5d / 60, .25, .5, 2, null])
            {
                token.ThrowIfCancellationRequested();
                DateTimeOffset[] origins = input.Prefixes.Where(x => horizon is null || x.Key.AddHours(horizon.Value) <= x.Value[^1].ResetsAtUtc)
                    .Select(x => x.Key).Order().ToArray();
                if (origins.Length == 0) continue;
                DateTimeOffset split = origins[(int)Math.Floor(origins.Length * .8)];
                var labels = Labels(input.Epochs, origins, horizon);
                var resets = horizon is null ? QuotaPredictionService.ReplayResetOrigins(input.Quota, origins, token) : null;
                var points = new List<PacePoint>();
                foreach (DateTimeOffset origin in origins)
                {
                    token.ThrowIfCancellationRequested();
                    QuotaSnapshot[] prefix = input.Prefixes[origin];
                    QuotaSnapshot anchor = prefix[^1];
                    double lead = (anchor.ResetsAtUtc!.Value - origin).TotalHours;
                    string leadGroup = lead <= 2 ? "0–2h" : lead <= 24 ? "2–24h" : "over-24h";
                    var twoHours = features.Build(origin, ForecastReplayAvailability.CollectedByOrigin);
                    var halfHour = features.Build(origin, .5, ForecastReplayAvailability.CollectedByOrigin);
                    var tenMinutes = features.Build(origin, 1d / 6, ForecastReplayAvailability.CollectedByOrigin);
                    double priorRate = Math.Max(0, twoHours.Tokens - halfHour.Tokens) / 1.5;
                    string state = twoHours.ObservedTokenEvents == 0 ? "missing-local-evidence" :
                        tenMinutes.Tokens <= 0 ? "quiet-recorded-idle-unverified" :
                        priorRate > 0 && halfHour.Tokens / .5 > 2 * priorRate ? "observed-local-burst" : "observed-local-active";
                    labels.TryGetValue(origin, out QuotaForecastTrial? label);
                    // Short outcomes use a target-appropriate tolerance; do not accept a five-minute delay on a five-minute target.
                    if (label is not null && horizon is { } h &&
                        label.OutcomeUtc - origin.AddHours(h) > TimeSpan.FromMinutes(Math.Min(5, h * 60 * .2))) label = null;
                    string period = origin >= split ? "final" : "development";
                    if (period == "development" && label?.OutcomeUtc >= split) label = null;
                    bool flat = prefix.All(x => x.UsedPercent == anchor.UsedPercent);
                    foreach (string model in comparators)
                    {
                        double? prediction = horizon is null && flat ? null : QuotaPaceModels.ProjectRemaining(prefix, model, horizon ?? lead);
                        points.Add(new(origin, anchor.ResetsAtUtc.Value, label?.OutcomeUtc, period, state, leadGroup,
                            model, prediction, label?.ObservedRemaining, null, null,
                            prediction is not null && prediction == anchor.RemainingPercent,
                            prediction == anchor.RemainingPercent && label?.ObservedRemaining < anchor.RemainingPercent));
                    }
                    if (resets is not null)
                    {
                        Forecast f = resets[origin];
                        bool supported = f.State is not ForecastState.Learning and not ForecastState.IdleWithinMeterPrecision;
                        points.Add(new(origin, anchor.ResetsAtUtc.Value, label?.OutcomeUtc, period, state, leadGroup, "production-reset",
                            supported ? f.ProjectedRemainingAtResetPercent : null, label?.ObservedRemaining,
                            supported ? f.Evidence?.RemainingAtResetLowerPercent : null, supported ? f.Evidence?.RemainingAtResetUpperPercent : null,
                            supported && f.ProjectedRemainingAtResetPercent == anchor.RemainingPercent,
                            supported && f.ProjectedRemainingAtResetPercent == anchor.RemainingPercent && label?.ObservedRemaining < anchor.RemainingPercent));
                    }
                }
                foreach (var group in points.GroupBy(x => (x.Period, x.State, x.Lead, x.Model)).Concat(
                             points.GroupBy(x => (x.Period, State: "all-local-states", Lead: "all-reset-leads", x.Model))))
                {
                    var predicted = group.Where(x => x.Prediction is not null).ToArray();
                    var paired = predicted.Where(x => x.Actual is not null).ToArray();
                    var bands = paired.Where(x => x.Low is not null && x.High is not null).ToArray();
                    double? Mean(IEnumerable<double> values) { double[] rows = values.ToArray(); return rows.Length == 0 ? null : rows.Average(); }
                    double? mse = Mean(paired.Select(x => Math.Pow(x.Prediction!.Value - x.Actual!.Value, 2)));
                    scores.Add(new(input.Alias, input.Cohort.Key.Kind.ToString(), horizon is { } hours
                            ? FormattableString.Invariant($"{hours:g}h") : "near-reset-proxy", group.Key.Period, group.Key.State, group.Key.Lead,
                        group.Key.State == "all-local-states" ? "overall" : "state-reset-lead", group.Key.Model, group.Count(), predicted.Length, group.Count(x => x.Actual is not null), paired.Length,
                        ChronologicalEvidence.Blocks(paired, x => x.Origin, x => x.Outcome!.Value, x => x.Reset).Sum(x => x.Count),
                        predicted.Count(x => x.Low is not null && x.High is not null), bands.Length, predicted.Count(x => x.ZeroUsage), paired.Count(x => x.MissedMovement),
                        Mean(paired.Select(x => x.Prediction!.Value - x.Actual!.Value)),
                        Mean(paired.Select(x => Math.Abs(x.Prediction!.Value - x.Actual!.Value))),
                        mse is { } value ? Math.Sqrt(value) : null,
                        Mean(bands.Select(x => x.Actual >= x.Low && x.Actual <= x.High ? 1d : 0d)), Mean(bands.Select(x => x.High!.Value - x.Low!.Value))));
                }
            }
        }
        return new("quota-pace-validation/v1", Fingerprint(data), data.CapturedAtUtc,
            QuotaPredictionService.ResetOutlookPolicyVersion, QuotaPredictionService.PolicyVersion, scores,
            "Read-only collection-qualified chronological comparison; final fifth of candidate origins is later-period evaluation, never used to choose a new window. " +
            "Fixed-horizon rows compare predefined arithmetic pace candidates, not the full workload/composed selector. Production-reset rows replay the actual repaired reset selector. " +
            "Origins use the existing 30-minute sampling after 15 minutes of compatible history; near-reset labels are within-five-minute proxies. " +
            "Fixed target tolerance is min(5 minutes, 20% of horizon); missing targets remain unavailable. " +
            "Errors are displayed remaining percentage points; positive signed error is optimistic. Counts and non-overlapping support remain separate by cohort/window/target/state/reset lead. " +
            "State is origin-time installation-local co-observation, never account attribution or proven idleness; burst means 30-minute token rate above twice the preceding 90-minute rate, with positive recent activity. " +
            "Reset-lead bins are 0–2h, 2–24h and over 24h. Neither those bins nor the two-hour fallback are universal scientific constants. " +
            "Point/band availability shares candidate denominators; band coverage uses banded matched outcomes only. Reset bands still require earlier completed compatible cycles. " +
            "Overall rows recompute non-overlapping support across the entire cohort; do not sum support across state/lead partitions or add overall rows to their partitions. " +
            "Legacy EWMA is a research comparator only; no synthetic rate target, probability, threshold relaxation or automatic model promotion.");
    }
}
