// Taj's Tokens | ChronologicalEvidence.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Services;

/// <summary>
///     Statistical grouping, not accounting-window identity. Six-hour blocks are a provisional
///     spacing assumption, not proof of independence. Callers supply one compatible cohort/target.
///     Outcomes never cross a reset and are never reused in overlapping blocks.
/// </summary>
public static class ChronologicalEvidence
{
    public const string Version = "chronological-blocks/v1";

    public static IReadOnlyList<IReadOnlyList<T>> Blocks<T>(
        IEnumerable<T> rows,
        Func<T, DateTimeOffset> start,
        Func<T, DateTimeOffset> end,
        Func<T, DateTimeOffset> reset)
    {
        var blocks = new List<IReadOnlyList<T>>();
        List<T>? block = null;
        DateTimeOffset? previousEnd = null;
        DateTimeOffset blockStart = default, blockReset = default;
        foreach (T row in rows.OrderBy(start).ThenBy(end))
        {
            DateTimeOffset a = start(row);
            DateTimeOffset b = end(row);
            DateTimeOffset r = reset(row);
            if (b <= a || b > r || previousEnd is { } prior && a < prior) continue;
            if (block is null || a - blockStart >= TimeSpan.FromHours(6) ||
                !QuotaResetGenerationPolicy.SameTimestamp(r, blockReset))
            {
                block = [];
                blocks.Add(block);
                blockStart = a;
                blockReset = r;
            }
            block.Add(row);
            previousEnd = b;
        }
        return blocks;
    }

    public static Support Describe<T>(
        IReadOnlyList<T> rows,
        Func<T, DateTimeOffset> start,
        Func<T, DateTimeOffset> end,
        Func<T, DateTimeOffset> reset,
        Func<T, double> residual)
    {
        IReadOnlyList<IReadOnlyList<T>> blocks = Blocks(rows, start, end, reset);
        T[] selected = blocks.SelectMany(x => x).ToArray();
        double[] means = blocks.Select(g => g.Average(residual)).ToArray();
        return new Support(
            rows.Count,
            selected.Length,
            blocks.Count,
            EffectiveSamples(means),
            selected.Select(x => start(x).UtcDateTime.Date).Distinct().Count(),
            QuotaResetGenerationPolicy.Group(selected, reset).Count,
            selected.Length == 0 ? TimeSpan.Zero : selected.Max(end) - selected.Min(start));
    }

    // Initial-positive autocorrelation sequence on block means. Irregular block spacing and
    // constant residuals do not establish independence; ESS is a diagnostic upper bound.
    public static double EffectiveSamples(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return values.Count;
        double mean = values.Average();
        double variance = values.Sum(x => (x - mean) * (x - mean));
        if (variance < 1e-12) return 1;
        double sum = 0d;
        for (int lag = 1; lag <= Math.Min(10, values.Count / 3); lag++)
        {
            double covariance = Enumerable.Range(lag, values.Count - lag)
                .Sum(i => (values[i] - mean) * (values[i - lag] - mean));
            double correlation = covariance / variance;
            if (correlation <= 0) break;
            sum += correlation;
        }
        return Math.Clamp(values.Count / (1 + 2 * sum), 1, values.Count);
    }

    public sealed record Support(
        int RawObservations,
        int NonOverlappingOutcomes,
        int EvaluationBlocks,
        double EffectiveSampleSize,
        int DistinctDays,
        int DistinctResetCycles,
        TimeSpan TemporalSpan);
}