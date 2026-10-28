using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Aggregates selected native increments directly; never forms cells by multiplying marginal shares.</summary>
public static class CodexJointWorkloadBuilder
{
    private sealed class Accumulator
    {
        private decimal input, cache, write, output, reasoning, total;
        private int count, inconsistent, invalid;
        public void Add(CodexPredictiveTokenEvent row)
        {
            input += row.UncachedInputTokens; cache += row.CacheReadTokens; write += row.CacheWriteTokens;
            output += row.NonReasoningOutputTokens; reasoning += row.ReasoningOutputTokens; total += row.ReportedTotalTokens;
            count++;
            if (!QuotaEvaluationCoverageBuilder.HasCompleteCategories(row)) inconsistent++;
            if (row.ReportedTotalTokens < 0 || row.UncachedInputTokens < 0 || row.CacheReadTokens < 0 ||
                row.CacheWriteTokens < 0 || row.NonReasoningOutputTokens < 0 || row.ReasoningOutputTokens < 0) invalid++;
        }
        public void Merge(Accumulator other)
        {
            input += other.input; cache += other.cache; write += other.write; output += other.output;
            reasoning += other.reasoning; total += other.total; count += other.count;
            inconsistent += other.inconsistent; invalid += other.invalid;
        }
        public CodexJointWorkloadCell Cell(CodexWorkloadCoordinate? key) => new(key, input, cache, write, output,
            reasoning, total, count, inconsistent, invalid);
    }

    private static readonly IComparer<(string? Model, string? Effort)> Comparer =
        Comparer<(string? Model, string? Effort)>.Create((a, b) =>
    {
        int c = StringComparer.Ordinal.Compare(a.Model, b.Model);
        return c == 0 ? StringComparer.Ordinal.Compare(a.Effort, b.Effort) : c;
    });

    public static CodexJointWorkload Build(IEnumerable<CodexPredictiveTokenEvent> rows)
    {
        var cells = new SortedDictionary<(string? Model, string? Effort), Accumulator>(Comparer);
        (string? Model, string? Effort)? largest = null;
        Accumulator? overflow = null;
        decimal missingModel = 0, missingEffort = 0;
        foreach (var row in rows)
        {
            var key = (Model: string.IsNullOrWhiteSpace(row.Model) ? null : row.Model,
                Effort: string.IsNullOrWhiteSpace(row.ReasoningEffort) ? null : row.ReasoningEffort);
            if (key.Model is null) missingModel += row.ReportedTotalTokens;
            if (key.Effort is null) missingEffort += row.ReportedTotalTokens;
            if (cells.TryGetValue(key, out var current)) { current.Add(row); continue; }
            if (cells.Count == CodexJointWorkload.MaximumCoordinates)
            {
                if (Comparer.Compare(key, largest!.Value) >= 0) { (overflow ??= new()).Add(row); continue; }
                (overflow ??= new()).Merge(cells[largest.Value]);
                cells.Remove(largest.Value);
            }
            var added = new Accumulator(); added.Add(row); cells.Add(key, added);
            if (cells.Count == CodexJointWorkload.MaximumCoordinates) largest = cells.Last().Key;
        }
        return new(CodexJointWorkload.Version, CodexJointQuantityKind.ObservedIncrements,
            cells.Select(x => x.Value.Cell(new(x.Key.Model, x.Key.Effort))).ToArray(), overflow?.Cell(null), missingModel, missingEffort);
    }

    public static CodexJointWorkload? Project(CodexJointWorkload observed, double expectedTokens)
    {
        if (!double.IsFinite(expectedTokens) || expectedTokens < 0 || expectedTokens >= (double)decimal.MaxValue ||
            !observed.IsComplete || observed.ReportedTokens <= 0) return null;
        decimal scale = (decimal)expectedTokens / observed.ReportedTokens;
        CodexJointWorkloadCell Scale(CodexJointWorkloadCell cell)
        {
            decimal input = cell.UncachedInput * scale, cache = cell.CacheRead * scale, write = cell.CacheWrite * scale,
                output = cell.NonReasoningOutput * scale, reasoning = cell.ReasoningOutput * scale;
            return cell with { UncachedInput = input, CacheRead = cache, CacheWrite = write, NonReasoningOutput = output,
                ReasoningOutput = reasoning, ReportedTotal = input + cache + write + output + reasoning };
        }
        return new(CodexJointWorkload.Version, CodexJointQuantityKind.ConditionalProjection, observed.Cells.Select(Scale).ToArray(),
            observed.Overflow is { } overflow ? Scale(overflow) : null, observed.MissingModelTokens * scale, observed.MissingEffortTokens * scale);
    }
}
