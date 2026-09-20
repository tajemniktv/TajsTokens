using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Frozen-prefix cost calibration, deliberately separate from future-workload forecasting.</summary>
public static class QuotaAccountingModel
{
    public const string Version = "quota-cost-evaluation/v5-blocks";
    private static readonly string[] Candidates = ["persistence", "pace", "total", "categories", "model-effort"];
    public const string Methodology = "Evaluation only: actual interval workload is an oracle cost input, NOT an end-to-end forecast. " +
        "Disjoint targets within each cohort/horizon; overlapping sources and horizons are never pooled. " +
        "First 20 non-overlapping matured observations fit a frozen nonnegative, no-intercept interval-distance ridge model. " +
        "Vocabulary/scaling use training only. Unknown model/effort mass is explicit. All later disjoint intervals are held out; no rolling refit hides drift. " +
        "Loss is distance to the measurement envelope; displayed-delta MAE is secondary. Rollout envelopes are sensitivity assumptions, not measured precision. " +
        "Residuals are associations, not causal attribution or proof of provider changes. Bands use earlier completed held-out block maximum errors " +
        "after eight blocks and target 80% envelope intersection, not calibrated latent coverage or exhaustion probability. " +
        "A diagnostic win needs 16 held-out intervals in three blocks and >=10% and 0.1pp block-average loss improvement over both pace and total; " +
        "richer candidates must also beat their simpler parent and win on most blocks. No automatic production promotion. " +
        "API-price weighting is a fixed retrospective standard/short-context baseline, never credits or actual cost; incomplete pricing is withheld. " +
        "Its comparisons use matched outcomes and it cannot earn a promotion label. " +
        "Incomplete category training blocks category-dependent fitting; incomplete held-out composition is withheld. " +
        "Raw-token baselines retain reported totals. Training without any positive recorded workload cannot identify cost weights; " +
        "it is withheld, not interpreted as free work. Pace and zero-use references remain available. " +
        "Different held-out target sets cannot earn a comparative promotion.";

    public static QuotaCostReport Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        bool userConfirmedRolloutOwnership = false, IReadOnlyList<double>? horizons = null, IReadOnlyList<string>? candidates = null, Func<string, QuotaCostFeatureExtension?>? extensions = null)
    {
        var built = QuotaCostObservationBuilder.BuildDetailed(data, cancellationToken, userConfirmedRolloutOwnership, horizons);
        return Evaluate(built.Observations, (userConfirmedRolloutOwnership ?
            "User confirms retained rollouts belong to their account. Native account IDs remain absent; sessions/sources are not pooled. " : "") + data.Coverage + " " +
            QuotaHistoryPolicy.Summarize(QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc)), cancellationToken, candidates, extensions)
            with { DatasetCapturedAtUtc = data.CapturedAtUtc, EvidenceCoverage = QuotaEvaluationCoverageBuilder.Build(data),
                ConstructionCoverage = built.Coverage };
    }

    public static QuotaCostReport Evaluate(IReadOnlyList<QuotaCostObservation> rows, string coverage,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? candidates = null, Func<string, QuotaCostFeatureExtension?>? extensions = null)
    {
        var scores = new List<QuotaCostScore>();
        foreach (var group in rows.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            var ordered = group.OrderBy(x => x.StartUtc).ToArray();
            var training = ordered.Take(20).ToArray();
            var trainingGenerations = QuotaResetGenerationPolicy.Group(training, x => x.ResetUtc).Count;
            var heldout = ordered.Skip(training.Length).Where(x => x.StartUtc >= training[^1].EndUtc).ToArray();
            var local = new List<QuotaCostScore>();
            foreach (var candidate in candidates ?? Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trials = new List<QuotaCostTrial>();
                var vector = new CostVector(training, candidate, extensions?.Invoke(candidate));
                var unpricedTraining = candidate == "api-price" ? training.Count(x => !Priceable(x)) : 0;
                var unpricedHeldout = candidate == "api-price" ? heldout.Count(x => !Priceable(x)) : 0;
                var needsCategories = candidate is not ("total" or "pace" or "persistence" or "api-price");
                var incompleteTraining = needsCategories ? training.Count(x => !x.HasCompleteTokenCategories) : 0;
                var incompleteHeldout = needsCategories ? heldout.Count(x => !x.HasCompleteTokenCategories) : 0;
                var noTrainingWork = candidate is not ("pace" or "persistence") && training.Length >= 20 && !HasTrainingWork(training);
                var fitted = training.Length >= 20 && unpricedTraining == 0 && incompleteTraining == 0 && !noTrainingWork;
                var fit = fitted && candidate is not "pace" and not "persistence"
                    ? IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, cancellationToken) : null;
                foreach (var row in fitted ? heldout : [])
                {
                    if (candidate == "api-price" && !Priceable(row)) continue;
                    if (needsCategories && !row.HasCompleteTokenCategories) continue;
                    var prediction = candidate switch
                    {
                        "pace" => row.PaceDelta,
                        "persistence" => 0,
                        _ => fit!.Predict(vector.Values(row))
                    };
                    var errors = ChronologicalEvidence.Blocks(trials.Where(x => x.EndUtc < row.StartUtc),
                            x => x.StartUtc, x => x.EndUtc, x => x.ResetUtc)
                        .Select(g => g.Max(x => x.IntervalLoss)).ToArray();
                    var radius = errors.Length >= 8 ? Quantile(errors, Math.Min(1, Math.Ceiling((errors.Length + 1) * 0.8) / errors.Length)) : null;
                    trials.Add(new(row.StartUtc, row.EndUtc, row.ResetUtc, row.ObservedDelta, row.LowerDelta, row.UpperDelta,
                        prediction, row.IntervalLoss(prediction), row.ObservedDelta - prediction,
                        radius is { } r ? Math.Max(0, prediction - r) : null, radius is { } r2 ? prediction + r2 : null));
                }
                var epochs = ChronologicalEvidence.Blocks(trials, x => x.StartUtc, x => x.EndUtc, x => x.ResetUtc);
                var bands = trials.Where(x => x.LowerPrediction is not null).ToArray();
                var residuals = trials.Select(x => x.Residual).ToArray();
                local.Add(new(group.Key.Cohort, group.Key.HorizonHours, candidate, ordered.Length,
                    training.Length, trainingGenerations, trials.Count, QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count,
                    Mean(trials.Select(x => x.IntervalLoss)), Mean(residuals.Select(Math.Abs)),
                    Mean(epochs.Select(g => g.Average(x => x.IntervalLoss))),
                    Quantile(residuals, 0.1), Quantile(residuals, 0.5), Quantile(residuals, 0.9),
                    Mean(trials.Select(x => Math.Max(0, x.LowerDelta - x.Prediction))), bands.Length,
                    Mean(bands.Select(x => x.UpperPrediction >= x.LowerDelta && x.LowerPrediction <= x.UpperDelta ? 1d : 0d)),
                    false, noTrainingWork ? "no-recorded-training-work" : incompleteTraining > 0 ? "incomplete-category-training" : unpricedTraining > 0 ? "unpriced-training-evidence" : fitted ? "evaluation-only" : "insufficient-training-intervals",
                    fit is null ? new Dictionary<string, double>() : vector.Names.Select((name, i) => (name, value: fit.Weights[i] / fit.Scales[i]))
                        .ToDictionary(x => x.name, x => x.value), trials, CodexRegimeModel.DetectPersistentShifts(epochs))
                {
                    RateCardVersion = candidate == "api-price" ? ApiPriceWorkload.Version : null,
                    IncompleteCategoryTrainingIntervals = incompleteTraining, IncompleteCategoryHeldOutIntervals = incompleteHeldout,
                    UnpricedTrainingIntervals = unpricedTraining, UnpricedHeldOutIntervals = unpricedHeldout,
                    UnpricedReportedTokens = candidate == "api-price" ? ordered.Sum(x => x.ApiPriceWeight?.UnpricedReportedTokens ?? 0) : 0
                });
            }
            foreach (var score in local)
            {
                if (score.Candidate == "api-price")
                {
                    var origins = score.Trials.Select(x => x.StartUtc).ToHashSet();
                    scores.Add(score with
                    {
                        PairedPaceIntervalLoss = Mean(local.Single(x => x.Candidate == "pace").Trials.Where(x => origins.Contains(x.StartUtc)).Select(x => x.IntervalLoss)),
                        PairedTotalIntervalLoss = Mean(local.Single(x => x.Candidate == "total").Trials.Where(x => origins.Contains(x.StartUtc)).Select(x => x.IntervalLoss))
                    });
                    continue;
                }
                var parents = new[] { "pace", "total", score.Candidate switch
                {
                    "model-effort" => "categories",
                    "context-ablation" or "activity-ablation" or "runtime-ablation" or "time-ablation" => "model-effort",
                    _ => "pace"
                }}.Distinct().Where(x => x != score.Candidate).Select(name => local.Single(x => x.Candidate == name)).ToArray();
                var supportedPrecision = heldout.All(x => !x.QualityFlags.Contains("meter-precision-unverified-sensitivity-only"));
                var win = score.Candidate is not "pace" and not "persistence" && supportedPrecision &&
                    score.Cohort.AccountKey is not null && score.HeldOutSamples >= 16 && ChronologicalEvidence.Blocks(score.Trials, x => x.StartUtc, x => x.EndUtc, x => x.ResetUtc).Count >= 3 &&
                    training.Count(x => x.Features.Tokens > 0) >= 12 &&
                    parents.All(parent => Beats(score, parent));
                scores.Add(score with { MaterialWin = win, Status = win ? "cost-only-win-requires-end-to-end-validation" : score.Status });
            }
        }
        return new(Version, Methodology, coverage, rows.Count,
            rows.SelectMany(x => x.QualityFlags).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()), scores)
            { CohortCoverage = QuotaEvaluationCoverageBuilder.Cohorts(rows) };
    }

    private static bool Priceable(QuotaCostObservation row) => row.ApiPriceWeight is { IsComplete: true } price &&
        price.RateCardVersion == ApiPriceWorkload.Version;

    internal static bool HasTrainingWork(IReadOnlyList<QuotaCostObservation> training) => training.Any(x => x.Features.Tokens > 0);

    private static bool Beats(QuotaCostScore score, QuotaCostScore parent)
    {
        if (!score.Trials.Select(x => (x.StartUtc, x.EndUtc)).SequenceEqual(parent.Trials.Select(x => (x.StartUtc, x.EndUtc)))) return false;
        if (score.BlockMeanIntervalLoss is not { } loss || parent.BlockMeanIntervalLoss is not { } baseline ||
            loss > baseline * 0.9 || baseline - loss < 0.1) return false;
        var pairs = score.Trials.Zip(parent.Trials).ToArray();
        var generations = ChronologicalEvidence.Blocks(pairs, x => x.First.StartUtc, x => x.First.EndUtc, x => x.First.ResetUtc);
        return generations.Count(g => g.Average(x => x.First.IntervalLoss) < g.Average(x => x.Second.IntervalLoss)) > generations.Count / 2;
    }

    internal static Func<QuotaCostObservation, double> FitFrozen(IReadOnlyList<QuotaCostObservation> training,
        string candidate, CancellationToken cancellationToken)
    {
        if (training.Count < 20 || !HasTrainingWork(training) || candidate is not ("total" or "categories" or "model-effort"))
            throw new ArgumentException("Composition forecasting requires 20 cost observations, positive recorded training work and a supported composition model.");
        if (candidate != "total" && training.Any(x => !x.HasCompleteTokenCategories))
            throw new ArgumentException("Category fitting requires complete disjoint token evidence.");
        var vector = new CostVector(training, candidate);
        var fit = IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, cancellationToken);
        return row => fit.Predict(vector.Values(row));
    }

    internal static double[] FitCategoryWeights(IReadOnlyList<QuotaCostObservation> training, CancellationToken cancellationToken)
    {
        var vector = new CostVector(training, "categories");
        var fit = IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, cancellationToken);
        return fit.Weights.Select((weight, i) => weight / fit.Scales[i] / 1e6).ToArray();
    }

    internal static CodexNumericInference CaptureFrozenInference(IReadOnlyList<QuotaCostObservation> training,
        string candidate, QuotaCostObservation projected, double remaining, CancellationToken token)
    {
        var vector = new CostVector(training, candidate);
        var fit = IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, token);
        return new("remaining=anchor-frozen-cost/v1:" + candidate, remaining, vector.Values(projected),
            fit.Weights.Select((w, i) => -w / fit.Scales[i]).ToArray(), 0, remaining);
    }

    private static double? Mean(IEnumerable<double> values)
    {
        var rows = values.ToArray();
        return rows.Length == 0 ? null : rows.Average();
    }

    private static double? Quantile(IEnumerable<double> values, double quantile)
    {
        var rows = values.Order().ToArray();
        return rows.Length == 0 ? null : rows[Math.Clamp((int)Math.Ceiling(rows.Length * quantile) - 1, 0, rows.Length - 1)];
    }

    private sealed class CostVector
    {
        private readonly string candidate;
        private readonly string[] models;
        private readonly string[] efforts;
        private readonly QuotaCostFeatureExtension? extension;
        public string[] Names { get; }

        public CostVector(IReadOnlyList<QuotaCostObservation> training, string candidate, QuotaCostFeatureExtension? extension = null)
        {
            this.candidate = candidate;
            this.extension = extension;
            if (candidate == "api-price")
            {
                models = []; efforts = []; Names = ["standard-short-context-api-weight"]; return;
            }
            models = training.SelectMany(x => x.Features.ModelTokenShares.Keys).Distinct().Order().Take(16).ToArray();
            efforts = training.SelectMany(x => x.Features.EffortTokenShares.Keys).Distinct().Order().Take(16).ToArray();
            var names = new List<string>(candidate == "total" ? ["reported-total/M"] :
                new[] { "uncached/M", "cache-read/M", "cache-write/M", "normal-output/M", "reasoning-output/M" });
            if (Rich) names.AddRange(models.Select(x => "model:" + x).Concat(efforts.Select(x => "effort:" + x)).Concat(["unknown-model", "unknown-effort"]));
            if (extension is not null) names.AddRange(extension.Names);
            Names = names.ToArray();
        }

        private bool Rich => candidate is not "pace" and not "persistence" and not "total" and not "categories";

        public double[] Values(QuotaCostObservation row)
        {
            if (candidate == "api-price") return [(double)row.ApiPriceWeight!.WeightedAmount];
            var f = row.Features;
            var total = f.Tokens / 1e6;
            var values = new List<double>(candidate == "total" ? [total] : row.TokenCategories.Select(x => x / 1e6));
            if (Rich)
            {
                values.AddRange(models.Select(x => total * f.ModelTokenShares.GetValueOrDefault(x)));
                values.AddRange(efforts.Select(x => total * f.EffortTokenShares.GetValueOrDefault(x)));
                values.Add(total * Math.Max(0, 1 - models.Sum(x => f.ModelTokenShares.GetValueOrDefault(x))));
                values.Add(total * Math.Max(0, 1 - efforts.Sum(x => f.EffortTokenShares.GetValueOrDefault(x))));
            }
            // Optional features are workload interactions: time or missing telemetry cannot
            // generate predicted local cost in an interval with no recorded tokens.
            if (extension is not null) values.AddRange(extension.Values(row).Select(x => x * total));
            return values.ToArray();
        }

    }

    private sealed record IntervalRidge(double[] Weights, double[] Scales)
    {
        public double Predict(double[] row) => row.Select((x, i) => x / Scales[i] * Weights[i]).Sum();

        public static IntervalRidge Fit(double[][] input, IReadOnlyList<QuotaCostObservation> targets, CancellationToken cancellationToken)
        {
            var dimensions = input[0].Length;
            var scales = Enumerable.Range(0, dimensions).Select(i => Math.Max(1e-9, Math.Sqrt(input.Average(x => x[i] * x[i])))).ToArray();
            var x = input.Select(row => row.Select((v, i) => v / scales[i]).ToArray()).ToArray();
            var weights = new double[dimensions];
            const double penalty = 0.1;
            // Squared distance to the censoring interval is convex, continuously differentiable.
            // Trace bounds the Hessian spectral norm, giving a conservative fixed step.
            var step = 1 / (x.Average(row => row.Sum(v => v * v)) + penalty);
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gradient = weights.Select(w => penalty * w).ToArray();
                for (var j = 0; j < x.Length; j++)
                {
                    var prediction = x[j].Select((v, i) => v * weights[i]).Sum();
                    var error = prediction - Math.Clamp(prediction, targets[j].LowerDelta, targets[j].UpperDelta);
                    for (var i = 0; i < dimensions; i++) gradient[i] += error * x[j][i] / x.Length;
                }
                var change = 0d;
                for (var i = 0; i < dimensions; i++)
                {
                    var next = Math.Max(0, weights[i] - step * gradient[i]);
                    change = Math.Max(change, Math.Abs(next - weights[i]));
                    weights[i] = next;
                }
                if (change < 1e-8) break;
            }
            return new(weights, scales);
        }
    }
}
