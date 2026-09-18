using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Earned live integration; unsupported composition never replaces the incumbent.</summary>
public static class ComposedQuotaPolicy
{
    public static IReadOnlyList<QuotaHorizonPrediction> Apply(CodexForecastDataset data, QuotaSnapshot anchor,
        IReadOnlyList<QuotaHorizonPrediction> incumbent, CancellationToken cancellationToken = default)
    {
        if (anchor.AccountKey is null || incumbent.Count == 0) return incumbent;
        var prefix = data with
        {
            CapturedAtUtc = anchor.CapturedAtUtc,
            Quota = data.Quota.Where(x => QuotaHistoryPolicy.Cohort(x) == QuotaHistoryPolicy.Cohort(anchor) &&
                x.CapturedAtUtc <= anchor.CapturedAtUtc && (x.CollectedAtUtc is null || x.CollectedAtUtc <= anchor.CapturedAtUtc)).ToArray(),
            Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= anchor.CapturedAtUtc && x.CapturedAtUtc <= anchor.CapturedAtUtc).ToArray(),
            Workload = data.Workload.Where(x => x.ObservedAtUtc <= anchor.CapturedAtUtc && x.CapturedAtUtc <= anchor.CapturedAtUtc).ToArray(),
            Context = data.Context.Where(x => x.ObservedAtUtc <= anchor.CapturedAtUtc && x.CapturedAtUtc <= anchor.CapturedAtUtc).ToArray()
        };
        var cost = QuotaCostEvaluation.Evaluate(prefix, cancellationToken);
        var eligible = cost.Scores.Where(x => x.MaterialWin && x.CandidateShiftResets.Count == 0 &&
            x.Candidate is "total" or "categories" or "model-effort").ToArray();
        if (eligible.Length == 0) return incumbent;
        var evaluation = ComposedQuotaEvaluator.Evaluate(prefix, cancellationToken);
        var observations = QuotaCostObservationBuilder.Build(prefix, cancellationToken);
        return incumbent.Select(baseline =>
        {
            var candidates = evaluation.Scores.Where(score => score.HorizonHours == baseline.HorizonHours &&
                eligible.Any(c => c.HorizonHours == score.HorizonHours && c.Candidate == score.CostModel) &&
                SupportsSelection(score, anchor.CapturedAtUtc)).OrderBy(x => x.IntervalLoss).ToArray();
            if (candidates.Length == 0) return baseline;
            var selected = candidates[0];
            var total = evaluation.Scores.FirstOrDefault(x => x.HorizonHours == selected.HorizonHours && x.CostModel == "total");
            if (selected.CostModel != "total" && (total?.IntervalLoss is not { } totalLoss ||
                selected.IntervalLoss + 0.1 >= totalLoss || selected.IntervalLoss >= totalLoss * 0.9)) return baseline;
            var workload = TokenWorkloadPredictionService.PredictHorizon(prefix, anchor.CapturedAtUtc,
                baseline.HorizonHours, ForecastReplayAvailability.CollectedByOrigin, cancellationToken)?.Composition;
            if (workload is null) return baseline;
            var training = observations.Where(x => x.HorizonHours == baseline.HorizonHours).OrderBy(x => x.StartUtc).Take(20).ToArray();
            if (training.Length < 20) return baseline;
            if (workload.ModelShares.Keys.Except(training.SelectMany(x => x.Features.ModelTokenShares.Keys)).Any() ||
                workload.EffortShares.Keys.Except(training.SelectMany(x => x.Features.EffortTokenShares.Keys)).Any()) return baseline;
            var fit = QuotaCostEvaluation.FitFrozen(training, selected.CostModel, cancellationToken);
            var predicted = fit(ComposedQuotaEvaluator.Project(training[0], workload));
            var errors = QuotaResetGenerationPolicy.Group(selected.Trials.Where(x => x.ResetUtc < anchor.CapturedAtUtc), x => x.ResetUtc)
                .Select(g => g.Max(x => Math.Abs(x.PredictedDelta - x.ObservedDelta))).Order().ToArray();
            if (errors.Length < 8) return baseline;
            var radius = Math.Max(1, errors[Math.Min(errors.Length - 1, (int)Math.Ceiling((errors.Length + 1) * 0.8) - 1)]);
            var remaining = Math.Clamp(anchor.RemainingPercent!.Value - predicted, 0, anchor.RemainingPercent.Value);
            return baseline with
            {
                RemainingPercent = remaining, ExpectedUsagePercent = anchor.RemainingPercent.Value - remaining,
                Model = ComposedQuotaEvaluator.Version + "/" + selected.CostModel, UsesWorkload = true,
                TrainingSamples = training.Length, ValidationSamples = selected.HeldOutIntervals,
                ValidationMeanAbsoluteError = selected.DisplayedDeltaMae,
                LowerRemainingPercent = Math.Max(0, remaining - radius),
                UpperRemainingPercent = Math.Min(anchor.RemainingPercent.Value, remaining + radius),
                IntervalSamples = errors.Length,
                Explanation = "Origin-only predicted workload composition through frozen account/cohort-local cost weights earned selection over pace and incumbent. " +
                    $"{selected.HeldOutIntervals} held-out outcomes; {selected.ResetGenerations} reset generations. " +
                    "Empirical 80%-target range from completed-generation maximum errors; not an exhaustion probability or guarantee. " +
                    "Unobserved account activity remains unexplained; unsupported/stale composition falls back to the incumbent."
            };
        }).ToArray();
    }

    public static bool SupportsSelection(ComposedQuotaScore score, DateTimeOffset origin)
    {
        if (score.HeldOutIntervals < 16 || score.ResetGenerations < 8 || score.MissingComposition > score.HeldOutIntervals ||
            score.Trials.Count == 0 || score.Trials.Any(x => x.OutcomeUtc > origin || x.IncumbentIntervalLoss is null) ||
            score.Trials.Max(x => x.OutcomeUtc) < origin.AddHours(-Math.Max(2, score.HorizonHours * 2))) return false;
        var generations = QuotaResetGenerationPolicy.Group(score.Trials, x => x.ResetUtc);
        if (generations.Count < 8 || score.Trials.Count < 16) return false;
        var loss = generations.Average(g => g.Average(x => x.IntervalLoss));
        var incumbentLoss = generations.Average(g => g.Average(x => x.IncumbentIntervalLoss!.Value));
        var paceLoss = generations.Average(g => g.Average(x => x.PaceIntervalLoss));
        return loss + 0.1 < incumbentLoss && loss < incumbentLoss * 0.9 &&
            loss + 0.1 < paceLoss && loss < paceLoss * 0.9 &&
            generations.Count(g => g.Average(x => x.IntervalLoss) < g.Average(x => x.IncumbentIntervalLoss!.Value)) > generations.Count / 2;
    }
}
