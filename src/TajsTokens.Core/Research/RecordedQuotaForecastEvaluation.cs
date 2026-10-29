using System.Security.Cryptography;
using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

public sealed record RecordedQuotaForecastTrial(string Cohort, string Window, string Phase, string Role,
    double? HorizonHours, string ResetLead, string Model, string PolicyVersion, DateTimeOffset OriginUtc,
    DateTimeOffset TargetUtc, DateTimeOffset ResetUtc, DateTimeOffset? ConfirmedAtUtc, double? AnchorRemaining,
    double? PredictedRemaining, double? LowerRemaining, double? UpperRemaining, string OutcomeState,
    DateTimeOffset? OutcomeUtc = null, double? ObservedRemaining = null, bool Saturated = false,
    bool? PredictedThreshold = null, bool? ObservedThreshold = null, double? WarningToSaturationReportMinutes = null,
    bool RecordedBeforeTarget = false);

public sealed record RecordedQuotaForecastScore(string Cohort, string Window, string Phase, string Role,
    double? HorizonHours, string ResetLead, string Model, string PolicyVersion, int Computations,
    int SupportedPoints, int BeforeTargetSupportedPoints, int BandComputations, int BeforeTargetBandComputations,
    int MatchedTargets, int OrdinaryLossOutcomes, int SaturatedTargets,
    int NonOverlappingOutcomes, int EvaluationBlocks, int BandedOutcomes, int MissedDisplayedBursts,
    int ThresholdComparisons, int FalseThresholdPredictions, int MissedThresholds, int SaturationReportLeads,
    double? MeanSignedRemainingError, double? MeanAbsoluteRemainingError, double? RootMeanSquaredRemainingError,
    double? NonOverlappingSignedErrorSum, double? IntervalCoverage, double? MeanIntervalWidth,
    double? MeanWarningToSaturationReportMinutes, IReadOnlyDictionary<string, int> OutcomeStates);

public sealed record RecordedQuotaForecastComparison(string Cohort, string Window, string Phase, double HorizonHours,
    string ResetLead, string SelectedModel, string PolicyVersion, int MatchedOrdinaryOutcomes, int NonOverlappingOutcomes,
    double? SelectedMeanAbsoluteError, double? DirectMeanAbsoluteError);

public sealed record RecordedQuotaForecastReport(string Version, string EvidenceIdentity, DateTimeOffset AsOfUtc,
    int Computations, int InvalidEntries, IReadOnlyList<RecordedQuotaForecastScore> Scores,
    IReadOnlyList<RecordedQuotaForecastComparison> Comparisons, IReadOnlyList<RecordedQuotaForecastTrial> Trials, string Boundary);

/// <summary>Scores immutable computed outputs; never refits, retimes or promotes a forecast.</summary>
public static class RecordedQuotaForecastEvaluation
{
    public const string Version = "recorded-quota-evaluation/v1";
    private sealed record IndexedTrial(int Entry, QuotaForecastPointRole Role, RecordedQuotaForecastTrial Value);

    public static RecordedQuotaForecastReport Evaluate(IReadOnlyList<QuotaForecastComputationEntry> entries,
        IReadOnlyList<QuotaSnapshot> quota, DateTimeOffset asOfUtc, CancellationToken token = default, bool includeTrials = false)
    {
        if (entries.Count > 10_000 || quota.Count > 500_000) throw new ArgumentException("Narrow the recorded-forecast evaluation range.");
        var admitted = entries.Where(e => e.RecordedAtUtc <= asOfUtc).ToArray();
        // Use only evidence known by this cutoff; never import a later capture into an earlier report.
        var known = quota.Where(q => q.CapturedAtUtc <= asOfUtc && (q.CollectedAtUtc is null || q.CollectedAtUtc <= asOfUtc)).ToArray();
        var cohorts = admitted.Where(e => e.Problem is null && e.Computation is not null).Select(e => e.Computation!.Cohort).Distinct().ToArray();
        if (cohorts.Length > 64) throw new ArgumentException("Narrow the recorded-forecast cohort range.");
        var indexed = new List<IndexedTrial>();
        for (int cohortIndex = 0; cohortIndex < cohorts.Length; cohortIndex++)
        {
            token.ThrowIfCancellationRequested();
            QuotaHistoryCohort cohort = cohorts[cohortIndex];
            string alias = "cohort-" + (cohortIndex + 1);
            var decisions = QuotaHistoryPolicy.Describe(known.Where(q => QuotaHistoryPolicy.SameMeter(QuotaHistoryPolicy.Cohort(q), cohort)), asOfUtc);
            // Duplicate/cached copies add no intervals. Invalid/conflicting/regime rows remain boundaries.
            var stream = QuotaHistoryPolicy.PaceRows(QuotaHistoryPolicy.ReplayRows(decisions.Where(d =>
                d.Reason is not "same-time-repeat" and not "possibly-cached-repeat")), cohort);
            var origins = QuotaForecastCalibration.SplitEpochs(stream).SelectMany(epoch =>
            {
                var saturation = epoch.Where(q => q.UsedPercent >= 100 && q.CollectedAtUtc >= q.CapturedAtUtc &&
                    q.CollectedAtUtc <= q.CapturedAtUtc.AddMinutes(5)).ToArray();
                return epoch.Select((q, i) => (q.CapturedAtUtc, Epoch: epoch, Index: i, Saturation: saturation));
            }).GroupBy(x => x.CapturedAtUtc).ToDictionary(g => g.Key, g => g.Last());
            for (int entryIndex = 0; entryIndex < admitted.Length; entryIndex++)
            {
                var entry = admitted[entryIndex];
                if (entry.Problem is not null || entry.Computation is not { } c || c.Cohort != cohort) continue;
                foreach (var point in c.Points)
                {
                    token.ThrowIfCancellationRequested();
                    var trial = new RecordedQuotaForecastTrial(alias, cohort.Kind.ToString(), c.Phase.ToString(), point.Role.ToString(),
                        point.Role == QuotaForecastPointRole.SelectedReset ? null : point.HorizonHours,
                        Lead((c.ResetUtc - c.AnchorUtc).TotalHours), point.Model, c.PolicyVersion, c.AnchorUtc, point.TargetUtc,
                        c.ResetUtc, entry.ConfirmedAtUtc, c.AnchorUsedPercent is { } used ? 100 - used : null,
                        point.RemainingPercent, point.LowerRemainingPercent, point.UpperRemainingPercent, "unmatched",
                        RecordedBeforeTarget: entry.Timing(point) == QuotaForecastRecordingTiming.BeforeTarget && entry.ConfirmedAtUtc <= asOfUtc);
                    string? reason = entry.Timing(point) switch
                    {
                        QuotaForecastRecordingTiming.Unconfirmed => "unconfirmed",
                        QuotaForecastRecordingTiming.InvalidClock => "invalid-issue-clock",
                        QuotaForecastRecordingTiming.AtOrAfterTarget => "issued-at-or-after-target",
                        _ => null,
                    };
                    if (entry.ConfirmedAtUtc > asOfUtc) reason = "confirmation-after-cutoff";
                    if (reason is null && point.RemainingPercent is null) reason = "point-unsupported";
                    if (reason is null && c.AnchorUsedPercent is not (>= 0 and < 100)) reason = "anchor-not-forecastable";
                    if (reason is null && c.AnchorMeasuredUtc is null) reason = "anchor-measurement-time-unknown";
                    if (reason is null && point.TargetUtc > c.ResetUtc) reason = "target-crosses-reset";
                    if (reason is null && point.TargetUtc > asOfUtc) reason = "target-immature";
                    if (reason is null)
                    {
                        if (!origins.TryGetValue(c.AnchorUtc, out var location) ||
                            location.Epoch[location.Index].UsedPercent != c.AnchorUsedPercent ||
                            !QuotaResetGenerationPolicy.SameTimestamp(location.Epoch[location.Index].ResetsAtUtc, c.ResetUtc))
                            reason = "anchor-unavailable-or-revised";
                        else
                        {
                            var epoch = location.Epoch;
                            bool resetTarget = point.Role == QuotaForecastPointRole.SelectedReset;
                            int targetIndex = LowerBound(epoch, point.TargetUtc);
                            if (resetTarget && (targetIndex == epoch.Count || epoch[targetIndex].CapturedAtUtc > point.TargetUtc)) targetIndex--;
                            QuotaSnapshot? outcome = targetIndex >= 0 && targetIndex < epoch.Count ? epoch[targetIndex] : null;
                            double tolerance = resetTarget ? 5 : Math.Min(5, point.HorizonHours * 60 * .2);
                            if (outcome is null || outcome.CapturedAtUtc <= c.AnchorUtc ||
                                Math.Abs((outcome.CapturedAtUtc - point.TargetUtc).TotalMinutes) > tolerance)
                                reason = "compatible-target-unavailable";
                            else if (outcome.CollectedAtUtc is null) reason = "outcome-collection-unknown";
                            else if (outcome.CollectedAtUtc < outcome.CapturedAtUtc) reason = "invalid-outcome-clock";
                            else if (outcome.CollectedAtUtc > outcome.CapturedAtUtc.AddMinutes(5)) reason = "outcome-collected-too-late";
                            else
                            {
                                // A pre-reset proxy cannot establish survival. A positive threshold
                                // label requires an actual compatible saturation report by the target.
                                int saturationIndex = LowerBound(location.Saturation, c.AnchorUtc.AddTicks(1));
                                var saturated = saturationIndex < location.Saturation.Length && location.Saturation[saturationIndex].CapturedAtUtc <= point.TargetUtc
                                    ? location.Saturation[saturationIndex] : null;
                                bool? actualThreshold = resetTarget
                                    ? saturated is not null ? true : outcome.CapturedAtUtc == c.ResetUtc ? false : null
                                    : outcome.UsedPercent < 100 ? false : saturated is not null ? true : null;
                                bool? predictedThreshold = resetTarget ? point.SurvivesUntilReset is { } survives ? !survives : null : point.RemainingPercent <= 0;
                                trial = trial with { OutcomeState = "matched", OutcomeUtc = outcome.CapturedAtUtc,
                                    ObservedRemaining = outcome.RemainingPercent, Saturated = outcome.UsedPercent >= 100,
                                    PredictedThreshold = predictedThreshold, ObservedThreshold = actualThreshold,
                                    WarningToSaturationReportMinutes = predictedThreshold == true && saturated is not null
                                        ? (saturated.CapturedAtUtc - entry.ConfirmedAtUtc!.Value).TotalMinutes : null };
                            }
                        }
                    }
                    if (reason is not null) trial = trial with { OutcomeState = reason };
                    indexed.Add(new(entryIndex, point.Role, trial));
                }
            }
        }
        var scores = indexed.Select(x => x.Value).GroupBy(t => new { t.Cohort, t.Window, t.Phase, t.Role, t.HorizonHours,
            t.ResetLead, t.Model, t.PolicyVersion }).Select(g =>
        {
            var matched = g.Where(t => t.OutcomeState == "matched").ToArray();
            var ordinary = matched.Where(t => !t.Saturated).ToArray();
            var blocks = ChronologicalEvidence.Blocks(ordinary, t => t.OriginUtc, t => t.OutcomeUtc!.Value, t => t.ResetUtc);
            var independent = blocks.SelectMany(x => x).ToArray();
            var bands = ordinary.Where(t => t.LowerRemaining is not null && t.UpperRemaining is not null).ToArray();
            var thresholds = matched.Where(t => t.PredictedThreshold is not null && t.ObservedThreshold is not null).ToArray();
            var leads = matched.Where(t => t.WarningToSaturationReportMinutes is not null).ToArray();
            double Error(RecordedQuotaForecastTrial t) => t.PredictedRemaining!.Value - t.ObservedRemaining!.Value;
            return new RecordedQuotaForecastScore(g.Key.Cohort, g.Key.Window, g.Key.Phase, g.Key.Role, g.Key.HorizonHours,
                g.Key.ResetLead, g.Key.Model, g.Key.PolicyVersion, g.Count(), g.Count(t => t.PredictedRemaining is not null),
                g.Count(t => t.RecordedBeforeTarget && t.PredictedRemaining is not null),
                g.Count(t => t.LowerRemaining is not null && t.UpperRemaining is not null),
                g.Count(t => t.RecordedBeforeTarget && t.PredictedRemaining is not null && t.LowerRemaining is not null && t.UpperRemaining is not null),
                matched.Length, ordinary.Length, matched.Count(t => t.Saturated), independent.Length, blocks.Count, bands.Length,
                ordinary.Count(t => t.AnchorRemaining - t.ObservedRemaining >= 2 && t.AnchorRemaining - t.PredictedRemaining < 1),
                thresholds.Length, thresholds.Count(t => t.PredictedThreshold == true && t.ObservedThreshold == false),
                thresholds.Count(t => t.PredictedThreshold == false && t.ObservedThreshold == true), leads.Length,
                Mean(ordinary.Select(Error)), Mean(ordinary.Select(t => Math.Abs(Error(t)))),
                ordinary.Length == 0 ? null : Math.Sqrt(ordinary.Average(t => Math.Pow(Error(t), 2))),
                independent.Length == 0 ? null : independent.Sum(Error),
                Mean(bands.Select(t => t.ObservedRemaining >= t.LowerRemaining && t.ObservedRemaining <= t.UpperRemaining ? 1d : 0d)),
                Mean(bands.Select(t => t.UpperRemaining!.Value - t.LowerRemaining!.Value)),
                Mean(leads.Select(t => t.WarningToSaturationReportMinutes!.Value)),
                g.GroupBy(t => t.OutcomeState).ToDictionary(s => s.Key, s => s.Count()));
        }).ToArray();
        var byEntry = indexed.ToLookup(x => x.Entry);
        var pairs = indexed.Where(x => x.Role == QuotaForecastPointRole.SelectedHorizon && Ordinary(x.Value)).Select(x =>
            (Selected: x.Value, Direct: byEntry[x.Entry].FirstOrDefault(y => y.Role == QuotaForecastPointRole.DirectBaseline &&
                y.Value.TargetUtc == x.Value.TargetUtc && y.Value.HorizonHours == x.Value.HorizonHours && Ordinary(y.Value))?.Value))
            .Where(x => x.Direct is not null && x.Direct.OutcomeUtc == x.Selected.OutcomeUtc && x.Direct.ObservedRemaining == x.Selected.ObservedRemaining).ToArray();
        var comparisons = pairs.GroupBy(x => new { x.Selected.Cohort, x.Selected.Window, x.Selected.Phase, x.Selected.HorizonHours,
            x.Selected.ResetLead, x.Selected.Model, x.Selected.PolicyVersion }).Select(g => new RecordedQuotaForecastComparison(g.Key.Cohort, g.Key.Window,
            g.Key.Phase, g.Key.HorizonHours!.Value, g.Key.ResetLead, g.Key.Model, g.Key.PolicyVersion, g.Count(),
            ChronologicalEvidence.Blocks(g, x => x.Selected.OriginUtc, x => x.Selected.OutcomeUtc!.Value, x => x.Selected.ResetUtc).Sum(b => b.Count),
            Mean(g.Select(x => Math.Abs(x.Selected.PredictedRemaining!.Value - x.Selected.ObservedRemaining!.Value))),
            Mean(g.Select(x => Math.Abs(x.Direct!.PredictedRemaining!.Value - x.Direct.ObservedRemaining!.Value))))).ToArray();
        using var sha = SHA256.Create();
        string identity;
        using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write, leaveOpen: true))
        {
            JsonSerializer.Serialize(stream, new { Version, AsOfUtc = asOfUtc,
                Entries = admitted.Select(e => new { e.Id, e.RecordedAtUtc, e.ConfirmedAtUtc, e.Problem }), Quota = known });
            stream.FlushFinalBlock();
            identity = Convert.ToHexStringLower(sha.Hash!);
        }
        return new(Version, identity, asOfUtc, admitted.Length,
            entries.Count(e => e.Problem is not null || e.Computation is null || e.RecordedAtUtc is null), scores, comparisons,
            includeTrials ? indexed.Select(x => x.Value).ToArray() : [],
            "Immutable computed outputs scored without refitting or retiming. Actual post-commit confirmation must precede the target. " +
            "Eligible target measurements require known collection no later than measurement plus 5 minutes and by cutoff. " +
            "Fixed target tolerance is min(5 minutes, 20% of horizon); reset labels are last-within-5-minute proxies. " +
            "Missing/conflicting/regime/reset/decrease boundaries are not bridged. Saturated targets are counted, excluded from ordinary loss and not treated as exact latent consumption. " +
            "Positive signed remaining error is optimistic. Accumulated signed error uses non-overlapping outcomes only; six-hour block counts are spacing diagnostics, not independence proof. " +
            "Numeric point/band availability and before-target availability have separate counts; coverage conditions on matched nonsaturated banded targets. " +
            "A missed displayed burst means at least 2pp observed movement with under 1pp predicted; these are explicit reporting thresholds, not a provider precision claim. " +
            "Threshold comparisons describe computed outputs, not delivered warnings. Negative reset labels need a reading exactly at reset; a pre-reset proxy cannot establish survival. " +
            "Warning lead is to the first timely saturation report, can be negative, and does not bracket true crossing under unknown backend lag. " +
            "Matched selected/direct comparisons use the same computation, target and ordinary outcome. Availability and model groups are descriptive; no promotion or probability is inferred.");
    }

    private static bool Ordinary(RecordedQuotaForecastTrial t) => t.OutcomeState == "matched" && !t.Saturated;
    private static int LowerBound(IReadOnlyList<QuotaSnapshot> rows, DateTimeOffset at)
    {
        int lo = 0, hi = rows.Count;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (rows[mid].CapturedAtUtc < at) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private static string Lead(double hours) => hours <= 2 ? "0–2h" : hours <= 24 ? "2–24h" : "over-24h";
    private static double? Mean(IEnumerable<double> values) { var rows = values.ToArray(); return rows.Length == 0 ? null : rows.Average(); }
}
