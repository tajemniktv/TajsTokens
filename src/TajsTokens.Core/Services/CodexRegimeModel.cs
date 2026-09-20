using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed record CodexRegimeCandidate(QuotaHistoryCohort Cohort, double HorizonHours,
    DateTimeOffset BoundaryLowerUtc, DateTimeOffset BoundaryUpperUtc, double BeforeResidual,
    double AfterResidual, double? RelativeRate, int BeforeSamples, int AfterSamples, string Explanation);
public sealed record CodexRegimeReport(string Version, IReadOnlyList<CodexRegimeCandidate> Candidates, string Methodology);

/// <summary>Retrospective residual segmentation. Does not assign durable regimes or relax live promotion.</summary>
public static class CodexRegimeModel
{
    public const string Version = "codex-retrospective-regime/v1";
    public const string Methodology = "Retrospective, not a live alert: both sides of each boundary use frozen held-out cost trials. " +
        "Measurement-envelope residuals; at least eight intervals on either side, robust median shift exceeding max(2pp, 3 MAD), " +
        "and at least 25% absolute-error reduction. Matched time-ablation residuals must corroborate a candidate when available. " +
        "A candidate is not proof of provider policy or a calibrated probability; coverage, model mix and reporting remain competing explanations. " +
        "Boundary bounds are adjacent observation origins, not a claimed exact change time. Production reset-generation gates remain unchanged.";

    public static CodexRegimeReport Analyze(QuotaCostReport report, CancellationToken token = default)
    {
        var candidates = new List<CodexRegimeCandidate>();
        foreach (var group in report.Scores.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            token.ThrowIfCancellationRequested();
            var score = group.FirstOrDefault(x => x.Candidate == "categories" && x.Trials.Count >= 16)
                ?? group.FirstOrDefault(x => x.Candidate == "total" && x.Trials.Count >= 16);
            if (score is null || score.Trials.Count > 10000) continue;
            var rows = score.Trials.OrderBy(x => x.StartUtc).ToArray();
            var time = group.FirstOrDefault(x => x.Candidate == "time-ablation");
            var adjusted = time?.Trials.ToDictionary(x => (x.StartUtc, x.EndUtc));
            // One strongest boundary per cohort/horizon; no repeated splitting until noise looks structural.
            (int Index, double Gain, double Before, double After)? best = null;
            for (var i = 8; i <= rows.Length - 8; i++)
            {
                token.ThrowIfCancellationRequested();
                var left = rows.Skip(Math.Max(0, i - 24)).Take(Math.Min(24, i)).ToArray();
                var right = rows.Skip(i).Take(24).ToArray();
                if (left[^1].EndUtc > right[0].StartUtc || left.Concat(right).Zip(left.Concat(right).Skip(1)).Any(x => x.First.EndUtc > x.Second.StartUtc)) continue;
                var fit = Shift(left.Select(Residual).ToArray(), right.Select(Residual).ToArray());
                if (fit is null) continue;
                if (adjusted is not null && left.Concat(right).All(x => adjusted.ContainsKey((x.StartUtc, x.EndUtc))))
                {
                    var check = Shift(left.Select(x => Residual(adjusted[(x.StartUtc, x.EndUtc)])).ToArray(),
                        right.Select(x => Residual(adjusted[(x.StartUtc, x.EndUtc)])).ToArray());
                    if (check is null || Math.Sign(check.Value.After - check.Value.Before) != Math.Sign(fit.Value.After - fit.Value.Before)) continue;
                }
                if (best is null || fit.Value.Gain > best.Value.Gain) best = (i, fit.Value.Gain, fit.Value.Before, fit.Value.After);
            }
            if (best is not { } boundary) continue;
            var before = rows.Skip(Math.Max(0, boundary.Index - 24)).Take(Math.Min(24, boundary.Index)).ToArray();
            var after = rows.Skip(boundary.Index).Take(24).ToArray();
            double? Rate(QuotaCostTrial[] trials) => trials.Sum(x => x.Prediction) > .01 ? trials.Sum(x => x.ObservedDelta) / trials.Sum(x => x.Prediction) : null;
            var oldRate = Rate(before); var newRate = Rate(after);
            candidates.Add(new(score.Cohort, score.HorizonHours, before[^1].StartUtc, after[0].EndUtc,
                boundary.Before, boundary.After, oldRate > .01 && newRate.HasValue ? newRate / oldRate : null,
                before.Length, after.Length, $"Possible accounting relationship change under frozen {score.Candidate}; " +
                (adjusted is null ? "time-pattern control unavailable. " : "time-ablation control checked where paired. ") +
                "Retrospective inference only; not a causal provider-policy claim."));
        }
        return new(Version, candidates, Methodology);
    }

    private static double Residual(QuotaCostTrial x) => x.Prediction < x.LowerDelta ? x.LowerDelta - x.Prediction :
        x.Prediction > x.UpperDelta ? x.UpperDelta - x.Prediction : 0;
    private static (double Gain, double Before, double After)? Shift(double[] left, double[] right)
    {
        if (left.Concat(right).Any(x => !double.IsFinite(x))) return null;
        var a = Median(left); var b = Median(right);
        var deviations = left.Select(x => Math.Abs(x - a)).Concat(right.Select(x => Math.Abs(x - b))).ToArray();
        if (Math.Abs(b - a) <= Math.Max(2, 3 * Median(deviations))) return null;
        var all = left.Concat(right).ToArray(); var center = Median(all);
        var baseline = all.Sum(x => Math.Abs(x - center)); var split = deviations.Sum();
        return baseline > 0 && split < baseline * .75 ? (baseline - split, a, b) : null;
    }
    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
