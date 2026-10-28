using TajsTokens.Core.Models;
using System.Numerics;

namespace TajsTokens.Core.Services;

/// <summary>Closed-form scoring for five leaves, input/output group totals and one overall total. No generic solver or fitting.</summary>
public static class TtJointScoring
{
    public const string Version = "tt-joint-scoring/v1";
    private static readonly int[][] Groups = [[0, 1, 2], [3, 4]];
    private static readonly BigInteger Scale = BigInteger.Pow(10, 28);
    private static readonly BigInteger MaximumMantissa = (BigInteger.One << 96) - 1;
    private sealed record Row(BigInteger Lower, BigInteger? Upper, BigInteger Covered, decimal? Total, decimal Unmapped, bool Partial, bool Inconsistent);

    public static TtScoreAssessment Evaluate(TtJointWorkloadBasis basis, CodexJointWorkload workload)
    {
        try
        {
            TtQuantityConstraints Convert(CodexJointWorkloadCell c) => new(c.Coordinate, c.UncachedInput, c.CacheRead,
                c.CacheWrite, c.NonReasoningOutput, c.ReasoningOutput, ReportedTotal: c.ReportedTotal, SourceInconsistent: !c.IsComplete);
            return Evaluate(basis, workload.Cells.Select(Convert).Concat(workload.Overflow is { } overflow ? [Convert(overflow)] : []).ToArray(), workload.Kind);
        }
        catch (OverflowException) { return Failure(basis, workload.Kind, TtScoreState.ArithmeticUnavailable, workload.Cells.Count + (workload.Overflow is null ? 0 : 1)); }
    }

    public static TtScoreAssessment Evaluate(TtJointWorkloadBasis basis, IReadOnlyList<TtQuantityConstraints> rows,
        CodexJointQuantityKind kind = CodexJointQuantityKind.ObservedIncrements)
    {
        ArgumentNullException.ThrowIfNull(basis); ArgumentNullException.ThrowIfNull(rows);
        if (!Enum.IsDefined(kind) || rows.Count > CodexJointWorkload.MaximumCoordinates + 1 || rows.Any(x => x is null))
            throw new ArgumentException("Unsupported joint scoring workload.");
        try
        {
            BigInteger lower = 0, covered = 0;
            BigInteger? upper = BigInteger.Zero;
            decimal unmapped = 0;
            decimal? total = 0;
            int partial = 0, inconsistent = 0;
            foreach (var constraints in rows)
            {
                Row row = ScoreRow(basis, constraints);
                if (row.Inconsistent) { inconsistent++; continue; }
                lower += row.Lower; upper += row.Upper; covered += row.Covered;
                total = total is { } a && row.Total is { } b ? ExactSum([a, b]) : null; unmapped = ExactSum([unmapped, row.Unmapped]);
                if (row.Partial) partial++;
            }
            if (inconsistent > 0) return Failure(basis, kind, TtScoreState.Inconsistent, rows.Count, inconsistent);
            TtScoreState state = partial > 0 ? TtScoreState.Partial : upper is null ? TtScoreState.Unbounded :
                lower == upper ? TtScoreState.Exact : TtScoreState.FiniteBounds;
            BigInteger normalizer = basis.Reference.Aggregate(BigInteger.Zero, (sum, amount) =>
            {
                if (!basis.TryWeight(amount.Coordinate, amount.Category, out decimal weight)) throw new InvalidOperationException("Unsupported reference.");
                return sum + Product(amount.Tokens, weight);
            });
            decimal? low = partial > 0 ? null : Ratio(lower, normalizer, state == TtScoreState.Exact ? 0 : -1);
            decimal? high = partial > 0 || upper is null ? null : state == TtScoreState.Exact ? low : Ratio(upper.Value, normalizer, 1);
            return new(Version, basis.BasisId, kind, state, low, high, Ratio(covered, normalizer, 0), total, unmapped, rows.Count, partial, 0);
        }
        catch (OverflowException) { return Failure(basis, kind, TtScoreState.ArithmeticUnavailable, rows.Count); }
    }

    private static TtScoreAssessment Failure(TtJointWorkloadBasis basis, CodexJointQuantityKind kind, TtScoreState state, int rows, int inconsistent = 0) =>
        new(Version, basis.BasisId, kind, state, null, null, null, null, null, rows, 0, inconsistent);

    private static Row ScoreRow(TtJointWorkloadBasis basis, TtQuantityConstraints c)
    {
        static Row Invalid() => new(0, null, 0, null, 0, false, true);
        decimal?[] values = [c.UncachedInput, c.CacheRead, c.CacheWrite, c.NonReasoningOutput, c.ReasoningOutput];
        decimal?[] budgets = [c.InputTotal, c.OutputTotal];
        if (c.SourceInconsistent || values.Concat(budgets).Append(c.ReportedTotal).Any(x => x < 0)) return Invalid();
        for (int i = 0; i < 2; i++)
        {
            decimal fixedSum = ExactSum(Groups[i].Select(j => values[j] ?? 0));
            bool allKnown = Groups[i].All(j => values[j] is not null);
            if (budgets[i] is { } budget && (budget < fixedSum || allKnown && budget != fixedSum)) return Invalid();
            if (allKnown) budgets[i] = fixedSum;
        }
        if (c.ReportedTotal is { } total)
        {
            if (budgets[0] is { } input && budgets[1] is { } output && ExactSum([input, output]) != total) return Invalid();
            if (budgets[0] is not null && budgets[1] is null) budgets[1] = ExactSum([total, -budgets[0]!.Value]);
            if (budgets[1] is not null && budgets[0] is null) budgets[0] = ExactSum([total, -budgets[1]!.Value]);
        }
        decimal? knownTotal = c.ReportedTotal ?? (budgets[0] is { } x && budgets[1] is { } y ? ExactSum([x, y]) : null);
        var partitions = c.ReportedTotal is not null && budgets.All(x => x is null)
            ? new[] { (Indices: new[] { 0, 1, 2, 3, 4 }, Budget: c.ReportedTotal) }
            : Groups.Select((g, i) => (Indices: g, Budget: budgets[i])).ToArray();
        BigInteger lower = 0, covered = 0;
        BigInteger? upper = BigInteger.Zero;
        decimal unmapped = 0;
        bool partial = false;
        foreach (var partition in partitions)
        {
            decimal fixedSum = 0;
            var freeWeights = new List<decimal?>();
            foreach (int i in partition.Indices)
            {
                decimal? weight = c.Coordinate is { } coordinate && basis.TryWeight(coordinate, (TtTokenCategory)i, out decimal w) ? w : null;
                if (values[i] is { } amount)
                {
                    fixedSum = ExactSum([fixedSum, amount]);
                    if (amount == 0) continue;
                    if (weight is { } known) { BigInteger value = Product(amount, known); lower += value; upper += value; covered += value; }
                    else { partial = true; unmapped = ExactSum([unmapped, amount]); }
                }
                else freeWeights.Add(weight);
            }
            decimal? residual = partition.Budget is { } budget ? ExactSum([budget, -fixedSum]) : null;
            if (residual < 0 || freeWeights.Count == 0 && residual is not null and not 0) return Invalid();
            if (freeWeights.Count == 0 || residual == 0) continue;
            if (freeWeights.Any(x => x is null))
            {
                partial = true;
                if (freeWeights.All(x => x is null) && residual is { } knownUnmapped) unmapped = ExactSum([unmapped, knownUnmapped]);
                continue;
            }
            decimal min = freeWeights.Min()!.Value, max = freeWeights.Max()!.Value;
            if (residual is { } remaining)
            {
                lower += Product(remaining, min); upper += Product(remaining, max);
                if (min == max) covered += Product(remaining, min);
            }
            else if (max > 0) upper = null;
            // Unconstrained amounts with known zero coefficients contribute exactly zero.
        }
        return new(lower, upper, covered, knownTotal, unmapped, partial, false);
    }

    // Exact finite-decimal products/sums keep tiny contributions instead of rounding them away.
    // Only final TT formatting rounds: nearest/even for identified values, outward for feasible bounds.
    private static BigInteger Scaled(decimal value)
    {
        int[] bits = decimal.GetBits(value); int scale = (bits[3] >> 16) & 255;
        BigInteger n = (uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        return ((bits[3] & int.MinValue) != 0 ? -n : n) * BigInteger.Pow(10, 28 - scale);
    }
    private static BigInteger Product(decimal amount, decimal weight) => Scaled(amount) * Scaled(weight);
    private static decimal ExactSum(IEnumerable<decimal> values)
    {
        BigInteger sum = values.Aggregate(BigInteger.Zero, (s, x) => s + Scaled(x));
        bool negative = sum.Sign < 0; sum = BigInteger.Abs(sum); int scale = 28;
        while (sum > MaximumMantissa && scale > 0)
        {
            sum = BigInteger.DivRem(sum, 10, out var remainder);
            if (remainder != 0) throw new OverflowException("Exact quantity sum exceeds decimal precision.");
            scale--;
        }
        if (sum > MaximumMantissa) throw new OverflowException();
        return Decimal(sum, scale, negative);
    }
    private static decimal Ratio(BigInteger numerator, BigInteger denominator, int direction)
    {
        for (int scale = 28; scale >= 0; scale--)
        {
            BigInteger n = numerator * BigInteger.Pow(10, scale);
            BigInteger q = BigInteger.DivRem(n, denominator, out var remainder);
            if (direction > 0 && remainder > 0 || direction == 0 &&
                (remainder * 2 > denominator || remainder * 2 == denominator && !q.IsEven)) q++;
            if (q > MaximumMantissa) continue;
            if (direction == 0 && numerator > 0 && q == 0) throw new OverflowException("Identified TT is below decimal precision.");
            return Decimal(q, scale, false);
        }
        throw new OverflowException("TT exceeds decimal range.");
    }
    private static decimal Decimal(BigInteger value, int scale, bool negative) => new(
        unchecked((int)(uint)(value & uint.MaxValue)), unchecked((int)(uint)((value >> 32) & uint.MaxValue)),
        unchecked((int)(uint)((value >> 64) & uint.MaxValue)), negative, (byte)scale);
}
