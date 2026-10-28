// Taj's Tokens | QuotaPredictionService.Scenarios.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Account-local conditional scenarios, selected using matured chronological outcomes.</summary>
public static partial class QuotaPredictionService
{
    private const int MinimumScenarioSamples = 12;

    public static ScenarioEstimate Simulate(
        ScenarioRequest request,
        IReadOnlyList<ScenarioHistorySample> history,
        DateTimeOffset? evaluatedAtUtc = null, ForecastEvaluationOptions? options = null)
    {
        options = (options ?? ForecastEvaluationOptions.Default).Validate();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);
        if (!double.IsFinite(request.DurationHours) || request.DurationHours <= 0 || request.DurationHours > 168 ||
            request.RootAgents < 0 || request.Subagents < 0 || (long)request.RootAgents + request.Subagents <= 0 ||
            !double.IsFinite(request.IntensityMultiplier) || request.IntensityMultiplier <= 0 || request.IntensityMultiplier > 5)
            throw new ArgumentOutOfRangeException(nameof(request));
        DateTimeOffset now = evaluatedAtUtc ?? DateTimeOffset.UtcNow;
        return new ScenarioEstimate(
            request,
            Build(QuotaWindowKind.FiveHour),
            Build(QuotaWindowKind.Weekly),
            "Account-local conditional estimates from authoritative meter intervals, including unchanged readings. " +
            "Active-session counts describe recent observed activity, not simultaneous compute or a causal agent-cost conversion. " +
            "A workload ridge candidate must beat the elapsed-time baseline on earlier chronological outcomes. " +
            "No universal token-to-quota conversion or calibrated exhaustion probability is assumed.") { EvaluatedAtUtc = now };

        ScenarioWindowEstimate Build(QuotaWindowKind kind)
        {
            ScenarioHistorySelection selection = ScenarioHistoryEligibility.Select(request, history, kind, now);
            ScenarioHistorySample[] samples = selection.Samples.ToArray();

            ScenarioWindowEstimate Unknown(string reason, int count, ForecastSupportReason support = ForecastSupportReason.InsufficientHistory)
            {
                return new ScenarioWindowEstimate(kind, false, count, null, null, null, 0,
                    reason + " " + selection.Describe()) { SupportReasons = support };
            }

            if (selection.IsStale)
                return Unknown("History is stale; a usable interval within 30 days is required.", samples.Length, ForecastSupportReason.StaleMeter);
            ScenarioHistorySample? latest = selection.Latest;
            if (samples.Length < options.ScenarioSamples)
                return Unknown(
                    $"Not enough matching history: need {options.ScenarioSamples} intervals; have {samples.Length}. Requested model/effort cohorts are never replaced silently.",
                    samples.Length);
            if (request.IntensityMultiplier != 1)
                return Unknown(
                    "Intensity multipliers have no observed source contract or validated response curve. Use intensity 1 for an evidence-backed conditional estimate.",
                    samples.Length, ForecastSupportReason.OutsideObservedSupport);
            IReadOnlyList<string> supportReasons = ScenarioHistoryEligibility.RequestSupportReasons(request, samples);
            if (supportReasons.Count > 0)
                return Unknown(
                    "Requested " + string.Join(", ", supportReasons.Select(x => x switch
                    {
                        "duration-outside-support" => "duration",
                        "root-sessions-outside-support" => "root-session count",
                        "subagent-sessions-outside-support" => "subagent-session count",
                        _ => "intensity",
                    })) + (supportReasons.Count == 1 ? " is" : " are") + " outside observed support; extrapolation is unavailable.",
                    samples.Length, ForecastSupportReason.OutsideObservedSupport);

            var trials = new List<(ScenarioHistorySample Sample, double BaselineError, double RidgeError, double SelectedError)>();
            foreach (ScenarioHistorySample target in samples)
            {
                // No overlapping interval outcome may enter this prediction's training set.
                ScenarioHistorySample[] prefix = samples.Where(x => x.EndUtc <= target.StartUtc && x.StartUtc < target.StartUtc).ToArray();
                if (prefix.Length < options.SelectionSamples) continue;
                double baseline = Baseline(prefix, Hours(target));
                AccountLocalRidge? fit = Fit(prefix);
                if (fit is null) continue;
                double ridge = Math.Clamp(fit.Predict(Features(target)), 0, 100);
                (ScenarioHistorySample Sample, double BaselineError, double RidgeError, double SelectedError)[] previous =
                    trials.Where(x => x.Sample.EndUtc <= target.StartUtc).ToArray();
                bool useRidge = previous.Length >= options.SelectionSamples &&
                                previous.Average(x => x.RidgeError) + .1 * options.ImprovementMultiplier < previous.Average(x => x.BaselineError) * (1 - .1 * options.ImprovementMultiplier);
                trials.Add(
                    (target, Math.Abs(target.QuotaDeltaPercent - baseline), Math.Abs(target.QuotaDeltaPercent - ridge),
                        Math.Abs(target.QuotaDeltaPercent - (useRidge ? ridge : baseline))));
            }
            bool selectedRidge = trials.Count >= options.SelectionSamples && trials.Average(x => x.RidgeError) + .1 * options.ImprovementMultiplier < trials.Average(x => x.BaselineError) * (1 - .1 * options.ImprovementMultiplier);
            double prediction = selectedRidge && Fit(samples) is { } model
                ? Math.Clamp(model.Predict([request.DurationHours, request.RootAgents, request.Subagents]), 0, 100)
                : Baseline(samples, request.DurationHours);
            double[] errors = ChronologicalEvidence.Blocks(
                    trials.Where(x => x.Sample.ResetUtc is not null &&
                                      Hours(x.Sample) >= request.DurationHours / 2 && Hours(x.Sample) <= request.DurationHours * 2),
                    x => x.Sample.StartUtc,
                    x => x.Sample.EndUtc,
                    x => x.Sample.ResetUtc!.Value, options.BlockHours)
                .Select(g => g.Max(x => x.SelectedError)).ToArray();
            double? radius = QuotaForecastCalibration.ErrorRadius(errors, options);
            return new ScenarioWindowEstimate(
                kind,
                true,
                samples.Length,
                prediction,
                radius is double r ? Math.Max(0, prediction - r) : null,
                radius is double r2 ? Math.Min(100, prediction + r2) : null,
                0,
                $"Conditional {(selectedRidge ? "workload ridge" : "elapsed-time baseline")}; {trials.Count} chronological test outcomes. " +
                (radius is null
                    ? $"Uncertainty is learning: fewer than {options.CalibrationGroups} comparable held-out chronological blocks. "
                    : "Empirical 80%-target band from earlier held-out chronological blocks; coverage can change with workload. ") +
                "Unchanged meters are precision-limited, not exact zero burn. Session counts are not measured concurrency; no confidence probability is assigned.")
            {
                Cohort = latest?.Cohort,
                SupportReasons = (radius is null ? ForecastSupportReason.InsufficientIntervalEvidence : ForecastSupportReason.None) |
                    (selectedRidge ? ForecastSupportReason.None : trials.Count >= options.SelectionSamples ? ForecastSupportReason.ChallengerNotBetter : ForecastSupportReason.InsufficientHistory),
            };
        }
    }

    private static double Hours(ScenarioHistorySample x)
    {
        return (x.EndUtc - x.StartUtc).TotalHours;
    }

    private static double[] Features(ScenarioHistorySample x)
    {
        return [Hours(x), x.RootAgents, x.Subagents];
    }

    private static AccountLocalRidge? Fit(ScenarioHistorySample[] samples)
    {
        return AccountLocalRidge.Fit(samples.Select(Features).ToArray(), samples.Select(x => x.QuotaDeltaPercent).ToArray(), 10);
    }

    private static double Baseline(ScenarioHistorySample[] samples, double hours)
    {
        return Math.Clamp(samples.Sum(x => x.QuotaDeltaPercent) / samples.Sum(Hours) * hours, 0, 100);
    }
}
