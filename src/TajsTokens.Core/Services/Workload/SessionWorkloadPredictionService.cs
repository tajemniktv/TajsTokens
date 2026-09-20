// Taj's Tokens | SessionWorkloadPredictionService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Derived hurdle model over already-retained token increments. No new source collection.</summary>
public static class SessionWorkloadPredictionService
{
    public const string Policy = "recorded-session-outlook/v2";

    public const string Methodology = "Reconstructed event-time evaluation over disjoint 30/60-minute targets. " +
                                      "Activity means any positive recorded local tokens, not human presence or complete account activity. " +
                                      "After 20 matured origins, estimate activity frequency with Beta(1,1) smoothing in recent-token-age groups " +
                                      "(under 5, under 10, under 30, under 120 minutes); use the last 120 global origins if a group has fewer than 20. " +
                                      "Conditional workload uses positive training targets only, with at least eight examples. " +
                                      "Conditional 10th–90th percentile ranges describe training outcomes, not guaranteed predictive coverage. " +
                                      "Expected tokens = activity estimate × conditional mean; range endpoints are never multiplied. " +
                                      "Activity estimates are shown only after 64 earlier held-out origins in the same age group with binned calibration error <=0.1 " +
                                      "and Brier score no worse than the earlier-only global-frequency baseline. This is an empirical gate, " +
                                      "not proof of stable calibration. Completed quiet targets extend through the earlier of evaluation time and dataset capture, " +
                                      "not just the last token event; a stale snapshot cannot supply later negative outcomes. " +
                                      "Missing collection can still resemble inactivity; no new metadata is retained.";

    public static SessionWorkloadReport Evaluate(
        CodexForecastDataset data,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return EvaluateCore(data, now, now < data.CapturedAtUtc ? now : data.CapturedAtUtc, cancellationToken);
    }

    // Retrospective features at an exact quota origin, using evidence retained by dataset capture.
    // This is explicitly not collection-time deployment validation.
    internal static SessionWorkloadReport ReconstructAtOrigin(
        CodexForecastDataset data,
        DateTimeOffset origin,
        double horizon,
        CancellationToken cancellationToken)
    {
        return EvaluateCore(data, origin, data.CapturedAtUtc, cancellationToken, [horizon]);
    }

    private static SessionWorkloadReport EvaluateCore(
        CodexForecastDataset data,
        DateTimeOffset now,
        DateTimeOffset collectionCutoff,
        CancellationToken cancellationToken,
        IReadOnlyList<double>? horizons = null)
    {
        DateTimeOffset evidenceThrough = now < data.CapturedAtUtc ? now : data.CapturedAtUtc;
        // Collection cutoff protects the current view; historical scores are explicitly reconstructed,
        // not claims that backfilled evidence was deployed at its original event time.
        CodexPredictiveTokenEvent[] tokens = data.Tokens.Where(x => x.ObservedAtUtc <= evidenceThrough &&
                                                                    (x.CapturedAtUtc is null || x.CapturedAtUtc <= collectionCutoff) &&
                                                                    x.ReportedTotalTokens > 0)
            .OrderBy(x => x.ObservedAtUtc).ToArray();
        if (tokens.Length < 2) return new SessionWorkloadReport(Policy, Methodology, [], [], []);
        DateTimeOffset[] times = tokens.Select(x => x.ObservedAtUtc).ToArray();
        double[] sums = new double[tokens.Length + 1];
        for (int i = 0; i < tokens.Length; i++) sums[i + 1] = sums[i] + tokens[i].ReportedTotalTokens;

        int Through(DateTimeOffset at)
        {
            int lo = 0;
            int hi = times.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (times[mid] <= at) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        double Window(DateTimeOffset from, DateTimeOffset to)
        {
            return sums[Through(to)] - sums[Through(from)];
        }

        int AgeGroup(DateTimeOffset at)
        {
            int index = Through(at) - 1;
            double age = index < 0 ? double.PositiveInfinity : (at - times[index]).TotalMinutes;
            return age < 5 ? 0 : age < 10 ? 1 : age < 30 ? 2 : age < 120 ? 3 : 4;
        }

        var allTrials = new List<SessionWorkloadTrial>();
        var current = new List<SessionWorkloadPrediction>();
        var scores = new List<SessionWorkloadScore>();
        foreach (double horizon in horizons ?? [.5, 1d])
        {
            TimeSpan step = TimeSpan.FromHours(horizon);
            DateTimeOffset first = times[0].AddHours(2);
            var origin = new DateTimeOffset((first.UtcTicks / step.Ticks + 1) * step.Ticks, TimeSpan.Zero);
            var history = new List<(DateTimeOffset End, int Group, double Target)>();
            var trials = new List<SessionWorkloadTrial>();
            for (; origin + step <= evidenceThrough; origin += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int group = AgeGroup(origin);
                if (group == 4) continue; // No multi-day grid extrapolation from abandoned work.
                double target = Window(origin, origin + step);
                if (Predict(origin, group) is { } prediction)
                    trials.Add(
                        new SessionWorkloadTrial(
                            origin,
                            origin + step,
                            group,
                            target,
                            prediction,
                            Probability(history.Where(x => x.End <= origin).TakeLast(120).Select(x => x.Target)),
                            Window(origin.AddMinutes(-30), origin) * horizon / .5));
                history.Add((origin + step, group, target));
            }
            scores.Add(Score(horizon, trials));
            int currentGroup = AgeGroup(now);
            // Same ten-minute token recency requirement as the current nowcast, not an activity oracle.
            if (currentGroup < 2 && Predict(now, currentGroup) is { } live) current.Add(live);
            allTrials.AddRange(trials);

            SessionWorkloadPrediction? Predict(DateTimeOffset at, int group)
            {
                (DateTimeOffset End, int Group, double Target)[] past = history.Where(x => x.End <= at).TakeLast(120).ToArray();
                if (past.Length < 20) return null;
                (DateTimeOffset End, int Group, double Target)[] similar = past.Where(x => x.Group == group).ToArray();
                (DateTimeOffset End, int Group, double Target)[] training = similar.Length >= 20 ? similar : past;
                double[] active = training.Where(x => x.Target > 0).Select(x => x.Target).Order().ToArray();
                double probability = Probability(training.Select(x => x.Target));
                SessionWorkloadTrial[] validation = trials.Where(x => x.EndUtc <= at && x.AgeGroup == group).TakeLast(128).ToArray();
                SessionWorkloadScore score = Score(horizon, validation);
                bool supported = validation.Length >= 64 && score.CalibrationError <= .1 && score.BrierScore <= score.BaselineBrierScore;
                double? mean = active.Length >= 8 ? active.Average() : null;
                return new SessionWorkloadPrediction(
                    horizon,
                    training.Length,
                    active.Length,
                    probability,
                    mean,
                    mean is not null ? Quantile(active, .1) : null,
                    mean is not null ? Quantile(active, .9) : null,
                    mean * probability,
                    supported,
                    $"{training.Length} earlier origins ({active.Length} with recorded work); " +
                    (similar.Length >= 20 ? "matched recent-token-age group. " : "global fallback: age group sparse. ") +
                    $"{validation.Length} earlier held-out activity outcomes. " +
                    (supported
                        ? "Activity estimate passed the empirical calibration/baseline gate; drift remains possible."
                        : "Activity probability withheld: insufficient or unreliable held-out calibration. Conditional workload remains a scenario, not a claim that work will occur."));
            }
        }
        return new SessionWorkloadReport(Policy, Methodology, current, scores, allTrials);
    }

    private static double Probability(IEnumerable<double> values)
    {
        double[] rows = values.ToArray();
        return (rows.Count(x => x > 0) + 1d) / (rows.Length + 2d);
    }

    private static double Quantile(double[] sorted, double quantile)
    {
        return sorted[(int)Math.Floor((sorted.Length - 1) * quantile)];
    }

    private static double? Mean(IEnumerable<double> values)
    {
        double[] array = values.ToArray();
        return array.Length == 0 ? null : array.Average();
    }

    private static SessionWorkloadScore Score(double horizon, IReadOnlyList<SessionWorkloadTrial> trials)
    {
        SessionWorkloadTrial[] active = trials.Where(x => x.ObservedTokens > 0 && x.Prediction.ConditionalMeanTokens is not null).ToArray();
        SessionWorkloadTrial[] expected = trials.Where(x => x.Prediction.ExpectedTokens is not null).ToArray();
        double? calibration = trials.Count == 0
            ? null
            : trials.GroupBy(x => Math.Min(4, (int)(x.Prediction.RecordedActivityProbability * 5)))
                .Sum(g => (double)g.Count() / trials.Count * Math.Abs(
                    g.Average(x => x.Prediction.RecordedActivityProbability) - g.Average(x => x.ObservedTokens > 0 ? 1d : 0d)));
        return new SessionWorkloadScore(
            horizon,
            trials.Count,
            trials.Count(x => x.ObservedTokens > 0),
            Mean(trials.Select(x => Math.Pow(x.Prediction.RecordedActivityProbability - (x.ObservedTokens > 0 ? 1 : 0), 2))),
            Mean(trials.Select(x => Math.Pow(x.BaselineProbability - (x.ObservedTokens > 0 ? 1 : 0), 2))),
            calibration,
            active.Length,
            Mean(active.Select(x => Math.Abs(x.Prediction.ConditionalMeanTokens!.Value - x.ObservedTokens))),
            Mean(active.Select(x => Math.Abs(x.PaceTokens - x.ObservedTokens))),
            Mean(
                active.Select(x =>
                    x.ObservedTokens >= x.Prediction.ConditionalLowTokens && x.ObservedTokens <= x.Prediction.ConditionalHighTokens
                        ? 1d
                        : 0d)),
            Mean(expected.Select(x => Math.Abs(x.Prediction.ExpectedTokens!.Value - x.ObservedTokens))),
            Mean(expected.Select(x => Math.Abs(x.PaceTokens - x.ObservedTokens)))) { ExpectedOrigins = expected.Length };
    }
}