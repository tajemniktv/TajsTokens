using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Frozen-prefix cost calibration, deliberately separate from future-workload forecasting.</summary>
public static class QuotaCostEvaluation
{
    public const string Version = "quota-cost-evaluation/v2";
    public static readonly string[] Candidates = ["persistence", "pace", "total", "categories", "model-effort",
        "context-ablation", "activity-ablation", "runtime-ablation", "time-ablation"];
    public const string Methodology = "Evaluation only: actual interval workload is an oracle cost input, NOT an end-to-end forecast. " +
        "Disjoint targets within each cohort/horizon; overlapping sources and horizons are never pooled. " +
        "First 20 non-overlapping matured observations fit a frozen nonnegative, no-intercept interval-distance ridge model. " +
        "Vocabulary/scaling use training only. Unknown model/effort mass is explicit. All later disjoint intervals are held out; no rolling refit hides drift. " +
        "Loss is distance to the measurement envelope; displayed-delta MAE is secondary. Rollout envelopes are sensitivity assumptions, not measured precision. " +
        "Residuals are associations, not causal attribution or proof of provider changes. Bands use earlier completed held-out generation maximum errors " +
        "after eight generations and target 80% envelope intersection, not calibrated latent coverage or exhaustion probability. " +
        "A diagnostic win needs 16 held-out intervals in three generations and >=10% and 0.1pp generation-average loss improvement over both pace and total; " +
        "richer candidates must also beat their simpler parent and win on most generations. No automatic production promotion. " +
        "API-price weighting is a fixed retrospective standard/short-context baseline, never credits or actual cost; incomplete pricing is withheld. " +
        "Its comparisons use matched outcomes and it cannot earn a promotion label.";

    public static QuotaCostReport Evaluate(CodexForecastDataset data, CancellationToken cancellationToken = default,
        bool userConfirmedRolloutOwnership = false)
    {
        var built = QuotaCostObservationBuilder.BuildDetailed(data, cancellationToken, userConfirmedRolloutOwnership);
        return Evaluate(built.Observations, (userConfirmedRolloutOwnership ?
            "User confirms retained rollouts belong to their account. Native account IDs remain absent; sessions/sources are not pooled. " : "") + data.Coverage + " " +
            QuotaHistoryPolicy.Summarize(QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc)), cancellationToken)
            with { DatasetCapturedAtUtc = data.CapturedAtUtc, EvidenceCoverage = QuotaEvaluationCoverageBuilder.Build(data),
                ConstructionCoverage = built.Coverage };
    }

    public static QuotaCostReport Evaluate(IReadOnlyList<QuotaCostObservation> rows, string coverage,
        CancellationToken cancellationToken = default)
    {
        var scores = new List<QuotaCostScore>();
        foreach (var group in rows.GroupBy(x => (x.Cohort, x.HorizonHours)))
        {
            var ordered = group.OrderBy(x => x.StartUtc).ToArray();
            var training = ordered.Take(20).ToArray();
            var trainingGenerations = QuotaResetGenerationPolicy.Group(training, x => x.ResetUtc).Count;
            var heldout = ordered.Skip(training.Length).Where(x => x.StartUtc >= training[^1].EndUtc).ToArray();
            var local = new List<QuotaCostScore>();
            foreach (var candidate in ordered.Any(x => x.ApiPriceWeight is not null) ? Candidates.Append("api-price") : Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trials = new List<QuotaCostTrial>();
                var vector = new CostVector(training, candidate);
                var unpricedTraining = candidate == "api-price" ? training.Count(x => !Priceable(x)) : 0;
                var unpricedHeldout = candidate == "api-price" ? heldout.Count(x => !Priceable(x)) : 0;
                var fitted = training.Length >= 20 && unpricedTraining == 0;
                var fit = fitted && candidate is not "pace" and not "persistence"
                    ? IntervalRidge.Fit(training.Select(vector.Values).ToArray(), training, cancellationToken) : null;
                foreach (var row in fitted ? heldout : [])
                {
                    if (candidate == "api-price" && !Priceable(row)) continue;
                    var prediction = candidate switch
                    {
                        "pace" => row.PaceDelta,
                        "persistence" => 0,
                        _ => fit!.Predict(vector.Values(row))
                    };
                    var errors = QuotaResetGenerationPolicy.Group(trials.Where(x =>
                            x.ResetUtc < row.StartUtc - QuotaResetGenerationPolicy.Tolerance), x => x.ResetUtc)
                        .Select(g => g.Max(x => x.IntervalLoss)).ToArray();
                    var radius = errors.Length >= 8 ? Quantile(errors, Math.Min(1, Math.Ceiling((errors.Length + 1) * 0.8) / errors.Length)) : null;
                    trials.Add(new(row.StartUtc, row.EndUtc, row.ResetUtc, row.ObservedDelta, row.LowerDelta, row.UpperDelta,
                        prediction, row.IntervalLoss(prediction), row.ObservedDelta - prediction,
                        radius is { } r ? Math.Max(0, prediction - r) : null, radius is { } r2 ? prediction + r2 : null));
                }
                var epochs = QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc);
                var bands = trials.Where(x => x.LowerPrediction is not null).ToArray();
                var residuals = trials.Select(x => x.Residual).ToArray();
                local.Add(new(group.Key.Cohort, group.Key.HorizonHours, candidate, ordered.Length,
                    training.Length, trainingGenerations, trials.Count, epochs.Count,
                    Mean(trials.Select(x => x.IntervalLoss)), Mean(residuals.Select(Math.Abs)),
                    Mean(epochs.Select(g => g.Average(x => x.IntervalLoss))),
                    Quantile(residuals, 0.1), Quantile(residuals, 0.5), Quantile(residuals, 0.9),
                    Mean(trials.Select(x => Math.Max(0, x.LowerDelta - x.Prediction))), bands.Length,
                    Mean(bands.Select(x => x.UpperPrediction >= x.LowerDelta && x.LowerPrediction <= x.UpperDelta ? 1d : 0d)),
                    false, unpricedTraining > 0 ? "unpriced-training-evidence" : fitted ? "evaluation-only" : "insufficient-training-intervals",
                    fit is null ? new Dictionary<string, double>() : vector.Names.Select((name, i) => (name, value: fit.Weights[i] / fit.Scales[i]))
                        .ToDictionary(x => x.name, x => x.value), trials, DetectShifts(epochs))
                {
                    RateCardVersion = candidate == "api-price" ? ApiPriceWorkload.Version : null,
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
                    score.Cohort.AccountKey is not null && score.HeldOutSamples >= 16 && score.HeldOutGenerations >= 3 &&
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

    private static bool Beats(QuotaCostScore score, QuotaCostScore parent)
    {
        if (score.GenerationMeanIntervalLoss is not { } loss || parent.GenerationMeanIntervalLoss is not { } baseline ||
            loss > baseline * 0.9 || baseline - loss < 0.1) return false;
        var pairs = score.Trials.Zip(parent.Trials).ToArray();
        var generations = QuotaResetGenerationPolicy.Group(pairs, x => x.First.ResetUtc);
        return generations.Count(g => g.Average(x => x.First.IntervalLoss) < g.Average(x => x.Second.IntervalLoss)) > generations.Count / 2;
    }

    internal static Func<QuotaCostObservation, double> FitFrozen(IReadOnlyList<QuotaCostObservation> training,
        string candidate, CancellationToken cancellationToken)
    {
        if (training.Count < 20 || candidate is not ("total" or "categories" or "model-effort"))
            throw new ArgumentException("Composition forecasting requires 20 cost observations and a supported composition model.");
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

    private static IReadOnlyList<DateTimeOffset> DetectShifts(IReadOnlyList<IReadOnlyList<QuotaCostTrial>> epochs)
    {
        // Freeze the reference after three held-out generations; require two later generations
        // with a replicated same-direction shift. This is a diagnostic, not a causal alarm.
        if (epochs.Count < 5) return [];
        var reference = epochs.Take(3).SelectMany(x => x).Select(x => x.Residual).ToArray();
        var center = Quantile(reference, 0.5)!.Value;
        var threshold = Math.Max(2, 3 * Quantile(reference.Select(x => Math.Abs(x - center)), 0.5)!.Value);
        var shifts = new List<DateTimeOffset>();
        for (var i = 4; i < epochs.Count; i++)
        {
            var a = Quantile(epochs[i - 1].Select(x => x.Residual), 0.5)!.Value - center;
            var b = Quantile(epochs[i].Select(x => x.Residual), 0.5)!.Value - center;
            if (epochs[i - 1].Count >= 3 && epochs[i].Count >= 3 && Math.Abs(a) > threshold &&
                Math.Abs(b) > threshold && Math.Sign(a) == Math.Sign(b)) shifts.Add(epochs[i][0].ResetUtc);
        }
        return shifts;
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
        public string[] Names { get; }

        public CostVector(IReadOnlyList<QuotaCostObservation> training, string candidate)
        {
            this.candidate = candidate;
            if (candidate == "api-price")
            {
                models = []; efforts = []; Names = ["standard-short-context-api-weight"]; return;
            }
            models = training.SelectMany(x => x.Features.ModelTokenShares.Keys).Distinct().Order().Take(16).ToArray();
            efforts = training.SelectMany(x => x.Features.EffortTokenShares.Keys).Distinct().Order().Take(16).ToArray();
            var names = new List<string>(candidate == "total" ? ["reported-total/M"] :
                new[] { "uncached/M", "cache-read/M", "cache-write/M", "normal-output/M", "reasoning-output/M" });
            if (Rich) names.AddRange(models.Select(x => "model:" + x).Concat(efforts.Select(x => "effort:" + x)).Concat(["unknown-model", "unknown-effort"]));
            names.AddRange(candidate switch
            {
                "context-ablation" => ["compactions", "context-pressure", "context-missing"],
                "activity-ablation" => ["root-sessions", "subagent-sessions", "unknown-sessions", "completed-turn-hours", "peak-overlap"],
                "runtime-ablation" => ["ttft-seconds", "runtime-missing"],
                "time-ablation" => ["utc-week-sin", "utc-week-cos"],
                _ => Array.Empty<string>()
            });
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
            values.AddRange((candidate switch
            {
                "context-ablation" => new[] { (double)f.Compactions, f.LastInputWindowRatio ?? 0, f.LastInputWindowRatio is null ? 1d : 0d },
                "activity-ablation" => new[] { (double)f.TokenActiveRootSessions, f.TokenActiveSubagentSessions, f.TokenActiveUnknownSessions, f.CompletedTurnWallHours, f.PeakObservedTurnOverlap },
                "runtime-ablation" => new[] { (row.MeanTtftMilliseconds ?? 0) / 1000, row.RuntimeSamples == 0 ? 1d : 0d },
                "time-ablation" => new[] { 1 + Math.Sin(WeekAngle(row.StartUtc)), 1 + Math.Cos(WeekAngle(row.StartUtc)) },
                _ => Array.Empty<double>()
            }).Select(x => x * total));
            return values.ToArray();
        }

        private static double WeekAngle(DateTimeOffset time) => 2 * Math.PI * ((int)time.UtcDateTime.DayOfWeek * 24 + time.UtcDateTime.TimeOfDay.TotalHours) / 168;
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
