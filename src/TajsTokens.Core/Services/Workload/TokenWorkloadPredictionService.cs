// Taj's Tokens | TokenWorkloadPredictionService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>
///     Learns recorded local token consumption directly from rollouts, independently of quota labels.
///     Historical features are event-time reconstructions, not claims about past deployment availability.
/// </summary>
public static class TokenWorkloadPredictionService
{
    public const string PolicyVersion = "rollout-token/v2";

    public const string Methodology = "Reconstructed rollout history: predict the sum of recorded token increments " +
                                      "(including cached input) in the next interval, conditional on recent local activity. " +
                                      "Chronological held-out outcomes; future tokens, completions and model changes are excluded from inputs. " +
                                      "A zero target means no tokens recorded in that interval, not proof of no account activity. " +
                                      "This installation's corpus is not the entire account; no subscription-quota or money conversion is implied. " +
                                      "Backfilled evidence is usable for retrospective learning, not proof that an earlier app had collected it.";

    private static readonly string[] Candidates = ["recent-30m", "recent-2h", "recent-median", "workload-ridge", "workload-linear"];

    public static TokenHorizonPrediction? PredictHorizon(
        CodexForecastDataset data,
        DateTimeOffset origin,
        double horizon,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime,
        CancellationToken cancellationToken = default)
    {
        CodexForecastDataset prefix = data with
        {
            Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= origin).ToArray(),
            Workload = data.Workload.Where(x => x.ObservedAtUtc <= origin).ToArray(),
            Context = data.Context.Where(x => x.ObservedAtUtc <= origin).ToArray(),
        };
        if (availability == ForecastReplayAvailability.CollectedByOrigin)
            prefix = prefix with
            {
                Tokens = prefix.Tokens.Where(x => x.CapturedAtUtc <= origin).ToArray(),
                Workload = prefix.Workload.Where(x => x.CapturedAtUtc <= origin).ToArray(),
                Context = prefix.Context.Where(x => x.CapturedAtUtc <= origin).ToArray(),
            };
        if (!prefix.Tokens.Any(x => x.ObservedAtUtc > origin.AddHours(-2))) return null;
        if (horizon <= .25 && !CodexNowcastActivity.Evaluate(prefix, origin, availability).SupportsNowcast) return null;
        TokenHorizonPrediction? prediction = Evaluate(prefix, horizon, origin, cancellationToken).Current;
        return prediction is null
            ? null
            : prediction with { Composition = WorkloadCompositionPrediction.Project(prefix, origin, prediction, availability) };
    }

    public static TokenWorkloadForecast Predict(
        CodexForecastDataset data,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        CodexForecastDataset available = Available(data, now);
        DateTimeOffset? last = available.Tokens.Count > 0 ? available.Tokens.Max(x => x.ObservedAtUtc) : null;
        var predictions = new List<TokenHorizonPrediction>();
        NowcastActivity activity = CodexNowcastActivity.Evaluate(available, now);
        if (activity.SupportsNowcast && last is not null && now - last <= TimeSpan.FromHours(2))
            foreach (double horizon in new[] { 5d / 60, .25 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                (IReadOnlyList<TokenForecastTrial> Trials, TokenHorizonPrediction? Current, IReadOnlyList<Point> Points) result = Evaluate(
                    available,
                    horizon,
                    now,
                    cancellationToken);
                if (result.Current is not null)
                    predictions.Add(
                        result.Current with { Composition = WorkloadCompositionPrediction.Project(available, now, result.Current) });
            }
        if (activity.SupportsNowcast && predictions.Count == 0)
            activity = activity with { Explanation = activity.Explanation + " Insufficient recent token history to anchor a nowcast." };
        return new TokenWorkloadForecast(
            now,
            last,
            available.Tokens.Count,
            available.Tokens.Select(x => x.SessionId).Distinct().Count(),
            predictions,
            Methodology + " Live nowcasts use a ten-minute observed-activity gate, not a liveness guarantee. " + activity.Explanation)
        {
            Activity = activity,
            SessionOutlooks = activity.SupportsNowcast
                ? SessionWorkloadPredictionService.Evaluate(available, now, cancellationToken).Current
                : [],
        };
    }

    public static IReadOnlyList<TokenForecastTrial> Replay(
        CodexForecastDataset data,
        double horizon,
        CancellationToken cancellationToken = default)
    {
        return Evaluate(data, horizon, null, cancellationToken).Trials
            .Select(x => x with
            {
                Prediction = x.Prediction with { Composition = WorkloadCompositionPrediction.Project(data, x.OriginUtc, x.Prediction) },
            }).ToArray();
    }

    public static IReadOnlyList<TokenForecastScore> EvaluateScores(
        CodexForecastDataset data,
        CancellationToken cancellationToken = default)
    {
        var scores = new List<TokenForecastScore>();
        foreach (double horizon in new[] { 5d / 60, .25, 0.5, 2d })
        {
            (IReadOnlyList<TokenForecastTrial> Trials, TokenHorizonPrediction? Current, IReadOnlyList<Point> Points) evaluated = Evaluate(
                data,
                horizon,
                null,
                cancellationToken);
            foreach (string candidate in Candidates.Append("zero-workload").Append(PolicyVersion))
            {
                // Candidate predictions are exposed by the same evaluation pass, not refitted on outcomes.
                double[] errors = evaluated.Points.Select(x => (candidate == PolicyVersion
                    ? x.Prediction.ExpectedTokens
                    : x.Estimates.GetValueOrDefault(candidate, x.Estimates["recent-30m"])) - x.Row.Target).ToArray();
                TokenForecastTrial[] bands = candidate == PolicyVersion
                    ? evaluated.Trials.Where(x => x.Prediction.LowerTokens is not null).ToArray()
                    : [];
                scores.Add(
                    new TokenForecastScore(
                        horizon,
                        candidate,
                        evaluated.Trials.Count,
                        errors.Length > 0 ? errors.Average(Math.Abs) : null,
                        errors.Length > 0 ? Math.Sqrt(errors.Average(x => x * x)) : null,
                        bands.Length,
                        bands.Length > 0
                            ? bands.Average(x => x.ObservedTokens >= x.Prediction.LowerTokens &&
                                                 x.ObservedTokens <= x.Prediction.UpperTokens
                                ? 1d
                                : 0d)
                            : null,
                        candidate == PolicyVersion
                            ? evaluated.Points.Count(x => x.Prediction.Model.StartsWith("workload-", StringComparison.Ordinal))
                            : candidate.StartsWith("workload-", StringComparison.Ordinal)
                                ? evaluated.Points.Count(x => x.Estimates.ContainsKey(candidate))
                                : 0));
            }
        }
        return scores;
    }

    private static (IReadOnlyList<TokenForecastTrial> Trials, TokenHorizonPrediction? Current, IReadOnlyList<Point> Points) Evaluate(
        CodexForecastDataset data,
        double horizon,
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(horizon) || horizon <= 0 || horizon > 24 || TimeSpan.FromHours(horizon).Ticks == 0)
            throw new ArgumentOutOfRangeException(nameof(horizon));
        CodexPredictiveTokenEvent[] tokens = data.Tokens.Where(x => x.ReportedTotalTokens >= 0).OrderBy(x => x.ObservedAtUtc).ToArray();
        if (tokens.Length < 2) return ([], null, []);
        decimal[] sums = new decimal[tokens.Length + 1];
        for (int i = 0; i < tokens.Length; i++) sums[i + 1] = sums[i] + tokens[i].ReportedTotalTokens;
        CodexWorkloadObservation[] workload = data.Workload.Where(x => x.ObservedAtUtc is not null).OrderBy(x => x.ObservedAtUtc).ToArray();
        DateTimeOffset[] workloadTimes = workload.Select(x => x.ObservedAtUtc!.Value).ToArray();
        DateTimeOffset[] activityTimes = tokens.Where(x => x.ReportedTotalTokens > 0).Select(x => x.ObservedAtUtc)
            .Concat(workload.Where(x => CodexNowcastActivity.IsActivityEvent(x.EventType)).Select(x => x.ObservedAtUtc!.Value)).Order()
            .ToArray();
        Dictionary<string, CodexWorkloadObservation[]> bySession =
            workload.GroupBy(x => x.SessionId).ToDictionary(x => x.Key, x => x.ToArray());
        CodexContextObservation[] contexts = data.Context.OrderBy(x => x.ObservedAtUtc).ToArray();
        DateTimeOffset[] contextTimes = contexts.Select(x => x.ObservedAtUtc).ToArray();

        int Through(DateTimeOffset at)
        {
            int low = 0;
            int high = tokens.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (tokens[middle].ObservedAtUtc <= at) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        double Window(DateTimeOffset from, DateTimeOffset to)
        {
            return (double)(sums[Through(to)] - sums[Through(from)]);
        }

        CodexForecastFeatures Features(DateTimeOffset at)
        {
            DateTimeOffset from = at.AddHours(-2);
            CodexPredictiveTokenEvent[] recentTokens = tokens[Through(from)..Through(at)];
            CodexWorkloadObservation[] recentWorkload = workload[UpperBound(workloadTimes, from)..UpperBound(workloadTimes, at)];
            IEnumerable<string> sessions = recentTokens.Select(x => x.SessionId).Concat(recentWorkload.Select(x => x.SessionId)).Distinct();
            // Keep complete prefixes for relevant sessions, including starts before the lookback.
            // Old inactive sessions cannot affect these features. The owner still applies the time cutoff.
            CodexForecastDataset local = data with
            {
                Tokens = recentTokens,
                Workload = sessions.Where(bySession.ContainsKey).SelectMany(x => bySession[x]).ToArray(),
                Context = contexts[UpperBound(contextTimes, from)..UpperBound(contextTimes, at)],
            };
            return CodexForecastFeatureBuilder.Build(local, at);
        }

        var rows = new List<Row>();
        DateTimeOffset first = tokens[0].ObservedAtUtc.AddHours(2);
        DateTimeOffset last = tokens[^1].ObservedAtUtc;
        // Non-overlapping target windows; no quota reset epochs are necessary for native token targets.
        TimeSpan step = TimeSpan.FromHours(horizon);
        var origin = new DateTimeOffset((first.UtcTicks / step.Ticks + 1) * step.Ticks, TimeSpan.Zero);
        for (; origin.AddHours(horizon) <= last; origin += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Do not rebuild turn metadata for months of idle grid points between sessions.
            if (Through(origin) == Through(origin.AddHours(-2))) continue;
            if (horizon <= .25)
            {
                int throughActivity = UpperBound(activityTimes, origin);
                if (throughActivity == 0 || origin - activityTimes[throughActivity - 1] >= CodexNowcastActivity.IdleAfter) continue;
            }
            CodexForecastFeatures features = Features(origin);
            if (features.ObservedTokenEvents == 0) continue;
            DateTimeOffset end = origin.AddHours(horizon);
            double target = Window(origin, end);
            rows.Add(new Row(origin, end, features, Recent(origin), target));
        }
        var points = new List<Point>();
        foreach (Row row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            points.Add(PredictAt(row));
        }
        TokenHorizonPrediction? current = null;
        if (now is { } at && at - tokens[0].ObservedAtUtc >= TimeSpan.FromHours(2))
        {
            CodexForecastFeatures features = Features(at);
            current = PredictAt(new Row(at, at.AddHours(horizon), features, Recent(at), 0)).Prediction;
        }
        return (points.Select(x => new TokenForecastTrial(x.Row.Origin, x.Row.End, x.Row.Target, x.Prediction)).ToArray(), current, points);

        double Recent(DateTimeOffset at)
        {
            return Window(at.AddMinutes(-30), at) * horizon / 0.5;
        }

        Point PredictAt(Row row)
        {
            Row[] training = rows.Where(x => x.End <= row.Origin && x.Origin < row.Origin).TakeLast(120).ToArray();
            var estimates = new Dictionary<string, double>
            {
                ["zero-workload"] = 0, // Evaluation-only inactivity baseline, not a fitted activity classifier.
                ["recent-30m"] = row.Recent,
                ["recent-2h"] = row.Features.Tokens * horizon / 2,
                ["recent-median"] = training.Length > 0 ? Median(training.TakeLast(8).Select(x => x.Target)) : row.Recent,
            };
            string[] models = training.SelectMany(x => x.Features.ModelTokenShares.Keys).Distinct().Order().Take(8).ToArray();
            string[] efforts = training.SelectMany(x => x.Features.EffortTokenShares.Keys).Distinct().Order().Take(8).ToArray();
            bool supported = row.Features.ModelTokenShares.Count > 0 && row.Features.EffortTokenShares.Count > 0 &&
                             !row.Features.ModelTokenShares.Keys.Except(models).Any() &&
                             !row.Features.EffortTokenShares.Keys.Except(efforts).Any();
            AccountLocalRidge? fit = training.Length >= 20 && supported
                ? AccountLocalRidge.Fit(
                    training.Select(x => Vector(x, models, efforts)).ToArray(),
                    training.Select(x => Math.Log(1 + x.Target)).ToArray(),
                    10)
                : null;
            if (fit is not null)
            {
                // Bound extrapolation to twice the largest observed training target, not a made-up quota conversion.
                double limit = Math.Max(1, training.Max(x => x.Target) * 2);
                estimates["workload-ridge"] = Math.Clamp(
                    Math.Exp(Math.Clamp(fit.Predict(Vector(row, models, efforts)), 0, Math.Log(1 + limit))) - 1,
                    0,
                    limit);
                AccountLocalRidge? linear = AccountLocalRidge.Fit(
                    training.Select(x => Vector(x, models, efforts)).ToArray(),
                    training.Select(x => x.Target - x.Recent).ToArray(),
                    100);
                if (linear is not null)
                    estimates["workload-linear"] = Math.Clamp(row.Recent + linear.Predict(Vector(row, models, efforts)), 0, limit);
            }
            Point[] validation = points.Where(x => x.Row.End <= row.Origin).TakeLast(48).ToArray();
            string selected = horizon >= 2 && training.Length >= 8 ? "recent-median" : "recent-30m";

            double Error(string model, IEnumerable<Point> source)
            {
                return source.Average(x => Math.Abs(x.Estimates[model] - x.Row.Target));
            }

            foreach (string candidate in Candidates)
            {
                if (!estimates.ContainsKey(candidate)) continue;
                Point[] compatible = validation.Where(x => x.Estimates.ContainsKey(candidate) && x.Estimates.ContainsKey(selected))
                    .ToArray();
                if (compatible.Length >= 16 && Error(candidate, compatible) < Error(selected, compatible) * 0.8) selected = candidate;
            }
            double predicted = estimates[selected];
            double[] selectedErrors = validation.Select(x => Math.Abs(x.Prediction.ExpectedTokens - x.Row.Target)).Order().ToArray();
            double? radius = selectedErrors.Length >= 20
                ? selectedErrors[Math.Min(selectedErrors.Length - 1, (int)Math.Ceiling((selectedErrors.Length + 1) * 0.8) - 1)]
                : null;
            Point[] applicable = validation.Where(x => x.Estimates.ContainsKey(selected)).ToArray();
            return new Point(
                row,
                estimates,
                new TokenHorizonPrediction(
                    horizon,
                    predicted,
                    selected,
                    training.Length,
                    applicable.Length,
                    applicable.Length > 0 ? Error(selected, applicable) : null,
                    radius is { } r ? Math.Max(0, predicted - r) : null,
                    radius is { } upper ? predicted + upper : null,
                    $"{training.Length} matured training intervals; {applicable.Length} held-out comparisons. " +
                    (selected.StartsWith("workload-", StringComparison.Ordinal)
                        ? "Model/effort/token/activity regression earned selection."
                        : "The simpler forecast currently wins or regression is still learning.") +
                    (radius is null
                        ? " Uncertainty is learning."
                        : " Empirical 80%-target range from earlier selected-policy errors; not a guarantee.")));
        }
    }

    private static double[] Vector(Row row, string[] models, string[] efforts)
    {
        CodexForecastFeatures f = row.Features;
        var result = new List<double>
        {
            Math.Log(1 + f.Tokens),
            Math.Log(1 + row.Recent),
            f.CacheReadShare ?? 0,
            f.ReasoningOutputShare ?? 0,
            f.ObservedTokenEvents,
            f.TokenActiveRootSessions,
            f.TokenActiveSubagentSessions,
            f.TokenActiveUnknownSessions,
            f.ObservedOpenTurns,
            f.CompletedTurns,
            f.CompletedTurnWallHours,
            f.Compactions,
            f.LastInputWindowRatio ?? 0,
            f.PeakObservedTurnOverlap,
        };
        result.AddRange(models.Select(x => f.ModelTokenShares.GetValueOrDefault(x)));
        result.AddRange(efforts.Select(x => f.EffortTokenShares.GetValueOrDefault(x)));
        return result.ToArray();
    }

    private static CodexForecastDataset Available(CodexForecastDataset data, DateTimeOffset now)
    {
        return data with
        {
            Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= now && (x.CapturedAtUtc is null || x.CapturedAtUtc <= now)).ToArray(),
            Workload = data.Workload.Where(x => x.ObservedAtUtc <= now && x.CapturedAtUtc <= now).ToArray(),
            Context = data.Context.Where(x => x.ObservedAtUtc <= now && (x.CapturedAtUtc is null || x.CapturedAtUtc <= now)).ToArray(),
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static int UpperBound(DateTimeOffset[] values, DateTimeOffset at)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (values[middle] <= at) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private sealed record Row(DateTimeOffset Origin, DateTimeOffset End, CodexForecastFeatures Features, double Recent, double Target);

    private sealed record Point(Row Row, IReadOnlyDictionary<string, double> Estimates, TokenHorizonPrediction Prediction);
}