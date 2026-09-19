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
            Quota = data.Quota.Where(x => (QuotaHistoryPolicy.Cohort(x) == QuotaHistoryPolicy.Cohort(anchor) ||
                RolloutAccountAssociationPolicy.Resolve(x, data.AccountAssociations, anchor.CapturedAtUtc)?.AccountKey == anchor.AccountKey) &&
                x.CapturedAtUtc <= anchor.CapturedAtUtc && (x.CollectedAtUtc is null || x.CollectedAtUtc <= anchor.CapturedAtUtc)).ToArray(),
            Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= anchor.CapturedAtUtc && x.CapturedAtUtc <= anchor.CapturedAtUtc).ToArray(),
            Workload = data.Workload.Where(x => x.ObservedAtUtc <= anchor.CapturedAtUtc && x.CapturedAtUtc <= anchor.CapturedAtUtc).ToArray(),
            Context = data.Context.Where(x => x.ObservedAtUtc <= anchor.CapturedAtUtc && x.CapturedAtUtc <= anchor.CapturedAtUtc).ToArray()
        };
        var cost = QuotaCostEvaluation.Evaluate(prefix, cancellationToken);
        var eligible = cost.Scores.Where(x => x.Cohort == QuotaHistoryPolicy.Cohort(anchor) && x.MaterialWin && x.CandidateShiftResets.Count == 0 &&
            x.Candidate is "total" or "categories" or "model-effort").ToArray();
        if (eligible.Length == 0 && prefix.AccountAssociations.Count == 0) return incumbent;
        // Extra short-horizon research does not expand the released forecast/promotion surface.
        var evaluation = ComposedQuotaEvaluator.Evaluate(prefix, cancellationToken, horizons: [.5, 2d]);
        var strict = ComposedQuotaEvaluator.Evaluate(prefix, cancellationToken, ForecastReplayAvailability.CollectedByOrigin, [.5, 2d]);
        var observations = QuotaCostObservationBuilder.Build(prefix, cancellationToken);
        return incumbent.Select(baseline =>
        {
            var candidates = evaluation.Scores.Where(score => score.Cohort == QuotaHistoryPolicy.Cohort(anchor) && score.HorizonHours == baseline.HorizonHours &&
                (eligible.Any(c => c.HorizonHours == score.HorizonHours && c.Candidate == score.CostModel) ||
                 SupportsAssertedCost(score, evaluation.Scores)) &&
                SupportsSelection(score, anchor.CapturedAtUtc) && strict.Scores.Any(s => s.Cohort == score.Cohort &&
                    s.HorizonHours == score.HorizonHours && s.CostModel == score.CostModel && SupportsStrictSelection(s, anchor.CapturedAtUtc)))
                .OrderBy(x => x.IntervalLoss).ToArray();
            if (candidates.Length == 0) return baseline;
            var selected = candidates[0];
            var liveEvidence = strict.Scores.Single(x => x.Cohort == selected.Cohort && x.HorizonHours == selected.HorizonHours && x.CostModel == selected.CostModel);
            var total = evaluation.Scores.FirstOrDefault(x => x.Cohort == selected.Cohort && x.HorizonHours == selected.HorizonHours && x.CostModel == "total");
            if (selected.CostModel != "total" && (total is null || !selected.Trials.Select(x => (x.OriginUtc, x.OutcomeUtc))
                .SequenceEqual(total.Trials.Select(x => (x.OriginUtc, x.OutcomeUtc))))) return baseline;
            if (selected.CostModel != "total" && (total?.IntervalLoss is not { } totalLoss ||
                selected.IntervalLoss + 0.1 >= totalLoss || selected.IntervalLoss >= totalLoss * 0.9)) return baseline;
            var workload = TokenWorkloadPredictionService.PredictHorizon(prefix, anchor.CapturedAtUtc,
                baseline.HorizonHours, ForecastReplayAvailability.CollectedByOrigin, cancellationToken)?.Composition;
            if (workload is null) return baseline;
            var training = QuotaCostTrainingPolicy.Select(observations, selected.Cohort, baseline.HorizonHours, selected.AssertedTrainingIntervals > 0);
            if (training.Length < 20) return baseline;
            if (selected.CostModel.Replace("asserted-", "", StringComparison.Ordinal) != "total" &&
                training.Any(x => !x.HasCompleteTokenCategories)) return baseline;
            if (workload.ModelShares.Keys.Except(training.SelectMany(x => x.Features.ModelTokenShares.Keys)).Any() ||
                workload.EffortShares.Keys.Except(training.SelectMany(x => x.Features.EffortTokenShares.Keys)).Any()) return baseline;
            var fit = QuotaCostEvaluation.FitFrozen(training, selected.CostModel.Replace("asserted-", "", StringComparison.Ordinal), cancellationToken);
            var predicted = fit(ComposedQuotaEvaluator.Project(training[0], workload));
            var calibration = CalibrateUncertainty(liveEvidence.Trials, anchor.CapturedAtUtc);
            if (calibration.Radius is not { } radius) return baseline;
            var remaining = Math.Clamp(anchor.RemainingPercent!.Value - predicted, 0, anchor.RemainingPercent.Value);
            return baseline with
            {
                RemainingPercent = remaining, ExpectedUsagePercent = anchor.RemainingPercent.Value - remaining,
                Model = ComposedQuotaEvaluator.Version + "/" + selected.CostModel, UsesWorkload = true,
                TrainingSamples = training.Length, ValidationSamples = selected.HeldOutIntervals,
                ValidationMeanAbsoluteError = selected.DisplayedDeltaMae,
                LowerRemainingPercent = Math.Max(0, remaining - radius),
                UpperRemainingPercent = Math.Min(anchor.RemainingPercent.Value, remaining + radius),
                IntervalSamples = calibration.Generations,
                Explanation = "Origin-only predicted workload composition through frozen account/cohort-local cost weights earned selection over pace and incumbent. " +
                    $"{selected.HeldOutIntervals} retrospective outcomes; {liveEvidence.HeldOutIntervals} collection-time outcomes across {liveEvidence.ResetGenerations} reset generations; {selected.AssertedTrainingIntervals} user-asserted training intervals. " +
                    "Empirical 80%-target range from completed-generation maximum errors; not an exhaustion probability or guarantee. " +
                    "Unobserved account activity remains unexplained; unsupported/stale composition falls back to the incumbent."
            };
        }).ToArray();
    }

    // Caller supplies a single source/account/cohort/horizon/model. Calibrate combined workload
    // and cost error directly; never multiply separate uncertainty endpoints.
    public static (double? Radius, int Generations) CalibrateUncertainty(
        IEnumerable<ComposedQuotaTrial> trials, DateTimeOffset origin)
    {
        var errors = QuotaResetGenerationPolicy.Group(trials.Where(x =>
                x.OutcomeUtc <= origin && x.CalibrationAvailableAtUtc <= origin &&
                x.ResetUtc < origin - QuotaResetGenerationPolicy.Tolerance), x => x.ResetUtc)
            .Select(g => g.Max(x => Math.Abs(x.PredictedDelta - x.ObservedDelta))).Order().ToArray();
        return (errors.Length < 8 ? null : Math.Max(1,
            errors[Math.Min(errors.Length - 1, (int)Math.Ceiling((errors.Length + 1) * .8) - 1)]), errors.Length);
    }

    public static bool SupportsSelection(ComposedQuotaScore score, DateTimeOffset origin)
        => AssessSelection(score, origin).Passed;

    /// <summary>Shared selection gate evidence, not a claim that all live promotion gates passed.</summary>
    public static ComposedSelectionAssessment AssessSelection(ComposedQuotaScore score, DateTimeOffset origin, bool requireStrict = false)
    {
        var reasons = new List<string>();
        var generations = QuotaResetGenerationPolicy.Group(score.Trials, x => x.ResetUtc);
        var pairs = score.Trials.Count(x => x.IncumbentIntervalLoss is not null);
        if (score.HeldOutIntervals < 16 || score.Trials.Count < 16) reasons.Add("At least 16 held-out outcomes are required.");
        if (score.ResetGenerations < 8 || generations.Count < 8) reasons.Add("At least eight independent reset generations are required.");
        if (score.MissingComposition > score.HeldOutIntervals) reasons.Add("Withheld intervals exceed evaluated outcomes.");
        if (score.Trials.Any(x => x.OutcomeUtc > origin)) reasons.Add("Some outcomes occur after the assessment time.");
        if (score.Trials.Count == 0) reasons.Add("No held-out trials are available.");
        else if (score.Trials.Max(x => x.OutcomeUtc) < origin.AddHours(-Math.Max(2, score.HorizonHours * 2)))
            reasons.Add("Recent completed outcomes are missing.");
        if (pairs != score.Trials.Count) reasons.Add("Not every outcome has a matched incumbent prediction.");
        double? loss = generations.Count == 0 ? null : generations.Average(g => g.Average(x => x.IntervalLoss));
        double? paceLoss = generations.Count == 0 ? null : generations.Average(g => g.Average(x => x.PaceIntervalLoss));
        double? incumbentLoss = generations.Count == 0 || pairs != score.Trials.Count ? null :
            generations.Average(g => g.Average(x => x.IncumbentIntervalLoss!.Value));
        if (loss is { } l && paceLoss is { } p && !(l + .1 < p && l < p * .9))
            reasons.Add("The reset-balanced improvement over pace is not both greater than 0.1pp and 10%.");
        if (loss is { } il && incumbentLoss is { } b)
        {
            if (!(il + .1 < b && il < b * .9)) reasons.Add("The reset-balanced improvement over incumbent is not both greater than 0.1pp and 10%.");
            if (generations.Count(g => g.Average(x => x.IntervalLoss) < g.Average(x => x.IncumbentIntervalLoss!.Value)) <= generations.Count / 2)
                reasons.Add("The challenger does not beat incumbent in a majority of reset generations.");
        }
        if (requireStrict)
        {
            if (score.Availability != ForecastReplayAvailability.CollectedByOrigin || score.Trials.Any(x =>
                x.Availability != ForecastReplayAvailability.CollectedByOrigin || x.Workload.Availability != ForecastReplayAvailability.CollectedByOrigin))
                reasons.Add("Collection-time replay is required; reconstructed history cannot qualify.");
            if (incumbentLoss is not null && !generations.OrderByDescending(g => g.Max(x => x.OutcomeUtc)).Take(2)
                .All(g => g.Average(x => x.IntervalLoss) <= g.Average(x => x.IncumbentIntervalLoss!.Value) &&
                    g.Average(x => x.IntervalLoss) <= g.Average(x => x.PaceIntervalLoss)))
                reasons.Add("One of the latest two reset groups loses to pace or incumbent.");
            if (score.AssertedTrainingIntervals > 0 && (score.HeldOutIntervals < 32 || generations.Count < 12))
                reasons.Add("User-asserted training requires at least 32 strict outcomes across 12 reset generations.");
        }
        return new(reasons.Count == 0, requireStrict, score.Trials.Count, generations.Count, pairs, loss, incumbentLoss, paceLoss, reasons);
    }

    public static bool SupportsStrictSelection(ComposedQuotaScore score, DateTimeOffset origin) =>
        AssessSelection(score, origin, requireStrict: true).Passed;

    private static bool SupportsAssertedCost(ComposedQuotaScore score, IReadOnlyList<ComposedQuotaScore> all)
    {
        if (score.AssertedTrainingIntervals == 0 || score.Cohort.AccountKey is null) return false;
        var local = all.FirstOrDefault(x => x.Cohort == score.Cohort && x.HorizonHours == score.HorizonHours && x.CostModel == "total");
        if (local is null || score.Trials.Count < 16) return false;
        var lookup = local.Trials.ToDictionary(x => x.OriginUtc);
        if (score.Trials.Any(x => !lookup.ContainsKey(x.OriginUtc))) return false;
        var generations = QuotaResetGenerationPolicy.Group(score.Trials, x => x.ResetUtc);
        var loss = generations.Average(g => g.Average(x => x.CostOnlyIntervalLoss));
        var baseline = generations.Average(g => g.Average(x => lookup[x.OriginUtc].CostOnlyIntervalLoss));
        return loss + 0.1 < baseline && loss < baseline * 0.9;
    }
}
