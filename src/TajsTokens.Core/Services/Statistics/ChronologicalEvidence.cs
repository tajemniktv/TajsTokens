namespace TajsTokens.Core.Services;

/// <summary>
/// Statistical grouping, not accounting-window identity. Six-hour blocks are a provisional
/// spacing assumption, not proof of independence. Callers supply one compatible cohort/target.
/// Outcomes never cross a reset and are never reused in overlapping blocks.
/// </summary>
public static class ChronologicalEvidence
{
    public const string Version = "chronological-blocks/v1";
    public sealed record Support(int RawObservations, int NonOverlappingOutcomes, int EvaluationBlocks,
        double EffectiveSampleSize, int DistinctDays, int DistinctResetCycles, TimeSpan TemporalSpan);

    public static IReadOnlyList<IReadOnlyList<T>> Blocks<T>(IEnumerable<T> rows,
        Func<T, DateTimeOffset> start, Func<T, DateTimeOffset> end, Func<T, DateTimeOffset> reset)
    {
        var blocks = new List<IReadOnlyList<T>>();
        List<T>? block = null;
        DateTimeOffset? previousEnd = null;
        DateTimeOffset blockStart = default, blockReset = default;
        foreach (var row in rows.OrderBy(start).ThenBy(end))
        {
            var a = start(row); var b = end(row); var r = reset(row);
            if (b <= a || b > r || previousEnd is { } prior && a < prior) continue;
            if (block is null || a - blockStart >= TimeSpan.FromHours(6) ||
                !QuotaResetGenerationPolicy.SameTimestamp(r, blockReset))
            {
                block = []; blocks.Add(block); blockStart = a; blockReset = r;
            }
            block.Add(row); previousEnd = b;
        }
        return blocks;
    }

    public static Support Describe<T>(IReadOnlyList<T> rows, Func<T, DateTimeOffset> start,
        Func<T, DateTimeOffset> end, Func<T, DateTimeOffset> reset, Func<T, double> residual)
    {
        var blocks = Blocks(rows, start, end, reset);
        var selected = blocks.SelectMany(x => x).ToArray();
        var means = blocks.Select(g => g.Average(residual)).ToArray();
        return new(rows.Count, selected.Length, blocks.Count, EffectiveSamples(means),
            selected.Select(x => start(x).UtcDateTime.Date).Distinct().Count(),
            QuotaResetGenerationPolicy.Group(selected, reset).Count,
            selected.Length == 0 ? TimeSpan.Zero : selected.Max(end) - selected.Min(start));
    }

    // Initial-positive autocorrelation sequence on block means. Irregular block spacing and
    // constant residuals do not establish independence; ESS is a diagnostic upper bound.
    public static double EffectiveSamples(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return values.Count;
        var mean = values.Average();
        var variance = values.Sum(x => (x - mean) * (x - mean));
        if (variance < 1e-12) return 1;
        var sum = 0d;
        for (var lag = 1; lag <= Math.Min(10, values.Count / 3); lag++)
        {
            var covariance = Enumerable.Range(lag, values.Count - lag)
                .Sum(i => (values[i] - mean) * (values[i - lag] - mean));
            var correlation = covariance / variance;
            if (correlation <= 0) break;
            sum += correlation;
        }
        return Math.Clamp(values.Count / (1 + 2 * sum), 1, values.Count);
    }
}
