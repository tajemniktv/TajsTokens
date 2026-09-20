// Taj's Tokens | QuotaTransferEvaluator.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Core.Research;

/// <summary>Explicit cross-regime research only. Never supplies a live model or creates TT units.</summary>
public static class QuotaTransferEvaluator
{
    public static QuotaTransferEvaluation Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default)
    {
        return Evaluate(QuotaCostObservationBuilder.Build(data, cancellationToken), cancellationToken);
    }

    public static QuotaTransferEvaluation Evaluate(
        IReadOnlyList<QuotaCostObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ((QuotaHistoryCohort Cohort, double HorizonHours) Key, QuotaCostObservation[] Rows)[] groups = observations
            .GroupBy(x => (x.Cohort, x.HorizonHours))
            .Select(x => (x.Key, Rows: x.OrderBy(y => y.StartUtc).ToArray())).ToArray();
        var scores = new List<QuotaTransferScore>();
        foreach (((QuotaHistoryCohort Cohort, double HorizonHours) Key, QuotaCostObservation[] Rows) destination in groups)
        foreach (((QuotaHistoryCohort Cohort, double HorizonHours) Key, QuotaCostObservation[] Rows) source in groups.Where(x =>
                     Compatible(x.Key.Cohort, destination.Key.Cohort) &&
                     x.Key.HorizonHours == destination.Key.HorizonHours))
        {
            QuotaCostObservation[] sourceTraining =
                source.Rows.Where(x => x.EndUtc <= destination.Rows[0].StartUtc).TakeLast(120).ToArray();
            QuotaCostObservation[] destinationTraining = destination.Rows.Take(20).ToArray();
            foreach (string model in new[] { "total", "categories", "model-effort" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                QuotaCostObservation[] heldout = destination.Rows.Skip(20).Where(x => x.StartUtc >= destinationTraining[^1].EndUtc)
                    .ToArray();
                IReadOnlyList<IReadOnlyList<QuotaCostObservation>> generations = ChronologicalEvidence.Blocks(
                    heldout,
                    x => x.StartUtc,
                    x => x.EndUtc,
                    x => x.ResetUtc);
                if (sourceTraining.Length < 20 || destinationTraining.Length < 20 || heldout.Length == 0)
                {
                    scores.Add(
                        new QuotaTransferScore(
                            source.Key.Cohort,
                            destination.Key.Cohort,
                            destination.Key.HorizonHours,
                            model,
                            sourceTraining.Length,
                            destinationTraining.Length,
                            heldout.Length,
                            QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                            0,
                            null,
                            null,
                            null,
                            false,
                            "insufficient-chronological-transfer-evidence"));
                    continue;
                }
                if (model != "total" && sourceTraining.Concat(destinationTraining).Concat(heldout).Any(x => !x.HasCompleteTokenCategories))
                {
                    scores.Add(
                        new QuotaTransferScore(
                            source.Key.Cohort,
                            destination.Key.Cohort,
                            destination.Key.HorizonHours,
                            model,
                            sourceTraining.Length,
                            destinationTraining.Length,
                            heldout.Length,
                            QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                            0,
                            null,
                            null,
                            null,
                            false,
                            "incomplete-category-transfer-evidence"));
                    continue;
                }
                if (!QuotaAccountingModel.HasTrainingWork(sourceTraining) || !QuotaAccountingModel.HasTrainingWork(destinationTraining))
                {
                    scores.Add(
                        new QuotaTransferScore(
                            source.Key.Cohort,
                            destination.Key.Cohort,
                            destination.Key.HorizonHours,
                            model,
                            sourceTraining.Length,
                            destinationTraining.Length,
                            heldout.Length,
                            QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                            0,
                            null,
                            null,
                            null,
                            false,
                            "no-recorded-training-work"));
                    continue;
                }
                Func<QuotaCostObservation, double> transferred = QuotaAccountingModel.FitFrozen(sourceTraining, model, cancellationToken);
                Func<QuotaCostObservation, double> local = QuotaAccountingModel.FitFrozen(destinationTraining, model, cancellationToken);
                double[] predictions = destinationTraining.Select(transferred).ToArray();
                double scale = FitScale(predictions, destinationTraining);
                double directLoss = generations.Average(g => g.Average(x => x.IntervalLoss(transferred(x))));
                double scaledLoss = generations.Average(g => g.Average(x => x.IntervalLoss(transferred(x) * scale)));
                double localLoss = generations.Average(g => g.Average(x => x.IntervalLoss(local(x))));
                bool supported = sourceTraining.Concat(destinationTraining).Concat(heldout)
                    .All(x => !x.QualityFlags.Contains("meter-precision-unverified-sensitivity-only"));
                bool improvement = supported && heldout.Length >= 16 && generations.Count >= 3 &&
                                   scaledLoss + 0.1 < localLoss && scaledLoss < localLoss * 0.9 &&
                                   generations.Count(g => g.Average(x => x.IntervalLoss(transferred(x) * scale)) <
                                                          g.Average(x => x.IntervalLoss(local(x)))) > generations.Count / 2;
                scores.Add(
                    new QuotaTransferScore(
                        source.Key.Cohort,
                        destination.Key.Cohort,
                        destination.Key.HorizonHours,
                        model,
                        sourceTraining.Length,
                        destinationTraining.Length,
                        heldout.Length,
                        QuotaResetGenerationPolicy.Group(heldout, x => x.ResetUtc).Count,
                        scale,
                        directLoss,
                        scaledLoss,
                        localLoss,
                        improvement,
                        improvement ? "research-transfer-improvement-not-a-TT-unit" : "transfer-not-supported"));
            }
        }
        return new QuotaTransferEvaluation(
            "quota-transfer/v4-blocks",
            "Explicit source/destination comparisons only within the same recorded account, source and session lineage. " +
            "Source weights use only intervals completed before the destination era. First 20 destination intervals fit one nonnegative scale and a local-only competitor. " +
            "Later disjoint intervals compare unscaled source weights, scale-only transfer and destination-local weights with block-balanced interval loss. " +
            "No cross-account inference, automatic transfer or TT currency. Missing compatible regimes are no evidence, not successful transfer.",
            scores);
    }

    private static bool Compatible(QuotaHistoryCohort source, QuotaHistoryCohort destination)
    {
        return source != destination && source.AccountKey is not null && source.AccountKey == destination.AccountKey &&
               source.Provider == destination.Provider && source.Profile == destination.Profile &&
               source.Source == destination.Source && source.SessionId == destination.SessionId;
    }

    private static double FitScale(double[] predictions, IReadOnlyList<QuotaCostObservation> rows)
    {
        double curvature = predictions.Average(x => x * x);
        if (curvature <= 1e-12) return 0;
        double scale = 1d;
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            double gradient = predictions.Select((x, i) => x * (x * scale - Math.Clamp(x * scale, rows[i].LowerDelta, rows[i].UpperDelta)))
                .Average();
            double next = Math.Max(0, scale - gradient / curvature);
            if (Math.Abs(next - scale) < 1e-9) return next;
            scale = next;
        }
        return scale;
    }
}