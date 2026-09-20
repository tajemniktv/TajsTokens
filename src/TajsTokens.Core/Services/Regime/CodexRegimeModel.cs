// Taj's Tokens | CodexRegimeModel.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public sealed record CodexRegimeCandidate(
    QuotaHistoryCohort Cohort,
    double HorizonHours,
    DateTimeOffset BoundaryLowerUtc,
    DateTimeOffset BoundaryUpperUtc,
    double BeforeResidual,
    double AfterResidual,
    double? RelativeRate,
    int BeforeSamples,
    int AfterSamples,
    string Explanation);

public sealed record CodexRegimeReport(string Version, IReadOnlyList<CodexRegimeCandidate> Candidates, string Methodology);

/// <summary>Retrospective residual segmentation. Does not assign durable regimes or relax live promotion.</summary>
public static class CodexRegimeModel
{
    public const string Version = "codex-retrospective-regime/v1";

    public const string Methodology = "Retrospective, not a live alert: both sides of each boundary use frozen held-out cost trials. " +
                                      "Measurement-envelope residuals; at least eight intervals on either side, robust median shift exceeding max(2pp, 3 MAD), " +
                                      "and at least 25% absolute-error reduction. Matched time-ablation residuals must corroborate a candidate when available. " +
                                      "A candidate is not proof of provider policy or a calibrated probability; coverage, model mix and reporting remain competing explanations. " +
                                      "Boundary bounds are adjacent observation origins, not a claimed exact change time. This analysis does not promote a runtime model.";

    public static CodexRegimeReport Analyze(QuotaCostReport report, CancellationToken token = default)
    {
        var candidates = new List<CodexRegimeCandidate>();
        foreach (IGrouping<(QuotaHistoryCohort Cohort, double HorizonHours), QuotaCostScore> group in report.Scores.GroupBy(x =>
                     (x.Cohort, x.HorizonHours)))
        {
            token.ThrowIfCancellationRequested();
            QuotaCostScore? score = group.FirstOrDefault(x => x.Candidate == "categories" && x.Trials.Count >= 16)
                                    ?? group.FirstOrDefault(x => x.Candidate == "total" && x.Trials.Count >= 16);
            if (score is null || score.Trials.Count > 10000) continue;
            QuotaCostTrial[] rows = score.Trials.OrderBy(x => x.StartUtc).ToArray();
            QuotaCostScore? time = group.FirstOrDefault(x => x.Candidate == "time-ablation");
            Dictionary<(DateTimeOffset StartUtc, DateTimeOffset EndUtc), QuotaCostTrial>? adjusted =
                time?.Trials.ToDictionary(x => (x.StartUtc, x.EndUtc));
            // One strongest boundary per cohort/horizon; no repeated splitting until noise looks structural.
            (int Index, double Gain, double Before, double After)? best = null;
            for (int i = 8; i <= rows.Length - 8; i++)
            {
                token.ThrowIfCancellationRequested();
                QuotaCostTrial[] left = rows.Skip(Math.Max(0, i - 24)).Take(Math.Min(24, i)).ToArray();
                QuotaCostTrial[] right = rows.Skip(i).Take(24).ToArray();
                if (left[^1].EndUtc > right[0].StartUtc ||
                    left.Concat(right).Zip(left.Concat(right).Skip(1)).Any(x => x.First.EndUtc > x.Second.StartUtc)) continue;
                (double Gain, double Before, double After)? fit = Shift(left.Select(Residual).ToArray(), right.Select(Residual).ToArray());
                if (fit is null) continue;
                if (adjusted is not null && left.Concat(right).All(x => adjusted.ContainsKey((x.StartUtc, x.EndUtc))))
                {
                    (double Gain, double Before, double After)? check = Shift(
                        left.Select(x => Residual(adjusted[(x.StartUtc, x.EndUtc)])).ToArray(),
                        right.Select(x => Residual(adjusted[(x.StartUtc, x.EndUtc)])).ToArray());
                    if (check is null || Math.Sign(check.Value.After - check.Value.Before) !=
                        Math.Sign(fit.Value.After - fit.Value.Before)) continue;
                }
                if (best is null || fit.Value.Gain > best.Value.Gain) best = (i, fit.Value.Gain, fit.Value.Before, fit.Value.After);
            }
            if (best is not { } boundary) continue;
            QuotaCostTrial[] before = rows.Skip(Math.Max(0, boundary.Index - 24)).Take(Math.Min(24, boundary.Index)).ToArray();
            QuotaCostTrial[] after = rows.Skip(boundary.Index).Take(24).ToArray();

            double? Rate(QuotaCostTrial[] trials)
            {
                return trials.Sum(x => x.Prediction) > .01 ? trials.Sum(x => x.ObservedDelta) / trials.Sum(x => x.Prediction) : null;
            }

            double? oldRate = Rate(before);
            double? newRate = Rate(after);
            candidates.Add(
                new CodexRegimeCandidate(
                    score.Cohort,
                    score.HorizonHours,
                    before[^1].StartUtc,
                    after[0].EndUtc,
                    boundary.Before,
                    boundary.After,
                    oldRate > .01 && newRate.HasValue ? newRate / oldRate : null,
                    before.Length,
                    after.Length,
                    $"Possible accounting relationship change under frozen {score.Candidate}; " +
                    (adjusted is null ? "time-pattern control unavailable. " : "time-ablation control checked where paired. ") +
                    "Retrospective inference only; not a causal provider-policy claim."));
        }
        return new CodexRegimeReport(Version, candidates, Methodology);
    }

    private static double Residual(QuotaCostTrial x)
    {
        return x.Prediction < x.LowerDelta ? x.LowerDelta - x.Prediction :
            x.Prediction > x.UpperDelta ? x.UpperDelta - x.Prediction : 0;
    }

    private static (double Gain, double Before, double After)? Shift(double[] left, double[] right)
    {
        if (left.Concat(right).Any(x => !double.IsFinite(x))) return null;
        double a = Median(left);
        double b = Median(right);
        double[] deviations = left.Select(x => Math.Abs(x - a)).Concat(right.Select(x => Math.Abs(x - b))).ToArray();
        if (Math.Abs(b - a) <= Math.Max(2, 3 * Median(deviations))) return null;
        double[] all = left.Concat(right).ToArray();
        double center = Median(all);
        double baseline = all.Sum(x => Math.Abs(x - center));
        double split = deviations.Sum();
        return baseline > 0 && split < baseline * .75 ? (baseline - split, a, b) : null;
    }

    private static double Median(double[] values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    internal static IReadOnlyList<DateTimeOffset> DetectPersistentShifts(IReadOnlyList<IReadOnlyList<QuotaCostTrial>> epochs)
    {
        // Freeze the reference after three held-out blocks; require two later blocks
        // with a replicated same-direction shift. This is a diagnostic, not a causal alarm.
        if (epochs.Count < 5) return [];
        double[] reference = epochs.Take(3).SelectMany(x => x).Select(x => x.Residual).ToArray();
        double center = ResidualQuantile(reference, 0.5)!.Value;
        double threshold = Math.Max(2, 3 * ResidualQuantile(reference.Select(x => Math.Abs(x - center)), 0.5)!.Value);
        var shifts = new List<DateTimeOffset>();
        for (int i = 4; i < epochs.Count; i++)
        {
            double a = ResidualQuantile(epochs[i - 1].Select(x => x.Residual), 0.5)!.Value - center;
            double b = ResidualQuantile(epochs[i].Select(x => x.Residual), 0.5)!.Value - center;
            if (epochs[i - 1].Count >= 3 && epochs[i].Count >= 3 && Math.Abs(a) > threshold &&
                Math.Abs(b) > threshold && Math.Sign(a) == Math.Sign(b)) shifts.Add(epochs[i][0].StartUtc);
        }
        return shifts;
    }

    private static double? ResidualQuantile(IEnumerable<double> values, double quantile)
    {
        double[] rows = values.Order().ToArray();
        return rows.Length == 0 ? null : rows[Math.Clamp((int)Math.Ceiling(rows.Length * quantile) - 1, 0, rows.Length - 1)];
    }
}