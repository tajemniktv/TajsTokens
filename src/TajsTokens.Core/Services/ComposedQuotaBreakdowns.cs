using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Descriptive held-out partitions only. Never used to fit or promote a model.</summary>
public static class ComposedQuotaBreakdowns
{
    public const string Methodology = "Breakdowns partition each candidate's own held-out targets separately by dimension; " +
        "do not add dimensions or candidates. Activity and model/effort mix use origin-available evidence; recorded-work " +
        "groups use the later outcome and are not prediction inputs. Dominant mix means at least 80% of projected token mass " +
        "(a descriptive convention, not a learned threshold); unknown mass is not renormalized. Zero-use and incumbent " +
        "comparisons use explicitly matched subsets. Bias is predicted minus displayed quota movement. Reset groups use " +
        "the existing bounded timestamp tolerance. Small subgroups and meter uncertainty do not establish a winner.";

    public static IReadOnlyList<ComposedQuotaBreakdown> Build(IReadOnlyList<ComposedQuotaTrial> trials)
    {
        var result = new List<ComposedQuotaBreakdown>();
        void Add(string dimension, string label, IEnumerable<ComposedQuotaTrial> rows)
        {
            var items = rows.ToArray();
            var incumbent = items.Where(x => x.IncumbentIntervalLoss is not null).ToArray();
            var zero = items.Where(x => x.ZeroUseIntervalLoss is not null).ToArray();
            var bands = items.Where(x => x.ObservedRemainingPercent is not null && x.LowerRemainingPercent is not null && x.UpperRemainingPercent is not null).ToArray();
            result.Add(new(dimension, label, items.Length, QuotaResetGenerationPolicy.Group(items, x => x.ResetUtc).Count,
                items.Average(x => x.IntervalLoss), items.Average(x => x.CostOnlyIntervalLoss), items.Average(x => x.PaceIntervalLoss),
                items.Average(x => x.PredictedDelta - x.ObservedDelta), incumbent.Length,
                Mean(incumbent.Select(x => x.IntervalLoss)), Mean(incumbent.Select(x => x.IncumbentIntervalLoss!.Value)),
                zero.Length, Mean(zero.Select(x => x.IntervalLoss)), Mean(zero.Select(x => x.ZeroUseIntervalLoss!.Value)),
                bands.Length, Mean(bands.Select(x => x.ObservedRemainingPercent >= x.LowerRemainingPercent && x.ObservedRemainingPercent <= x.UpperRemainingPercent ? 1d : 0d)),
                Mean(bands.Select(x => x.UpperRemainingPercent!.Value - x.LowerRemainingPercent!.Value))));
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
        return result;
    }

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
