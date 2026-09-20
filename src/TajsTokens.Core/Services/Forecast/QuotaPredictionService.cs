// Taj's Tokens | QuotaPredictionService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public sealed record QuotaPredictionTrial(QuotaForecastTrial Observation, QuotaHorizonPrediction Prediction);

/// <summary>
///     Shared live/replay policy: horizon-specific pace selection and an earned workload correction.
///     Model choice needs matured non-overlapping outcomes, not eight completed weekly resets.
/// </summary>
public static partial class QuotaPredictionService
{
    public const string PolicyVersion = "quota-workload/v5-blocks";
    public const string FallbackModel = "time-ewma-2h";
    public const int MinimumSelectionSamples = 6;
    public const int MinimumIntervalSamples = 20;

    public static IReadOnlyList<QuotaHorizonPrediction> Predict(
        CodexForecastDataset data,
        QuotaSnapshot anchor,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (anchor.Authority != QuotaObservationAuthority.ProviderAuthoritative || anchor.CapturedAtUtc > now ||
            now - anchor.CapturedAtUtc > TimeSpan.FromHours(6) || anchor.ResetsAtUtc is not { } reset || reset <= now ||
            anchor.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100) return [];
        IEnumerable<QuotaSnapshot> prefix = data.Quota.Where(x => SameStream(x, anchor) && x.CapturedAtUtc < anchor.CapturedAtUtc &&
                                                                  (x.CollectedAtUtc is null || x.CollectedAtUtc <= anchor.CapturedAtUtc))
            .Append(anchor);
        CodexForecastDataset local = data with
        {
            Quota = QuotaHistoryPolicy.ReplayRows(
                QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(prefix, now)).SelectMany(x => x)),
        };
        IReadOnlyList<QuotaSnapshot>? epoch = QuotaForecastCalibration.SplitEpochs(local.Quota).LastOrDefault();
        if (epoch is null || epoch[^1] != anchor || epoch.Count < 2 ||
            anchor.CapturedAtUtc - epoch[0].CapturedAtUtc < TimeSpan.FromMinutes(15)) return [];
        var result = new List<QuotaHorizonPrediction>();
        foreach (double horizon in anchor.Kind == QuotaWindowKind.Weekly ? new[] { 0.5, 2d, 24d } : new[] { 0.5, 2d })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchor.CapturedAtUtc.AddHours(horizon) > reset || anchor.CapturedAtUtc.AddHours(horizon) <= now) continue;
            (IReadOnlyList<QuotaPredictionTrial> Trials, QuotaHorizonPrediction? Current,
                IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> Targets) evaluated = Evaluate(
                    local,
                    horizon,
                    ForecastReplayAvailability.CollectedByOrigin,
                    anchor,
                    cancellationToken);
            if (evaluated.Current is { } current) result.Add(current);
        }
        return ComposedQuotaPolicy.Apply(data, anchor, result, cancellationToken);
    }

    public static IReadOnlyList<QuotaPredictionTrial> Replay(
        CodexForecastDataset data,
        double horizon,
        ForecastReplayAvailability availability,
        CancellationToken cancellationToken = default)
    {
        return Evaluate(data, horizon, availability, null, cancellationToken).Trials;
    }

    /// <summary>Scores exact comparison targets without admitting them to the incumbent's training/selection history.</summary>
    public static IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> ReplayAtTargets(
        CodexForecastDataset data,
        double horizon,
        ForecastReplayAvailability availability,
        IReadOnlyList<(DateTimeOffset Origin, DateTimeOffset Outcome)> targets,
        CancellationToken cancellationToken = default)
    {
        return Evaluate(data, horizon, availability, null, cancellationToken, targets).Targets;
    }

    private static (IReadOnlyList<QuotaPredictionTrial> Trials, QuotaHorizonPrediction? Current,
        IReadOnlyDictionary<DateTimeOffset, QuotaHorizonPrediction> Targets) Evaluate(
            CodexForecastDataset data,
            double horizon,
            ForecastReplayAvailability availability,
            QuotaSnapshot? current,
            CancellationToken cancellationToken,
            IReadOnlyList<(DateTimeOffset Origin, DateTimeOffset Outcome)>? targets = null)
    {
        // The caller supplies one source/account/window stream. SplitEpochs rejects mixed streams.
        Dictionary<string, Dictionary<DateTimeOffset, QuotaForecastTrial>> models = QuotaPaceModels.Candidates.ToDictionary(
            model => model,
            model => QuotaForecastCalibration.Replay(data.Quota, model, horizon, cancellationToken)
                .ToDictionary(x => x.OriginUtc));
        QuotaForecastTrial[] labels = models[FallbackModel].Values.OrderBy(x => x.OriginUtc).ToArray();
        Dictionary<DateTimeOffset, QuotaSnapshot>
            anchors = data.Quota.GroupBy(x => x.CapturedAtUtc).ToDictionary(x => x.Key, x => x.Last());
        var featureRows = new Dictionary<DateTimeOffset, CodexForecastFeatures>();
        foreach (QuotaForecastTrial label in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            featureRows[label.OriginUtc] = CodexForecastFeatureBuilder.Build(data, label.OriginUtc, 2, availability);
        }
        var points = new List<Point>();
        foreach (QuotaForecastTrial label in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, double> estimates = models.ToDictionary(x => x.Key, x => x.Value[label.OriginUtc].PredictedRemaining);
            points.Add(PredictAt(anchors[label.OriginUtc], label.LeadHours, estimates, label));
        }
        QuotaHorizonPrediction? live = null;
        if (current is not null)
        {
            IReadOnlyList<QuotaSnapshot> epoch = QuotaForecastCalibration.SplitEpochs(data.Quota).Last();
            live = PredictAt(
                current,
                horizon,
                QuotaPaceModels.Candidates.ToDictionary(
                    model => model,
                    model => QuotaPaceModels.ProjectRemaining(epoch, model, horizon)),
                null).Prediction;
        }
        var matched = new Dictionary<DateTimeOffset, QuotaHorizonPrediction>();
        if (targets is { Count: > 0 })
        {
            IReadOnlyList<IReadOnlyList<QuotaSnapshot>> epochs = QuotaForecastCalibration.SplitEpochs(data.Quota);
            Dictionary<DateTimeOffset, IReadOnlyList<QuotaSnapshot>> epochByOrigin = epochs
                .SelectMany(epoch => epoch.Select(row => (row.CapturedAtUtc, Epoch: epoch)))
                .GroupBy(x => x.CapturedAtUtc).ToDictionary(x => x.Key, x => x.Last().Epoch);
            Dictionary<DateTimeOffset, Point> sampled = points.ToDictionary(x => x.Label!.OriginUtc);
            foreach ((DateTimeOffset Origin, DateTimeOffset Outcome) target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!epochByOrigin.TryGetValue(target.Origin, out IReadOnlyList<QuotaSnapshot>? epoch) ||
                    !anchors.TryGetValue(target.Origin, out QuotaSnapshot? anchor) || anchor.UsedPercent >= 100 ||
                    target.Origin - epoch[0].CapturedAtUtc < TimeSpan.FromMinutes(15)) continue;
                DateTimeOffset nominal = target.Origin.AddHours(horizon);
                QuotaSnapshot? outcome = epoch.FirstOrDefault(x => x.CapturedAtUtc >= nominal);
                if (outcome is null || outcome.CapturedAtUtc != target.Outcome ||
                    target.Outcome - nominal > TimeSpan.FromMinutes(Math.Min(5, horizon * 60 * .2))) continue;
                if (sampled.TryGetValue(target.Origin, out Point? existing) && existing.Label!.OutcomeUtc == target.Outcome)
                {
                    matched[target.Origin] = existing.Prediction with { TargetUtc = target.Outcome };
                    continue;
                }
                QuotaSnapshot[] prefix = epoch.TakeWhile(x => x.CapturedAtUtc <= target.Origin).ToArray();
                double lead = (target.Outcome - target.Origin).TotalHours;
                Dictionary<string, double> estimates = QuotaPaceModels.Candidates.ToDictionary(
                    model => model,
                    model => QuotaPaceModels.ProjectRemaining(prefix, model, lead));
                // PredictAt selects only matured points/labels. Do not append this extra query
                // to points: querying another target must never change a later prediction.
                matched[target.Origin] = PredictAt(anchor, lead, estimates, null).Prediction with { TargetUtc = target.Outcome };
            }
        }
        return (points.Select(x => new QuotaPredictionTrial(x.Label!, x.Prediction)).ToArray(), live, matched);

        Point PredictAt(QuotaSnapshot anchor, double lead, IReadOnlyDictionary<string, double> estimates, QuotaForecastTrial? label)
        {
            Point[] matured = Independent(points.Where(x => x.Label!.OutcomeUtc <= anchor.CapturedAtUtc)).TakeLast(48).ToArray();
            string selected = FallbackModel;

            double Error(string model, IEnumerable<Point> observations)
            {
                return observations.Average(point =>
                    Math.Abs(models[model][point.Label!.OriginUtc].PredictedRemaining - point.Label.ObservedRemaining));
            }

            if (matured.Length >= MinimumSelectionSamples)
            {
                string best = QuotaPaceModels.Candidates.MinBy(model => Error(model, matured))!;
                if (Error(best, matured) + 0.05 < Error(FallbackModel, matured) * 0.9) selected = best;
            }
            double baseline = estimates[selected];
            QuotaWorkloadPrediction workload = QuotaWorkloadCorrection.Predict(
                data,
                anchor,
                lead,
                estimates[FallbackModel],
                labels,
                featureRows,
                availability);
            Point[] validation = Independent(points.Where(x => x.Label!.OutcomeUtc <= anchor.CapturedAtUtc && x.Workload.Fitted))
                .TakeLast(48).ToArray();
            double? baselineError = validation.Length > 0 ? Error(FallbackModel, validation) : null;
            double? selectedError = validation.Length > 0 ? Error(selected, validation) : null;
            double? workloadError = validation.Length > 0
                ? validation.Average(x => Math.Abs(x.Workload.Remaining - x.Label!.ObservedRemaining))
                : null;
            bool useWorkload = workload.Fitted && validation.Length >= MinimumSelectionSamples &&
                               workloadError + 0.05 < baselineError * 0.9 && workloadError + 0.05 < selectedError * 0.9;
            double prediction = useWorkload ? workload.Remaining : baseline;
            double[] errors = matured.Select(x => Math.Abs(x.Prediction.RemainingPercent - x.Label!.ObservedRemaining)).Order().ToArray();
            double? radius = errors.Length >= MinimumIntervalSamples
                ? Math.Max(1, errors[Math.Min(errors.Length - 1, (int)Math.Ceiling((errors.Length + 1) * 0.8) - 1)])
                : null;
            string explanation = useWorkload
                ? $"Workload correction earned selection on {validation.Length} matured non-overlapping outcomes; MAE {workloadError:0.##}pp versus pace-only {baselineError:0.##}pp."
                : !workload.Fitted
                    ? $"Pace-only estimate; workload learning has {workload.TrainingSamples} compatible training outcomes (need 12), or current model/effort evidence is missing or unseen."
                    : $"Pace-only estimate; workload has {validation.Length} held-out outcomes (need 6 and a material error improvement).";
            explanation += $" Pace selection: {matured.Length} matured non-overlapping outcomes. " +
                           (radius is null
                               ? "Uncertainty is learning; no calibrated band or exhaustion probability is claimed."
                               : $"Empirical 80%-target band from {errors.Length} earlier non-overlapping prediction errors; at least 1pp meter-resolution allowance, not a provider precision guarantee.") +
                           " Assumes recent conditions remain informative. Local model/effort/token/activity signals are co-observed, not verified account attribution.";
            var result = new QuotaHorizonPrediction(
                horizon,
                anchor.CapturedAtUtc.AddHours(lead),
                prediction,
                Math.Max(0, anchor.RemainingPercent!.Value - prediction),
                useWorkload ? "model-effort-ridge" : selected,
                useWorkload,
                workload.TrainingSamples,
                useWorkload ? validation.Length : matured.Length,
                useWorkload ? workloadError : matured.Length > 0 ? Error(selected, matured) : null,
                radius is { } r ? Math.Max(0, prediction - r) : null,
                radius is { } upper ? Math.Min(anchor.RemainingPercent.Value, prediction + upper) : null,
                errors.Length,
                explanation)
            {
                Inference = useWorkload
                    ? workload.Inference
                    : new CodexNumericInference(
                        "selected-pace-output/v1:" + selected,
                        baseline,
                        [],
                        [],
                        0,
                        anchor.RemainingPercent.Value),
            };
            return new Point(label, result, baseline, workload);
        }
    }

    private static IEnumerable<Point> Independent(IEnumerable<Point> points)
    {
        DateTimeOffset? previousEnd = null;
        foreach (Point point in points.OrderBy(x => x.Label!.OriginUtc))
        {
            if (previousEnd is not null && point.Label!.OriginUtc < previousEnd) continue;
            previousEnd = point.Label!.OutcomeUtc;
            yield return point;
        }
    }

    private static bool SameStream(QuotaSnapshot x, QuotaSnapshot y)
    {
        return QuotaHistoryPolicy.Cohort(x) == QuotaHistoryPolicy.Cohort(y);
    }

    private sealed record Point(
        QuotaForecastTrial? Label,
        QuotaHorizonPrediction Prediction,
        double BasePrediction,
        QuotaWorkloadPrediction Workload);
}