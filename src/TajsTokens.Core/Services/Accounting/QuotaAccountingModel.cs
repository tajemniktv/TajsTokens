// Taj's Tokens | QuotaAccountingModel.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Frozen-prefix cost calibration, deliberately separate from future-workload forecasting.</summary>
public static class QuotaAccountingModel
{
    public const string Version = "quota-cost-evaluation/v7-joint-evidence";

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

    private static readonly string[] Candidates = ["persistence", "pace", "total", "categories", "model-effort"];

    public static QuotaCostReport Evaluate(
        CodexForecastDataset data,
        CancellationToken cancellationToken = default,
        bool userConfirmedRolloutOwnership = false,
        IReadOnlyList<double>? horizons = null,
        IReadOnlyList<string>? candidates = null,
        Func<string, QuotaCostFeatureExtension?>? extensions = null)
    {
        QuotaCostObservationBuild built = QuotaCostObservationBuilder.BuildDetailed(
            data,
            cancellationToken,
            userConfirmedRolloutOwnership,
            horizons);
        return Evaluate(
                built.Observations,
                (userConfirmedRolloutOwnership
                    ? "User confirms retained rollouts belong to their account. Native account IDs remain absent; sessions/sources are not pooled. "
                    : "") + data.Coverage + " " +
                QuotaHistoryPolicy.Summarize(QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc)),
                cancellationToken,
                candidates,
                extensions)
            with
            {
                DatasetCapturedAtUtc = data.CapturedAtUtc,
                EvidenceCoverage = QuotaEvaluationCoverageBuilder.Build(data),
                ConstructionCoverage = built.Coverage,
            };
    }

    public static QuotaCostReport Evaluate(
        IReadOnlyList<QuotaCostObservation> rows,
        string coverage,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? candidates = null,
        Func<string, QuotaCostFeatureExtension?>? extensions = null)
    {
        var scores = new List<QuotaCostScore>();
        foreach (IGrouping<(QuotaHistoryCohort Cohort, double HorizonHours), QuotaCostObservation> group in rows.GroupBy(x =>
                     (x.Cohort, x.HorizonHours)))
        {
            QuotaCostObservation[] ordered = group.OrderBy(x => x.StartUtc).ToArray();
            QuotaCostObservation[] training = ordered.Take(20).ToArray();
            int trainingGenerations = QuotaResetGenerationPolicy.Group(training, x => x.ResetUtc).Count;
            QuotaCostObservation[] heldout = ordered.Skip(training.Length).Where(x => x.StartUtc >= training[^1].EndUtc).ToArray();
            var local = new List<QuotaCostScore>();
            foreach (string candidate in candidates ?? Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trials = new List<QuotaCostTrial>();
                var vector = new CostVector(training, candidate, extensions?.Invoke(candidate));
                int unpricedTraining = candidate == "api-price" ? training.Count(x => !Priceable(x)) : 0;
                int unpricedHeldout = candidate == "api-price" ? heldout.Count(x => !Priceable(x)) : 0;
                bool needsCategories = candidate is not ("total" or "pace" or "persistence" or "api-price");
                int incompleteTraining = needsCategories ? training.Count(x => !x.HasCompleteTokenCategories) : 0;
                int incompleteHeldout = needsCategories ? heldout.Count(x => !x.HasCompleteTokenCategories) : 0;
                bool joint = candidate == "joint-categories";
                int incompleteJointTraining = joint ? training.Count(x => !QuotaCostIdentification.Valid(x)) : 0;
                int incompleteJointHeldout = joint ? heldout.Count(x => !QuotaCostIdentification.Valid(x)) : 0;
                bool noTrainingWork = candidate is not ("pace" or "persistence") && training.Length >= 20 && !HasTrainingWork(training);
                bool fitted = training.Length >= 20 && unpricedTraining == 0 && incompleteTraining == 0 && incompleteJointTraining == 0 && !noTrainingWork;
                IntervalRidge? fit = fitted && candidate is not "pace" and not "persistence"
                    ? Fit(training, vector, candidate, cancellationToken)
                    : null;
                foreach (QuotaCostObservation row in fitted ? heldout : [])
                {
                    if (candidate == "api-price" && !Priceable(row)) continue;
                    if (needsCategories && !row.HasCompleteTokenCategories) continue;
                    if (joint && !QuotaCostIdentification.Valid(row)) continue;
                    double prediction = candidate switch
                    {
                        "pace" => row.PaceDelta,
                        "persistence" => 0,
                        _ => fit!.Predict(vector.Values(row)),
                    };
                    double[] errors = ChronologicalEvidence.Blocks(
                            trials.Where(x => x.EndUtc < row.StartUtc),
                            x => x.StartUtc,
                            x => x.EndUtc,
                            x => x.ResetUtc)
                        .Select(g => g.Max(x => x.IntervalLoss)).ToArray();
                    double? radius = errors.Length >= 8
                        ? Quantile(errors, Math.Min(1, Math.Ceiling((errors.Length + 1) * 0.8) / errors.Length))
                        : null;
                    trials.Add(
                        new QuotaCostTrial(
                            row.StartUtc,
                            row.EndUtc,
                            row.ResetUtc,
                            row.ObservedDelta,
                            row.LowerDelta,
                            row.UpperDelta,
                            prediction,
                            row.IntervalLoss(prediction),
                            row.ObservedDelta - prediction,
                            radius is { } r ? Math.Max(0, prediction - r) : null,
                            radius is { } r2 ? prediction + r2 : null));
                }
                IReadOnlyList<IReadOnlyList<QuotaCostTrial>> epochs = ChronologicalEvidence.Blocks(
                    trials,
                    x => x.StartUtc,
                    x => x.EndUtc,
                    x => x.ResetUtc);
                QuotaCostTrial[] bands = trials.Where(x => x.LowerPrediction is not null).ToArray();
                double[] residuals = trials.Select(x => x.Residual).ToArray();
                local.Add(
                    new QuotaCostScore(
                        group.Key.Cohort,
                        group.Key.HorizonHours,
                        candidate,
                        ordered.Length,
                        training.Length,
                        trainingGenerations,
                        trials.Count,
                        QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc).Count,
                        Mean(trials.Select(x => x.IntervalLoss)),
                        Mean(residuals.Select(Math.Abs)),
                        Mean(epochs.Select(g => g.Average(x => x.IntervalLoss))),
                        Quantile(residuals, 0.1),
                        Quantile(residuals, 0.5),
                        Quantile(residuals, 0.9),
                        Mean(trials.Select(x => Math.Max(0, x.LowerDelta - x.Prediction))),
                        bands.Length,
                        Mean(bands.Select(x => x.UpperPrediction >= x.LowerDelta && x.LowerPrediction <= x.UpperDelta ? 1d : 0d)),
                        false,
                        noTrainingWork ? "no-recorded-training-work" :
                        incompleteTraining > 0 ? "incomplete-category-training" :
                        incompleteJointTraining > 0 ? "incomplete-joint-training" :
                        unpricedTraining > 0 ? "unpriced-training-evidence" :
                        fitted ? joint ? "research-only-joint-shrunk-to-pooled" : "evaluation-only" : "insufficient-training-intervals",
                        fit is null
                            ? new Dictionary<string, double>()
                            : vector.Names.Select((name, i) => (name, value: fit.Weights[i] / fit.Scales[i]))
                                .ToDictionary(x => x.name, x => x.value),
                        trials,
                        CodexRegimeModel.DetectPersistentShifts(epochs))
                    {
                        RateCardVersion = candidate == "api-price" ? ApiPriceWorkload.Version : null,
                        IncompleteCategoryTrainingIntervals = incompleteTraining,
                        IncompleteCategoryHeldOutIntervals = incompleteHeldout,
                        IncompleteJointTrainingIntervals = incompleteJointTraining,
                        IncompleteJointHeldOutIntervals = incompleteJointHeldout,
                        UnpricedTrainingIntervals = unpricedTraining,
                        UnpricedHeldOutIntervals = unpricedHeldout,
                        UnpricedReportedTokens =
                            candidate == "api-price" ? ordered.Sum(x => x.ApiPriceWeight?.UnpricedReportedTokens ?? 0) : 0,
                    });
            }
            foreach (QuotaCostScore score in local)
            {
                if (score.Candidate is "api-price" or "joint-categories")
                {
                    var origins = score.Trials.Select(x => (x.StartUtc, x.EndUtc)).ToHashSet();
                    scores.Add(
                        score with
                        {
                            PairedPaceIntervalLoss =
                            Mean(
                                local.Single(x => x.Candidate == "pace").Trials.Where(x => origins.Contains((x.StartUtc, x.EndUtc)))
                                    .Select(x => x.IntervalLoss)),
                            PairedTotalIntervalLoss = Mean(
                                local.Single(x => x.Candidate == "total").Trials.Where(x => origins.Contains((x.StartUtc, x.EndUtc)))
                                    .Select(x => x.IntervalLoss)),
                            PairedCategoryIntervalLoss = score.Candidate == "joint-categories" ? Mean(
                                local.Single(x => x.Candidate == "categories").Trials.Where(x => origins.Contains((x.StartUtc, x.EndUtc)))
                                    .Select(x => x.IntervalLoss)) : null,
                        });
                    continue;
                }
                QuotaCostScore[] parents = new[]
                {
                    "pace",
                    "total",
                    score.Candidate switch
                    {
                        "model-effort" => "categories",
                        "context-ablation" or "activity-ablation" or "runtime-ablation" or "time-ablation" => "model-effort",
                        _ => "pace",
                    },
                }.Distinct().Where(x => x != score.Candidate).Select(name => local.Single(x => x.Candidate == name)).ToArray();
                bool supportedPrecision = heldout.All(x => !x.QualityFlags.Contains("meter-precision-unverified-sensitivity-only"));
                bool win = score.Candidate is not "pace" and not "persistence" && supportedPrecision &&
                           score.Cohort.AccountKey is not null && score.HeldOutSamples >= 16 && ChronologicalEvidence
                               .Blocks(score.Trials, x => x.StartUtc, x => x.EndUtc, x => x.ResetUtc).Count >= 3 &&
                           training.Count(x => x.Features.Tokens > 0) >= 12 &&
                           parents.All(parent => Beats(score, parent));
                scores.Add(score with { MaterialWin = win, Status = win ? "cost-only-win-requires-end-to-end-validation" : score.Status });
            }
        }
        return new QuotaCostReport(
            Version,
            Methodology,
            coverage,
            rows.Count,
            rows.SelectMany(x => x.QualityFlags).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count()),
            scores) { CohortCoverage = QuotaEvaluationCoverageBuilder.Cohorts(rows) };
    }

    private static bool Priceable(QuotaCostObservation row)
    {
        return row.ApiPriceWeight is { IsComplete: true } price &&
               price.RateCardVersion == ApiPriceWorkload.Version;
    }

    internal static bool HasTrainingWork(IReadOnlyList<QuotaCostObservation> training)
    {
        return training.Any(x => x.Features.Tokens > 0);
    }

    internal static (string[] Names, double[][] Training, double[] Scenario) JointScenarioDesign(
        IReadOnlyList<QuotaCostObservation> rows, CodexJointWorkload scenario)
    {
        // Feasibility geometry includes requested coordinates explicitly. This is not
        // a fitted vocabulary or a pooled fallback for previously unseen models.
        var requested = rows[0] with { Features = rows[0].Features with { JointWorkload = scenario } };
        var vector = new CostVector(rows.Append(requested).ToArray(), "joint-categories");
        return (vector.Names, rows.Select(vector.Values).ToArray(), vector.Values(requested));
    }

    private static bool Beats(QuotaCostScore score, QuotaCostScore parent)
    {
        if (!score.Trials.Select(x => (x.StartUtc, x.EndUtc))
                .SequenceEqual(parent.Trials.Select(x => (x.StartUtc, x.EndUtc)))) return false;
        if (score.BlockMeanIntervalLoss is not { } loss || parent.BlockMeanIntervalLoss is not { } baseline ||
            loss > baseline * 0.9 || baseline - loss < 0.1) return false;
        (QuotaCostTrial First, QuotaCostTrial Second)[] pairs = score.Trials.Zip(parent.Trials).ToArray();
        IReadOnlyList<IReadOnlyList<(QuotaCostTrial First, QuotaCostTrial Second)>> generations = ChronologicalEvidence.Blocks(
            pairs,
            x => x.First.StartUtc,
            x => x.First.EndUtc,
            x => x.First.ResetUtc);
        return generations.Count(g => g.Average(x => x.First.IntervalLoss) < g.Average(x => x.Second.IntervalLoss)) > generations.Count / 2;
    }

    internal static Func<QuotaCostObservation, double> FitFrozen(
        IReadOnlyList<QuotaCostObservation> training,
        string candidate,
        CancellationToken cancellationToken)
    {
        if (training.Count < 20 || !HasTrainingWork(training) || candidate is not ("total" or "categories" or "model-effort" or "joint-categories"))
            throw new ArgumentException(
                "Composition forecasting requires 20 cost observations, positive recorded training work and a supported composition model.");
        if (candidate != "total" && training.Any(x => !x.HasCompleteTokenCategories))
            throw new ArgumentException("Category fitting requires complete disjoint token evidence.");
        if (candidate == "joint-categories" && training.Any(x => !QuotaCostIdentification.Valid(x)))
            throw new ArgumentException("Joint fitting requires complete observed joint token evidence.");
        var vector = new CostVector(training, candidate);
        IntervalRidge fit = Fit(training, vector, candidate, cancellationToken);
        return row => fit.Predict(vector.Values(row));
    }

    private static IntervalRidge Fit(IReadOnlyList<QuotaCostObservation> training, CostVector vector, string candidate, CancellationToken token)
    {
        double[]? prior = null;
        if (candidate == "joint-categories")
        {
            var pooled = new CostVector(training, "categories");
            IntervalRidge fit = IntervalRidge.Fit(training.Select(pooled.Values).ToArray(), training, token);
            // Final nonnegative joint coefficients shrink toward the training-only pooled
            // category fit. Unknown and vocabulary-overflow bins retain distinct coordinates.
            prior = Enumerable.Range(0, vector.Names.Length).Select(i => fit.Weights[i % 5] / fit.Scales[i % 5]).ToArray();
        }
        return IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, token, prior);
    }

    internal static double[] FitCategoryWeights(IReadOnlyList<QuotaCostObservation> training, CancellationToken cancellationToken)
    {
        var vector = new CostVector(training, "categories");
        IntervalRidge fit = IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, cancellationToken);
        return fit.Weights.Select((weight, i) => weight / fit.Scales[i] / 1e6).ToArray();
    }

    internal static CodexNumericInference CaptureFrozenInference(
        IReadOnlyList<QuotaCostObservation> training,
        string candidate,
        QuotaCostObservation projected,
        double remaining,
        CancellationToken token)
    {
        var vector = new CostVector(training, candidate);
        IntervalRidge fit = IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, token);
        return new CodexNumericInference(
            "remaining=anchor-frozen-cost/v1:" + candidate,
            remaining,
            vector.Values(projected),
            fit.Weights.Select((w, i) => -w / fit.Scales[i]).ToArray(),
            0,
            remaining);
    }

    private static double? Mean(IEnumerable<double> values)
    {
        double[] rows = values.ToArray();
        return rows.Length == 0 ? null : rows.Average();
    }

    private static double? Quantile(IEnumerable<double> values, double quantile)
    {
        double[] rows = values.Order().ToArray();
        return rows.Length == 0 ? null : rows[Math.Clamp((int)Math.Ceiling(rows.Length * quantile) - 1, 0, rows.Length - 1)];
    }

    private sealed class CostVector
    {
        private readonly string candidate;
        private readonly string[] efforts;
        private readonly QuotaCostFeatureExtension? extension;
        private readonly string[] models;

        public CostVector(IReadOnlyList<QuotaCostObservation> training, string candidate, QuotaCostFeatureExtension? extension = null)
        {
            this.candidate = candidate;
            this.extension = extension;
            if (candidate == "api-price")
            {
                models = [];
                efforts = [];
                Names = ["standard-short-context-api-weight"];
                return;
            }
            models = training.SelectMany(x => x.Features.ModelTokenShares.Keys).Distinct().Order().Take(16).ToArray();
            efforts = training.SelectMany(x => x.Features.EffortTokenShares.Keys).Distinct().Order().Take(16).ToArray();
            if (candidate == "joint-categories")
            {
                models = training.SelectMany(x => x.Features.JointWorkload?.Cells ?? []).Select(x => x.Coordinate!.Model)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(16).ToArray();
                efforts = [];
                Names = models.Select(x => "joint-model:" + x).Concat(["joint-missing-model", "joint-vocabulary-overflow"])
                    .SelectMany(x => Enumerable.Range(0, 5).Select(c => $"{x}/{(TtTokenCategory)c}/M")).ToArray();
                return;
            }
            var names = new List<string>(
                candidate == "total"
                    ? ["reported-total/M"]
                    : new[] { "uncached/M", "cache-read/M", "cache-write/M", "normal-output/M", "reasoning-output/M" });
            if (Rich)
                names.AddRange(
                    models.Select(x => "model:" + x).Concat(efforts.Select(x => "effort:" + x))
                        .Concat(["unknown-model", "unknown-effort", "vocabulary-overflow-model", "vocabulary-overflow-effort"]));
            if (extension is not null) names.AddRange(extension.Names);
            Names = names.ToArray();
        }

        public string[] Names { get; }

        private bool Rich => candidate is not "pace" and not "persistence" and not "total" and not "categories";

        public double[] Values(QuotaCostObservation row)
        {
            if (candidate == "api-price") return [(double)row.ApiPriceWeight!.WeightedAmount];
            if (candidate == "joint-categories")
            {
                var joint = row.Features.JointWorkload ?? throw new ArgumentException("Joint workload is unavailable.");
                double[] result = new double[Names.Length];
                foreach (var cell in joint.Cells.Concat(joint.Overflow is { } overflow ? [overflow] : []))
                {
                    string? model = cell.Coordinate?.Model;
                    int m = cell.Coordinate is not null && string.IsNullOrWhiteSpace(model) ? models.Length : Array.IndexOf(models, model);
                    if (m < 0) m = models.Length + 1;
                    for (int c = 0; c < 5; c++) result[m * 5 + c] += (double)TtJointWorkloadBasis.Amount(cell, (TtTokenCategory)c) / 1e6;
                }
                return result;
            }
            CodexForecastFeatures f = row.Features;
            double total = f.Tokens / 1e6;
            var values = new List<double>(candidate == "total" ? [total] : row.TokenCategories.Select(x => x / 1e6));
            if (Rich)
            {
                values.AddRange(models.Select(x => total * f.ModelTokenShares.GetValueOrDefault(x)));
                values.AddRange(efforts.Select(x => total * f.EffortTokenShares.GetValueOrDefault(x)));
                values.Add(total * Math.Max(0, 1 - f.ModelTokenShares.Values.Sum()));
                values.Add(total * Math.Max(0, 1 - f.EffortTokenShares.Values.Sum()));
                values.Add(total * f.ModelTokenShares.Where(x => !models.Contains(x.Key, StringComparer.Ordinal)).Sum(x => x.Value));
                values.Add(total * f.EffortTokenShares.Where(x => !efforts.Contains(x.Key, StringComparer.Ordinal)).Sum(x => x.Value));
            }
            // Optional features are workload interactions: time or missing telemetry cannot
            // generate predicted local cost in an interval with no recorded tokens.
            if (extension is not null) values.AddRange(extension.Values(row).Select(x => x * total));
            return values.ToArray();
        }
    }

    private sealed record IntervalRidge(double[] Weights, double[] Scales)
    {
        public double Predict(double[] row)
        {
            return row.Select((x, i) => x / Scales[i] * Weights[i]).Sum();
        }

        public static IntervalRidge Fit(double[][] input, IReadOnlyList<QuotaCostObservation> targets, CancellationToken cancellationToken, double[]? prior = null)
        {
            int dimensions = input[0].Length;
            double[] scales = Enumerable.Range(0, dimensions).Select(i => Math.Max(1e-9, Math.Sqrt(input.Average(x => x[i] * x[i]))))
                .ToArray();
            double[][] x = input.Select(row => row.Select((v, i) => v / scales[i]).ToArray()).ToArray();
            double[] center = Enumerable.Range(0, dimensions).Select(i => (prior?[i] ?? 0) * scales[i]).ToArray();
            double[] weights = (double[])center.Clone();
            const double penalty = 0.1;
            // Squared distance to the censoring interval is convex, continuously differentiable.
            // Trace bounds the Hessian spectral norm, giving a conservative fixed step.
            double step = 1 / (x.Average(row => row.Sum(v => v * v)) + penalty);
            for (int iteration = 0; iteration < 2000; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double[] gradient = weights.Select((w, i) => penalty * (w - center[i])).ToArray();
                for (int j = 0; j < x.Length; j++)
                {
                    double prediction = x[j].Select((v, i) => v * weights[i]).Sum();
                    double error = prediction - Math.Clamp(prediction, targets[j].LowerDelta, targets[j].UpperDelta);
                    for (int i = 0; i < dimensions; i++) gradient[i] += error * x[j][i] / x.Length;
                }
                double change = 0d;
                for (int i = 0; i < dimensions; i++)
                {
                    double next = Math.Max(0, weights[i] - step * gradient[i]);
                    change = Math.Max(change, Math.Abs(next - weights[i]));
                    weights[i] = next;
                }
                if (change < 1e-8) break;
            }
            return new IntervalRidge(weights, scales);
        }
    }
}
