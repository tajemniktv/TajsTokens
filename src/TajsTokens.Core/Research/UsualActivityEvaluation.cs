using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record UsualActivityOrigin(DateTimeOffset OriginUtc, DateTimeOffset EndUtc, double HorizonHours,
    string Period, int AgeGroup, double ObservedTokens, int TrainingOrigins, int ActiveTrainingOrigins,
    double Probability, double GlobalProbability, bool ActivityGatePassed, double? ConditionalTokens,
    double? RecentPositiveTokens, double? ConditionalLow, double? ConditionalHigh, double? Continue30m,
    double? Continue2h, bool EarlierTokenClocksAvailable, bool? PositiveTargetClocksTimely);

public sealed record UsualActivityScore(double HorizonHours, string Period, string Candidate, int TargetOrigins,
    int ActivityOrigins, double? Brier, double? CalibrationError, int GatePassedActivityOrigins,
    double? GatePassedBrier, double? GatePassedGlobalBrier, int ExpectedOrigins, double? ExpectedMae,
    double? ExpectedRmse, int MatchedOrigins, double? MatchedMae, double? MatchedWape, double? MatchedSignedSum,
    int Blocks, int ConditionalOrigins, double? ConditionalMae, int PositiveRangeOrigins,
    double? PositiveRangeCoverage, double? PositiveRangeWidth);

public sealed record UsualActivityCoverage(double HorizonHours, string Period, int Origins,
    int EarlierTokenClocksAvailable, int PositiveTargets, int PositiveTargetsWithTimelyClocks, int QuietTargets);

public sealed record UsualActivityReport(string Version, string EvidenceIdentity, DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc, DateTimeOffset EvidenceAsOfUtc, DateTimeOffset SplitUtc, int InputEvents,
    int UsedPositiveEvents, int UnknownCollectionEvents, int InvalidCollectionEvents,
    IReadOnlyList<UsualActivityCoverage> Coverage, IReadOnlyList<UsualActivityScore> Scores,
    IReadOnlyList<UsualActivityOrigin> Origins, string Boundary);

/// <summary>Explicit comparison of existing recorded-activity/amount owners; no live selection.</summary>
public static class UsualActivityEvaluation
{
    public const string Version = "usual-recorded-activity-evaluation/v1";
    public static IReadOnlyList<double> Horizons { get; } = Array.AsReadOnly(new[] { 5d / 60, .25, .5, 2d });
    public static IReadOnlyList<string> Candidates { get; } = Array.AsReadOnly(new[]
        { "age-group-hurdle", "global-activity-control", "recent-positive-hurdle", "continue-30m", "continue-2h", "zero-work" });

    public static UsualActivityReport Evaluate(CodexForecastDataset data, DateTimeOffset from, DateTimeOffset through,
        CancellationToken token = default, bool includeOrigins = false)
    {
        if (from >= through || through > data.CapturedAtUtc || through - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Choose an ordered past target range up to 366 days within the evidence snapshot.");
        long span = (through - from).Ticks;
        var split = from.AddTicks(span / 5 * 4 + span % 5 * 4 / 5);
        var retained = data.Tokens.Where(t => t.ObservedAtUtc <= through &&
            (t.CapturedAtUtc is null || t.CapturedAtUtc <= data.CapturedAtUtc)).ToArray();
        var positive = retained.Where(t => t.ReportedTotalTokens > 0).OrderBy(t => t.ObservedAtUtc).ToArray();
        var source = data with { Tokens = retained, Quota = [], Workload = [], Context = [], AccountAssociations = [], Ledger = [] };
        var report = SessionWorkloadPredictionService.ReconstructHorizons(source, through, Horizons, token);
        var index = new CodexForecastFeatureIndex(source);
        var origins = new List<UsualActivityOrigin>();
        var times = positive.Select(t => t.ObservedAtUtc).ToArray();
        var maxCollection = new DateTimeOffset[positive.Length + 1];
        var invalid = new int[positive.Length + 1];
        for (int i = 0; i < positive.Length; i++)
        {
            var t = positive[i];
            invalid[i + 1] = invalid[i] + (t.CapturedAtUtc is null || t.CapturedAtUtc < t.ObservedAtUtc ? 1 : 0);
            maxCollection[i + 1] = t.CapturedAtUtc is { } c && c > maxCollection[i] ? c : maxCollection[i];
        }
        int Through(DateTimeOffset at)
        {
            int lo = 0, hi = times.Length;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (times[mid] <= at) lo = mid + 1; else hi = mid; }
            return lo;
        }
        foreach (var t in report.Trials.Where(t => t.OriginUtc >= from))
        {
            token.ThrowIfCancellationRequested();
            int end = Through(t.OriginUtc), outcome = Through(t.EndUtc);
            bool? timely = outcome == end ? null : invalid[outcome] == invalid[end] &&
                positive.Skip(end).Take(outcome - end).All(e => e.CapturedAtUtc <= t.EndUtc.AddMinutes(5));
            double? context = WorkloadCompositionPrediction.Continue(index, t.OriginUtc, 2, t.Prediction.HorizonHours,
                ForecastReplayAvailability.ReconstructedEventTime).Receipt.ProjectedTokens;
            double? shortContext = WorkloadCompositionPrediction.Continue(index, t.OriginUtc, .5, t.Prediction.HorizonHours,
                ForecastReplayAvailability.ReconstructedEventTime).Receipt.ProjectedTokens;
            origins.Add(new(t.OriginUtc, t.EndUtc, t.Prediction.HorizonHours,
                t.OriginUtc >= split ? "final" : t.EndUtc <= split ? "development" : "boundary", t.AgeGroup,
                t.ObservedTokens, t.Prediction.TrainingOrigins, t.Prediction.ActiveTrainingOrigins,
                t.Prediction.RecordedActivityProbability, t.BaselineProbability, t.Prediction.ActivityEstimateSupported,
                t.Prediction.ConditionalMeanTokens, t.RecentPositiveMeanTokens, t.Prediction.ConditionalLowTokens,
                t.Prediction.ConditionalHighTokens, shortContext, context,
                invalid[end] == 0 && maxCollection[end] <= t.OriginUtc, timely));
        }
        var coverage = new List<UsualActivityCoverage>(); var scores = new List<UsualActivityScore>();
        foreach (double horizon in Horizons)
        foreach (string period in new[] { "development", "final" })
        {
            var rows = origins.Where(t => t.HorizonHours == horizon && t.Period == period).ToArray();
            var matched = rows.Where(t => Candidates.All(c => Point(t, c).Expected is not null)).ToArray();
            coverage.Add(new(horizon, period, rows.Length, rows.Count(t => t.EarlierTokenClocksAvailable),
                rows.Count(t => t.ObservedTokens > 0), rows.Count(t => t.PositiveTargetClocksTimely == true),
                rows.Count(t => t.ObservedTokens == 0)));
            foreach (string candidate in Candidates) scores.Add(Score(rows, matched, candidate, horizon, period, through));
        }
        return new(Version, ForecastThresholdStudy.Fingerprint(source), from, through, data.CapturedAtUtc, split,
            data.Tokens.Count, positive.Length, retained.Count(t => t.CapturedAtUtc is null),
            retained.Count(t => t.CapturedAtUtc < t.ObservedAtUtc), coverage, scores, includeOrigins ? origins : [],
            "Explicit reconstructed recorded-local-work research, not collection-time replay, personal presence or account-wide activity. " +
            "Origins use the existing under-two-hour token-age grid and 20 matured-target training floor; long idle gaps and initial training are outside this frame. " +
            "Targets include quiet tails through the fixed target cutoff. Unknown/delayed collection and quiet intervals have no independent completeness guarantee; clock checks are necessary diagnostics, not proof of coverage. " +
            "The activity owner uses the last 120 matured targets, age-group frequency with global fallback, Beta(1,1) smoothing and its existing earlier-only calibration gate. " +
            "Positive amount controls compare the existing pooled positive mean with the last eight positive training outcomes; expected means multiply probability and positive mean, never range endpoints. " +
            "Continuation controls assume recent work continues, not usual future activity. All-point availability and shared-target errors stay separate. " +
            "Last fifth of the declared time range is final holdout; boundary-crossing targets are excluded from both summaries. No tuning or candidate choice uses final outcomes. " +
            "Positive-work 10th–90th ranges describe training outcomes, not calibrated unconditional bands or workload paths. Six-hour blocks use the study end as a grouping boundary, not a quota reset. " +
            "No quota conversion, exhaustion probability, new live model or automatic promotion.");
    }

    private static (double? Probability, double? Conditional, double? Expected, double? Low, double? High) Point(UsualActivityOrigin t, string c) => c switch
    {
        "age-group-hurdle" => (t.Probability, t.ConditionalTokens, t.Probability * t.ConditionalTokens, t.ConditionalLow, t.ConditionalHigh),
        "global-activity-control" => (t.GlobalProbability, t.ConditionalTokens, t.GlobalProbability * t.ConditionalTokens, t.ConditionalLow, t.ConditionalHigh),
        "recent-positive-hurdle" => (t.Probability, t.RecentPositiveTokens, t.Probability * t.RecentPositiveTokens, null, null),
        "continue-30m" => (null, t.Continue30m, t.Continue30m, null, null),
        "continue-2h" => (null, t.Continue2h, t.Continue2h, null, null),
        "zero-work" => (0, null, 0, null, null),
        _ => throw new ArgumentException("Unknown activity candidate."),
    };

    private static UsualActivityScore Score(UsualActivityOrigin[] rows, UsualActivityOrigin[] matched, string candidate,
        double horizon, string period, DateTimeOffset through)
    {
        double? Avg(IEnumerable<double> x) { var a = x.ToArray(); return a.Length == 0 ? null : a.Average(); }
        double Brier(UsualActivityOrigin t, double probability) => Math.Pow(probability - (t.ObservedTokens > 0 ? 1 : 0), 2);
        var probability = rows.Where(t => Point(t, candidate).Probability is not null).ToArray();
        var expected = rows.Where(t => Point(t, candidate).Expected is not null).ToArray();
        var active = rows.Where(t => t.ObservedTokens > 0 && Point(t, candidate).Conditional is not null).ToArray();
        var band = active.Where(t => Point(t, candidate).Low is not null && Point(t, candidate).High is not null).ToArray();
        var published = candidate == "age-group-hurdle" ? rows.Where(t => t.ActivityGatePassed).ToArray() : [];
        var blocks = ChronologicalEvidence.Blocks(matched, t => t.OriginUtc, t => t.EndUtc, _ => through);
        double denominator = matched.Sum(t => t.ObservedTokens);
        double? error = Avg(expected.Select(t => Math.Pow(Point(t, candidate).Expected!.Value - t.ObservedTokens, 2)));
        double? calibration = probability.Length == 0 ? null : probability.GroupBy(t => Math.Min(4, (int)(Point(t, candidate).Probability!.Value * 5)))
            .Sum(g => (double)g.Count() / probability.Length * Math.Abs(g.Average(t => Point(t, candidate).Probability!.Value) - g.Average(t => t.ObservedTokens > 0 ? 1d : 0d)));
        return new(horizon, period, candidate, rows.Length, probability.Length,
            Avg(probability.Select(t => Brier(t, Point(t, candidate).Probability!.Value))), calibration, published.Length,
            Avg(published.Select(t => Brier(t, t.Probability))), Avg(published.Select(t => Brier(t, t.GlobalProbability))), expected.Length,
            Avg(expected.Select(t => Math.Abs(Point(t, candidate).Expected!.Value - t.ObservedTokens))), error is null ? null : Math.Sqrt(error.Value),
            matched.Length, Avg(matched.Select(t => Math.Abs(Point(t, candidate).Expected!.Value - t.ObservedTokens))),
            denominator > 0 ? matched.Sum(t => Math.Abs(Point(t, candidate).Expected!.Value - t.ObservedTokens)) / denominator : null,
            matched.Length == 0 ? null : blocks.SelectMany(b => b).Sum(t => Point(t, candidate).Expected!.Value - t.ObservedTokens), blocks.Count,
            active.Length, Avg(active.Select(t => Math.Abs(Point(t, candidate).Conditional!.Value - t.ObservedTokens))), band.Length,
            Avg(band.Select(t => t.ObservedTokens >= Point(t, candidate).Low && t.ObservedTokens <= Point(t, candidate).High ? 1d : 0d)),
            Avg(band.Select(t => Point(t, candidate).High!.Value - Point(t, candidate).Low!.Value)));
    }
}
