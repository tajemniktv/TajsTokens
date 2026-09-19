using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class TtEvaluator
{
    public const string Version = "tt-lab/v2";
    public const string Methodology = "Experimental local index, reconstructed completed-work cost evaluation, not a forecast. " +
        "First 20 compatible known-account intervals freeze nonnegative category weights. Reference is one million tokens " +
        "in the first positive training interval's category mix; models/efforts are pooled within observed support, not separately priced. " +
        "Basis identity covers exact weights, reference, supported dimensions and token semantics. Tier/context are unmodelled. " +
        "Next 20 disjoint intervals fit a separate nonnegative quota/TT scale and raw-token/full-vector competitors. " +
        "Later matched supported intervals report reset-balanced measurement-envelope loss. Unknown model/effort/category work is withheld, not zero. " +
        "Transfer trials reuse an earlier cohort's exact basis and fit the first 20 destination intervals separately. " +
        "Only the same recorded account/provider/profile/source/session lineage and horizon may pair; the source cohort must end before the destination starts. " +
        "Pairs are reported evidence contexts, not proof of provider-policy changes. No cross-user comparability is established. Basis is a reproducible research result, " +
        "saved Model Lab snapshots preserve original aggregate research output, not per-task scoring history. " +
        "Later reconstruction can create a different basis and must be labelled restatement. " +
        "No subscription balance, credit equivalence, live promotion or original-forecast rewriting.";

    public static TtEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default) =>
        Evaluate(QuotaCostObservationBuilder.Build(data, cancellationToken), cancellationToken);

    public static TtEvaluation Evaluate(IReadOnlyList<QuotaCostObservation> rows, CancellationToken cancellationToken = default)
    {
        var scores = new List<TtEvaluationScore>();
        var groups = rows.Where(x => x.Cohort.AccountKey is not null).GroupBy(x => (x.Cohort, x.HorizonHours))
            .Select(x => (x.Key.Cohort, x.Key.HorizonHours, Rows: x.OrderBy(y => y.StartUtc).ToArray())).ToArray();
        var comparisons = groups.Select(g => (Source: g, Destination: g, Transfer: false)).Concat(
            from source in groups
            from destination in groups
            where source.Cohort != destination.Cohort && source.HorizonHours == destination.HorizonHours &&
                source.Cohort.AccountKey == destination.Cohort.AccountKey && source.Cohort.Provider == destination.Cohort.Provider &&
                source.Cohort.Profile == destination.Cohort.Profile && source.Cohort.Source == destination.Cohort.Source &&
                source.Cohort.SessionId == destination.Cohort.SessionId && source.Rows.Max(x => x.EndUtc) <= destination.Rows[0].StartUtc
            select (Source: source, Destination: destination, Transfer: true));
        foreach (var comparison in comparisons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = comparison.Destination.Rows;
            var training = comparison.Source.Rows.Take(20).ToArray();
            TtWorkloadBasis? basis = null;
            if (training.Length == 20 && training.All(TtWorkloadBasis.CompleteCategories) &&
                training.FirstOrDefault(x => x.TokenCategories.Sum() > 0) is { } reference)
            {
                var weights = QuotaCostEvaluation.FitCategoryWeights(training, cancellationToken);
                var basket = reference.TokenCategories.Select(x => x / reference.TokenCategories.Sum() * 1e6).ToArray();
                if (basket.Select((x, i) => x * weights[i]).Sum() > 0)
                    basis = new(weights, basket, Enumerable.Range(0, 5).Select(i => training.Any(x => x.TokenCategories[i] > 0)),
                        training.SelectMany(x => x.Features.ModelTokenShares.Keys), training.SelectMany(x => x.Features.EffortTokenShares.Keys));
                if (basis is not null && training.Any(x => basis.Score(x) is null)) basis = null;
            }
            var calibration = ordered.Skip(comparison.Transfer ? 0 : 20).Where(x => x.StartUtc >= training[^1].EndUtc).Take(20).ToArray();
            var heldout = calibration.Length == 20 ? ordered.Where(x => x.StartUtc >= calibration[^1].EndUtc).ToArray() : [];
            var supported = heldout.Where(x => basis?.Score(x) is not null).ToArray();
            var unsupported = heldout.Except(supported).ToArray();
            var generations = QuotaResetGenerationPolicy.Group(supported, x => x.ResetUtc);
            var ready = basis is not null && calibration.Length == 20 && calibration.All(x => basis.Score(x) is not null);
            double? scale = null, scalarLoss = null, vectorLoss = null, rawLoss = null;
            double? scalarMae = null, vectorMae = null, rawMae = null, zeroLoss = null;
            if (ready)
            {
                var quantities = calibration.Select(x => basis!.Score(x)!.Value).ToArray();
                var curvature = quantities.Average(x => x * x);
                if (curvature > 0)
                {
                    var alpha = 0d;
                    for (var i = 0; i < 1000; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var gradient = quantities.Select((x, j) => x * (x * alpha - Math.Clamp(x * alpha, calibration[j].LowerDelta, calibration[j].UpperDelta))).Average();
                        var next = Math.Max(0, alpha - gradient / curvature);
                        if (Math.Abs(next - alpha) < 1e-10) { alpha = next; break; }
                        alpha = next;
                    }
                    scale = alpha;
                    var full = QuotaCostEvaluation.FitFrozen(calibration, "model-effort", cancellationToken);
                    var raw = QuotaCostEvaluation.FitFrozen(calibration, "total", cancellationToken);
                    if (generations.Count > 0)
                    {
                        scalarLoss = generations.Average(g => g.Average(x => x.IntervalLoss(alpha * basis!.Score(x)!.Value)));
                        vectorLoss = generations.Average(g => g.Average(x => x.IntervalLoss(full(x))));
                        rawLoss = generations.Average(g => g.Average(x => x.IntervalLoss(raw(x))));
                        scalarMae = generations.Average(g => g.Average(x => Math.Abs(alpha * basis!.Score(x)!.Value - x.ObservedDelta)));
                        vectorMae = generations.Average(g => g.Average(x => Math.Abs(full(x) - x.ObservedDelta)));
                        rawMae = generations.Average(g => g.Average(x => Math.Abs(raw(x) - x.ObservedDelta)));
                        zeroLoss = generations.Average(g => g.Average(x => x.IntervalLoss(0)));
                    }
                }
            }
            scores.Add(new(comparison.Destination.Cohort, comparison.Destination.HorizonHours, basis,
                basis is null ? "insufficient-unsupported-or-degenerate-basis" : !ready ? "insufficient-or-unsupported-calibration" :
                scale is null ? "zero-calibration-work" : supported.Length == 0 ? "no-supported-heldout-work" : "research-only-not-promoted",
                training.Length, calibration.Length, supported.Length, unsupported.Length, unsupported.Sum(x => (double)x.Features.Tokens),
                generations.Count, scale, supported.Length > 0 ? supported.Sum(x => basis!.Score(x)!.Value) : null,
                scalarLoss, vectorLoss, rawLoss)
            {
                ScalarMae = scalarMae, FullVectorMae = vectorMae, RawTokenMae = rawMae, ZeroLoss = zeroLoss,
                BasisCohort = comparison.Source.Cohort, IsTransfer = comparison.Transfer,
                BasisEndUtc = training[^1].EndUtc, CalibrationEndUtc = calibration.LastOrDefault()?.EndUtc
            });
        }
        return new(Version, Methodology, scores);
    }
}
