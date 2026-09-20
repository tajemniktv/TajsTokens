using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Descriptive held-out partitions only. Never used to fit or promote a model.</summary>
public static class ComposedQuotaBreakdowns
{
    public const string Methodology = "Breakdowns partition each candidate's own held-out targets separately by dimension; " +
        "do not add dimensions or candidates. Activity and model/effort mix use origin-available evidence; recorded-work " +
        "groups use the later outcome and are not prediction inputs. Dominant mix means at least 80% of projected token mass " +
        "(a descriptive convention, not a learned threshold); unknown mass is not renormalized. Zero-use and incumbent " +
        "comparisons use explicitly matched subsets. Outcome quality is a later diagnostic; no recorded flags does not " +
        "prove completeness. Category completeness checks reported vectors, not account-wide coverage. Bias is predicted minus displayed quota movement. Reset groups use " +
        "the existing bounded timestamp tolerance. Quota-movement groups use the later meter envelope and a fixed 5pp " +
        "diagnostic cutoff per target interval (not a provider rule, hourly rate or cross-window equivalent). Envelopes " +
        "straddling the cutoff stay separate. Underprediction counts predictions below the lower meter bound; mean " +
        "underprediction includes zero misses among finite predictions with valid meter envelopes, not unknown rows. " +
        "Small subgroups and meter uncertainty do not establish a winner.";

    public static IReadOnlyList<ComposedQuotaBreakdown> Build(IReadOnlyList<ComposedQuotaTrial> trials)
    {
        var result = new List<ComposedQuotaBreakdown>();
        void Add(string dimension, string label, IEnumerable<ComposedQuotaTrial> rows)
        {
            var items = rows.ToArray();
            var incumbent = items.Where(x => x.IncumbentIntervalLoss is not null).ToArray();
            var zero = items.Where(x => x.ZeroUseIntervalLoss is not null).ToArray();
            var bands = items.Where(x => x.ObservedRemainingPercent is not null && x.LowerRemainingPercent is not null && x.UpperRemainingPercent is not null).ToArray();
            var misses = items.Where(x => HasMeterEnvelope(x) && double.IsFinite(x.PredictedDelta))
                .Select(x => Math.Max(0, x.LowerObservedDelta!.Value - x.PredictedDelta)).ToArray();
            result.Add(new(dimension, label, items.Length, QuotaResetGenerationPolicy.Group(items, x => x.ResetUtc).Count,
                items.Average(x => x.IntervalLoss), items.Average(x => x.CostOnlyIntervalLoss), items.Average(x => x.PaceIntervalLoss),
                items.Average(x => x.PredictedDelta - x.ObservedDelta), incumbent.Length,
                Mean(incumbent.Select(x => x.IntervalLoss)), Mean(incumbent.Select(x => x.IncumbentIntervalLoss!.Value)),
                zero.Length, Mean(zero.Select(x => x.IntervalLoss)), Mean(zero.Select(x => x.ZeroUseIntervalLoss!.Value)),
                bands.Length, Mean(bands.Select(x => x.ObservedRemainingPercent >= x.LowerRemainingPercent && x.ObservedRemainingPercent <= x.UpperRemainingPercent ? 1d : 0d)),
                Mean(bands.Select(x => x.UpperRemainingPercent!.Value - x.LowerRemainingPercent!.Value)))
            {
                MeteredOutcomes = misses.Length,
                UnderpredictedOutcomes = misses.Count(x => x > 0),
                MeanUnderprediction = Mean(misses)
            });
        }
        foreach (var reset in QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc))
            Add("reset", reset.Min(x => x.ResetUtc).ToString("O"), reset);
        void Partition(string dimension, Func<ComposedQuotaTrial, string> key)
        {
            foreach (var group in trials.GroupBy(key).OrderBy(x => x.Key, StringComparer.Ordinal)) Add(dimension, group.Key, group);
        }
        Partition("origin-activity", x => x.OriginActivity);
        Partition("origin-model-mix", x => Mix(x.Workload.ModelShares));
        Partition("origin-effort-mix", x => Mix(x.Workload.EffortShares));
        Partition("recorded-outcome", x => x.RecordedOutcomeTokens is null ? "unknown" : x.RecordedOutcomeTokens > 0 ? "positive-recorded-work" : "no-recorded-work");
        Partition("outcome-quota-movement", x => !HasMeterEnvelope(x) ? "unknown-meter-envelope" :
            x.LowerObservedDelta >= 5 ? "at-least-5pp" : x.UpperObservedDelta < 5 ? "below-5pp" : "straddles-5pp");
        Partition("outcome-category-coverage", x => x.CompleteOutcomeTokenCategories switch {
            true => "complete-reported-vectors", false => "incomplete-reported-vectors", _ => "unknown" });
        Partition("outcome-quality", x => x.OutcomeQualityFlags.Count == 0 ? "no-recorded-flags-not-proven-complete" :
            string.Join("; ", x.OutcomeQualityFlags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        return result;
    }

    private static bool HasMeterEnvelope(ComposedQuotaTrial trial) =>
        trial.LowerObservedDelta is double low && double.IsFinite(low) && low >= 0 &&
        trial.UpperObservedDelta is double high && double.IsFinite(high) && high >= low;

    private static string Mix(IReadOnlyDictionary<string, double> shares)
    {
        if (shares.Count == 0 || shares.Any(x => string.IsNullOrWhiteSpace(x.Key) || !double.IsFinite(x.Value) || x.Value < 0) ||
            shares.Values.Sum() < .8 || shares.Values.Sum() > 1.000001) return "unknown-or-partial";
        var largest = shares.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
        return largest.Value >= .8 ? "dominant: " + largest.Key : "mixed";
    }

    private static double? Mean(IEnumerable<double> values)
    {
        var rows = values.ToArray(); return rows.Length == 0 ? null : rows.Average();
    }
}
