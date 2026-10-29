using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record QuotaSelectionSummary(string Outcome, int Attempts, int DistinctOutcomes, int NonOverlappingAttempts,
    int Blocks, int KnownAvailability, int TimelyAvailability, int InvalidOrUnknownClocks, int NoRecordedTokenEvents,
    int IncompleteTokenCategories, double? MeanOriginHeadroom, double? MedianOriginHeadroom, double? MeanDisplayedMovement,
    double? MeanRecordedTokens, double? MedianRecordedTokens, double? MeanOriginMinutesToReset);
public sealed record QuotaCensoringGroup(string Cohort, string Window, bool KnownAccount, string Authority,
    double HorizonHours, int CandidateStarts, int OrdinaryIntervals, IReadOnlyDictionary<string, int> RejectedStarts,
    IReadOnlyList<QuotaSelectionSummary> Selection);
public sealed record QuotaCensoringAttempt(string Cohort, double HorizonHours, DateTimeOffset OriginUtc,
    DateTimeOffset NominalTargetUtc, DateTimeOffset OutcomeUtc, DateTimeOffset ResetUtc,
    DateTimeOffset OriginalReportedOriginUtc, DateTimeOffset OriginalReportedOutcomeUtc, double StartUsed, double EndUsed,
    long RecordedTokens, int TokenObservations, bool CompleteTokenCategories, DateTimeOffset? TokenCostAvailableUtc,
    bool ValidCollectionClocks);
public sealed record QuotaCensoringReport(string Version, string EvidenceIdentity, ForecastReplayAvailability Availability,
    DateTimeOffset CutoffUtc, int InputQuotaRows, int FrameCeilingReadings, int EligibleCeilingReadings,
    int OrdinaryIntervals, int AuditedAttempts, IReadOnlyList<QuotaCensoringGroup> Groups,
    IReadOnlyList<QuotaCensoringAttempt> Attempts, string Boundary);

/// <summary>Measures the existing sampler's selection; introduces no censored loss or latent-cost label.</summary>
public static class QuotaCensoringStudy
{
    public const string Version = "quota-censoring-selection/v1";
    public const int MaximumAttempts = 100_000;
    public static QuotaCensoringReport Evaluate(CodexForecastDataset source, ForecastReplayAvailability availability,
        CancellationToken token = default, bool includeAttempts = false)
    {
        if (!Enum.IsDefined(availability)) throw new ArgumentException("Unknown availability mode.");
        bool strict = availability == ForecastReplayAvailability.CollectedByOrigin;
        var quota = QuotaHistoryPolicy.AvailableRows(source.Quota, availability).Where(q => q.CapturedAtUtc <= source.CapturedAtUtc)
            .Select(q => strict && (q.CollectedAtUtc is null || q.CollectedAtUtc < (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc) ||
                q.CollectedAtUtc > (q.ReplayMeasuredAtUtc ?? q.CapturedAtUtc).AddMinutes(5)) ? q with { UsedPercent = null } : q).ToArray();
        var data = source with { Quota = quota }; var attempts = new List<QuotaCostCandidate>();
        var built = QuotaCostObservationBuilder.BuildDetailed(data, token, horizons: ComposedQuotaValidation.EvaluationHorizons,
            auditCandidate: candidate =>
            {
                if (attempts.Count == MaximumAttempts) throw new InvalidOperationException("Censoring audit exceeds 100,000 attempts; narrow the range.");
                attempts.Add(candidate);
            });
        var cohorts = built.Coverage.Select(c => c.Cohort).Distinct().Select((c, i) => (c, Alias: "cohort-" + (i + 1))).ToDictionary(x => x.c, x => x.Alias);
        var groups = built.Coverage.Select(c => new QuotaCensoringGroup(cohorts[c.Cohort], c.Cohort.Kind.ToString(),
            c.Cohort.AccountKey is not null, QuotaSnapshot.ClassifyAuthority(c.Cohort.Source).ToString(), c.HorizonHours,
            c.CandidateStarts, c.BuiltIntervals, c.RejectedStarts, new[]
            {
                Summarize(attempts.Where(a => a.Cohort == c.Cohort && a.HorizonHours == c.HorizonHours && a.EndUsed < 100).ToArray(), "ordinary-selected"),
                Summarize(attempts.Where(a => a.Cohort == c.Cohort && a.HorizonHours == c.HorizonHours && a.EndUsed >= 100).ToArray(), "ceiling-excluded"),
            })).ToArray();
        var history = QuotaHistoryPolicy.Describe(quota, data.CapturedAtUtc);
        return new(Version, ForecastThresholdStudy.Fingerprint(data), availability, data.CapturedAtUtc, source.Quota.Count,
            quota.Count(q => q.UsedPercent >= 100), history.Count(d => d.Eligible && d.Observation.UsedPercent >= 100),
            built.Observations.Count, attempts.Count, groups, includeAttempts ? attempts.Select(a => new QuotaCensoringAttempt(
                cohorts[a.Cohort], a.HorizonHours, a.OriginUtc, a.NominalTargetUtc, a.OutcomeUtc, a.ResetUtc,
                a.OriginalReportedOriginUtc, a.OriginalReportedOutcomeUtc, a.StartUsed, a.EndUsed, a.RecordedTokens,
                a.TokenObservations, a.CompleteTokenCategories, a.TokenCostAvailableUtc, a.ValidCollectionClocks)).ToArray() : [],
            "Construction-selection audit, not a censoring estimator or causal bias correction. Ordinary training output and first-rejection order are unchanged. " +
            "The observer runs after overlap/warmup/target/compatibility checks, immediately before the outcome-ceiling exclusion. " +
            "Ceiling outcomes do not advance the released sampler's previous-end clock, so excluded attempts can overlap or reuse the same report; attempted, distinct-outcome and non-overlap counts remain separate. " +
            "Raw/eligible ceiling readings and saturated-origin rejections are other denominators, not independent clipped-cost targets. " +
            "Displayed movement/headroom, local recorded workload and time to reported reset describe the selected/excluded frames; no missing local events prove idleness or complete account attribution. " +
            "Known first-collection clocks and a five-minute label-availability budget are diagnostics, not proof of provider lag, endpoint precision or complete source coverage. " +
            "Original reported clocks remain separate when collection qualification rebases the effective clock. Reporting/collection times do not bracket physical crossing. " +
            "No latent-cost lower bound, exact hidden usage, censoring likelihood, calibrated crossing interval, quota price or live selection is introduced.");
    }

    private static QuotaSelectionSummary Summarize(QuotaCostCandidate[] rows, string outcome)
    {
        double? Mean(IEnumerable<double> values) { var a = values.ToArray(); return a.Length == 0 ? null : a.Average(); }
        double? Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return a.Length == 0 ? null : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
        var blocks = ChronologicalEvidence.Blocks(rows, r => r.OriginUtc, r => r.OutcomeUtc, r => r.ResetUtc);
        return new(outcome, rows.Length, rows.Select(r => r.OutcomeUtc).Distinct().Count(), blocks.Sum(b => b.Count), blocks.Count,
            rows.Count(r => r.TokenCostAvailableUtc is not null),
            rows.Count(r => r.ValidCollectionClocks && r.TokenCostAvailableUtc <= r.OutcomeUtc.AddMinutes(5)),
            rows.Count(r => !r.ValidCollectionClocks), rows.Count(r => r.TokenObservations == 0), rows.Count(r => !r.CompleteTokenCategories),
            Mean(rows.Select(r => 100 - r.StartUsed)), Median(rows.Select(r => 100 - r.StartUsed)), Mean(rows.Select(r => r.EndUsed - r.StartUsed)),
            Mean(rows.Select(r => (double)r.RecordedTokens)), Median(rows.Select(r => (double)r.RecordedTokens)),
            Mean(rows.Select(r => (r.ResetUtc - r.OriginUtc).TotalMinutes)));
    }
}
