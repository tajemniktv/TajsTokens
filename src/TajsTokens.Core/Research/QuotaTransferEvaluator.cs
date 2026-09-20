using TajsTokens.Core.Models;

using TajsTokens.Core.Services;

namespace TajsTokens.Core.Research;

/// <summary>Explicit cross-regime research only. Never supplies a live model or creates TT units.</summary>
public static class QuotaTransferEvaluator
{
    public static QuotaTransferEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default) =>
        Evaluate(QuotaCostObservationBuilder.Build(data, cancellationToken), cancellationToken);

    public static QuotaTransferEvaluation Evaluate(IReadOnlyList<QuotaCostObservation> observations,
        CancellationToken cancellationToken = default)
    {
        var groups = observations.GroupBy(x => (x.Cohort, x.HorizonHours))
            .Select(x => (x.Key, Rows: x.OrderBy(y => y.StartUtc).ToArray())).ToArray();
        var scores = new List<QuotaTransferScore>();
        foreach (var destination in groups)
        foreach (var source in groups.Where(x => Compatible(x.Key.Cohort, destination.Key.Cohort) &&
                     x.Key.HorizonHours == destination.Key.HorizonHours))
        {
            var sourceTraining = source.Rows.Where(x => x.EndUtc <= destination.Rows[0].StartUtc).TakeLast(120).ToArray();
            var destinationTraining = destination.Rows.Take(20).ToArray();
            foreach (var model in new[] { "total", "categories", "model-effort" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var heldout = destination.Rows.Skip(20).Where(x => x.StartUtc >= destinationTraining[^1].EndUtc).ToArray();
                var generations = ChronologicalEvidence.Blocks(heldout, x => x.StartUtc, x => x.EndUtc, x => x.ResetUtc);
                if (sourceTraining.Length < 20 || destinationTraining.Length < 20 || heldout.Length == 0)
                {
                    scores.Add(new(source.Key.Cohort, destination.Key.Cohort, destination.Key.HorizonHours, model,
                        sourceTraining.Length, destinationTraining.Length, heldout.Length, QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                        0, null, null, null, false, "insufficient-chronological-transfer-evidence"));
                    continue;
                }
                if (model != "total" && sourceTraining.Concat(destinationTraining).Concat(heldout).Any(x => !x.HasCompleteTokenCategories))
                {
                    scores.Add(new(source.Key.Cohort, destination.Key.Cohort, destination.Key.HorizonHours, model,
                        sourceTraining.Length, destinationTraining.Length, heldout.Length, QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                        0, null, null, null, false, "incomplete-category-transfer-evidence"));
                    continue;
                }
                if (!QuotaAccountingModel.HasTrainingWork(sourceTraining) || !QuotaAccountingModel.HasTrainingWork(destinationTraining))
                {
                    scores.Add(new(source.Key.Cohort, destination.Key.Cohort, destination.Key.HorizonHours, model,
                        sourceTraining.Length, destinationTraining.Length, heldout.Length, QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                        0, null, null, null, false, "no-recorded-training-work"));
                    continue;
                }
                var transferred = QuotaAccountingModel.FitFrozen(sourceTraining, model, cancellationToken);
                var local = QuotaAccountingModel.FitFrozen(destinationTraining, model, cancellationToken);
                var predictions = destinationTraining.Select(transferred).ToArray();
                var scale = FitScale(predictions, destinationTraining);
                var directLoss = generations.Average(g => g.Average(x => x.IntervalLoss(transferred(x))));
                var scaledLoss = generations.Average(g => g.Average(x => x.IntervalLoss(transferred(x) * scale)));
                var localLoss = generations.Average(g => g.Average(x => x.IntervalLoss(local(x))));
                var supported = sourceTraining.Concat(destinationTraining).Concat(heldout)
                    .All(x => !x.QualityFlags.Contains("meter-precision-unverified-sensitivity-only"));
                var improvement = supported && heldout.Length >= 16 && generations.Count >= 3 &&
                    scaledLoss + 0.1 < localLoss && scaledLoss < localLoss * 0.9 &&
                    generations.Count(g => g.Average(x => x.IntervalLoss(transferred(x) * scale)) <
                        g.Average(x => x.IntervalLoss(local(x)))) > generations.Count / 2;
                scores.Add(new(source.Key.Cohort, destination.Key.Cohort, destination.Key.HorizonHours, model,
                    sourceTraining.Length, destinationTraining.Length, heldout.Length, QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                    scale, directLoss, scaledLoss, localLoss, improvement,
                    improvement ? "research-transfer-improvement-not-a-TT-unit" : "transfer-not-supported"));
            }
        }
        return new("quota-transfer/v4-blocks", "Explicit source/destination comparisons only within the same recorded account, source and session lineage. " +
            "Source weights use only intervals completed before the destination era. First 20 destination intervals fit one nonnegative scale and a local-only competitor. " +
            "Later disjoint intervals compare unscaled source weights, scale-only transfer and destination-local weights with block-balanced interval loss. " +
            "No cross-account inference, automatic transfer or TT currency. Missing compatible regimes are no evidence, not successful transfer.", scores);
    }

    private static bool Compatible(QuotaHistoryCohort source, QuotaHistoryCohort destination) =>
        source != destination && source.AccountKey is not null && source.AccountKey == destination.AccountKey &&
        source.Provider == destination.Provider && source.Profile == destination.Profile &&
        source.Source == destination.Source && source.SessionId == destination.SessionId;

    private static double FitScale(double[] predictions, IReadOnlyList<QuotaCostObservation> rows)
    {
        var curvature = predictions.Average(x => x * x);
        if (curvature <= 1e-12) return 0;
        var scale = 1d;
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var gradient = predictions.Select((x, i) => x * (x * scale - Math.Clamp(x * scale, rows[i].LowerDelta, rows[i].UpperDelta))).Average();
            var next = Math.Max(0, scale - gradient / curvature);
            if (Math.Abs(next - scale) < 1e-9) return next;
            scale = next;
        }
        return scale;
    }
}
