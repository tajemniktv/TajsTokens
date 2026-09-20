// Taj's Tokens | QuotaForecastCalibration.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public sealed record QuotaForecastTrial(
    string Model,
    string Target,
    DateTimeOffset OriginUtc,
    DateTimeOffset OutcomeUtc,
    DateTimeOffset ResetUtc,
    double LeadHours,
    double PredictedRemaining,
    double ObservedRemaining,
    double? LowerRemaining,
    double? UpperRemaining,
    int CalibrationGroups,
    bool? ObservedExhaustion,
    bool PredictedExhaustion,
    double? ExhaustionEtaBracketErrorHours,
    DateTimeOffset? ExhaustionLabelAvailableAtUtc = null,
    string? AccountKey = null);

/// <summary>
///     Source-homogeneous chronological replay. Calibration sees only outcomes from earlier
///     blocks already observed at the origin (completed cycles for reset outlooks).
///     No target is interpolated across a reset.
/// </summary>
public static class QuotaForecastCalibration
{
    public const int MinimumCalibrationGroups = 8;

    public const double NominalCoverage = 0.8;

    // Observed app-server reset timestamps alternate by one second. This is a derived
    // segmentation tolerance, never a normalization of provider facts or anchor identity.
    private static readonly TimeSpan ResetJitterTolerance = QuotaResetGenerationPolicy.Tolerance;

    // Keep the incumbent until comparable, matured account-local target outcomes support a switch.
    public static string DefaultModel(QuotaSnapshot anchor)
    {
        return "legacy-ewma";
    }

    public static string SelectModel(
        IReadOnlyDictionary<string, IReadOnlyList<QuotaForecastTrial>> candidates,
        DateTimeOffset origin,
        DateTimeOffset reset,
        double lead,
        string fallback)
    {
        (string Model, double Error)[] scores = candidates.Select(pair => (Model: pair.Key,
                Errors: CalibrationErrors(pair.Value, origin, reset, lead)))
            .Where(x => x.Errors.Count >= MinimumCalibrationGroups)
            .Select(x => (x.Model, Error: x.Errors.Average())).ToArray();
        if (scores.Length == 0) return fallback;
        double best = scores.Min(x => x.Error);
        // A 10% practical-equivalence band avoids changing models for tiny noisy wins.
        string[] preferred = new[]
        {
            fallback,
            "persistence",
            "time-ewma-2h",
            "time-ewma-6h",
            "epoch",
            "recent-2h",
            "recent-30m",
            "recent-6h",
            "recent-24h",
            "damped-2h",
            "damped-6h",
            "legacy-ewma",
        };
        return preferred.First(model => scores.Any(x => x.Model == model && x.Error <= best * 1.1 + 0.1));
    }

    public static IReadOnlyList<QuotaForecastTrial> ReplayAdaptive(
        IReadOnlyList<QuotaSnapshot> stream,
        double? horizonHours,
        CancellationToken cancellationToken = default)
    {
        if (stream.Count == 0) return [];
        Dictionary<string, IReadOnlyList<QuotaForecastTrial>> candidates = QuotaPaceModels.Candidates.ToDictionary(
            model => model,
            model => Replay(stream, model, horizonHours, cancellationToken));
        string fallback = DefaultModel(stream[^1]);
        return ReplayAdaptive(candidates, fallback);
    }

    public static IReadOnlyList<QuotaForecastTrial> ReplayAdaptive(
        IReadOnlyDictionary<string, IReadOnlyList<QuotaForecastTrial>> candidates,
        string fallback)
    {
        var selected = new List<QuotaForecastTrial>();
        Dictionary<string, Dictionary<DateTimeOffset, QuotaForecastTrial>> lookup = candidates.ToDictionary(
            x => x.Key,
            x => x.Value.ToDictionary(t => t.OriginUtc));
        foreach (QuotaForecastTrial trial in candidates[fallback])
        {
            string model = SelectModel(candidates, trial.OriginUtc, trial.ResetUtc, trial.LeadHours, fallback);
            QuotaForecastTrial point = lookup[model][trial.OriginUtc];
            IReadOnlyList<double> errors = CalibrationErrors(selected, trial.OriginUtc, trial.ResetUtc, trial.LeadHours);
            double? radius = ErrorRadius(errors);
            selected.Add(
                point with
                {
                    Model = "adaptive/" + model,
                    LowerRemaining = radius is double r ? Math.Max(0, point.PredictedRemaining - r) : null,
                    UpperRemaining = radius is double r2 ? Math.Min(100, point.PredictedRemaining + r2) : null,
                    CalibrationGroups = errors.Count,
                });
        }
        return selected;
    }

    public static IReadOnlyList<IReadOnlyList<QuotaSnapshot>> SplitEpochs(IEnumerable<QuotaSnapshot> snapshots)
    {
        var result = new List<IReadOnlyList<QuotaSnapshot>>();
        List<QuotaSnapshot>? current = null;
        QuotaSnapshot? previous = null;
        DateTimeOffset minimumReset = default, maximumReset = default;
        foreach (QuotaSnapshot row in snapshots.OrderBy(x => x.CapturedAtUtc))
        {
            if (previous is not null && (row.Provider != previous.Provider || row.Profile != previous.Profile ||
                                         row.Kind != previous.Kind || row.Source != previous.Source ||
                                         row.AccountKey != previous.AccountKey))
                throw new ArgumentException("Replay requires one provider/profile/kind/source/account stream.", nameof(snapshots));
            bool valid = row.UsedPercent is double used && double.IsFinite(used) && used is >= 0 and <= 100 &&
                         row.ResetsAtUtc is not null && row.CapturedAtUtc <= row.ResetsAtUtc;
            if (!valid)
            {
                current = null;
                previous = row;
                continue;
            }
            DateTimeOffset reset = row.ResetsAtUtc!.Value;
            if (current is null || previous is null ||
                !QuotaResetGenerationPolicy.FitsRange(reset, minimumReset, maximumReset) ||
                row.CapturedAtUtc > minimumReset ||
                QuotaHistoryPolicy.Cohort(row) != QuotaHistoryPolicy.Cohort(previous) ||
                row.UsedPercent < previous.UsedPercent)
            {
                current = [];
                result.Add(current);
                minimumReset = maximumReset = reset;
            }
            // Ambiguous equal-time readings are not slope intervals. A conflicting one starts
            // a new segment, preventing any pre-conflict history from contributing afterwards.
            if (current.Count > 0 && row.CapturedAtUtc == current[^1].CapturedAtUtc)
            {
                if (row.UsedPercent == current[^1].UsedPercent) continue;
                current = [];
                result.Add(current);
                minimumReset = maximumReset = reset;
            }
            minimumReset = reset < minimumReset ? reset : minimumReset;
            maximumReset = reset > maximumReset ? reset : maximumReset;
            current.Add(row);
            previous = row;
        }
        return result;
    }

    public static IReadOnlyList<QuotaForecastTrial> Replay(
        IReadOnlyList<QuotaSnapshot> stream,
        string model,
        double? horizonHours,
        CancellationToken cancellationToken = default)
    {
        if (horizonHours is <= 0 || horizonHours is double h && !double.IsFinite(h))
            throw new ArgumentOutOfRangeException(nameof(horizonHours));
        var trials = new List<QuotaForecastTrial>();
        foreach (IReadOnlyList<QuotaSnapshot> epoch in SplitEpochs(stream).Where(x => x.Count >= 3))
        {
            DateTimeOffset? lastOrigin = null;
            for (int i = 1; i < epoch.Count - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QuotaSnapshot anchor = epoch[i];
                if (anchor.UsedPercent >= 100) continue; // Already exhausted is a fact, not a successful forecast.
                // Short-horizon comparisons need short-horizon origins. Retain the existing
                // half-hour sampling for longer/reset outlooks; never pool these trial streams.
                if (lastOrigin is not null && anchor.CapturedAtUtc - lastOrigin <
                    TimeSpan.FromHours(Math.Min(.5, horizonHours ?? .5))) continue;
                if (anchor.CapturedAtUtc - epoch[0].CapturedAtUtc < TimeSpan.FromMinutes(15)) continue;
                DateTimeOffset targetTime =
                    horizonHours is double horizon ? anchor.CapturedAtUtc.AddHours(horizon) : anchor.ResetsAtUtc!.Value;
                if (targetTime > anchor.ResetsAtUtc) continue;
                QuotaSnapshot? target =
                    horizonHours is null ? epoch[^1] : epoch.Skip(i + 1).FirstOrDefault(x => x.CapturedAtUtc >= targetTime);
                if (target is null || Math.Abs((target.CapturedAtUtc - targetTime).TotalMinutes) > 5 ||
                    target.CapturedAtUtc <= anchor.CapturedAtUtc) continue;
                lastOrigin = anchor.CapturedAtUtc;
                double rate = QuotaPaceModels.Estimate(epoch.Take(i + 1).ToArray(), model);
                double lead = (target.CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
                double prediction = QuotaPaceModels.ProjectRemaining(epoch.Take(i + 1).ToArray(), model, lead);
                IReadOnlyList<double> calibration = CalibrationErrors(trials, anchor.CapturedAtUtc, anchor.ResetsAtUtc!.Value, lead);
                double? radius = ErrorRadius(calibration);
                int exhaustedIndex = -1;
                for (int j = i + 1; j < epoch.Count; j++)
                    if (epoch[j].UsedPercent >= 100)
                    {
                        exhaustedIndex = j;
                        break;
                    }
                // A last sample shortly before reset is NOT proof of survival. Negative labels
                // require a reading at reset; positive labels require observed saturation.
                bool? exhausted = exhaustedIndex >= 0 ? true : epoch[^1].CapturedAtUtc == anchor.ResetsAtUtc ? false : null;
                double hoursToReset = (anchor.ResetsAtUtc!.Value - anchor.CapturedAtUtc).TotalHours;
                bool predictsExhaustion = QuotaPaceModels.ProjectRemaining(epoch.Take(i + 1).ToArray(), model, hoursToReset) <= 0;
                double? etaError = null;
                if (exhaustedIndex >= 0 && rate > 0 && predictsExhaustion && anchor.RemainingPercent > 0)
                {
                    double eta = anchor.RemainingPercent!.Value / rate;
                    double decay = model switch { "damped-2h" => 2d, "damped-6h" => 6d, _ => 0d };
                    if (decay > 0) eta = -decay * Math.Log(1 - eta / decay);
                    double bracketStart = (epoch[exhaustedIndex - 1].CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
                    double bracketEnd = (epoch[exhaustedIndex].CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
                    etaError = Math.Max(0, Math.Max(bracketStart - eta, eta - bracketEnd));
                }
                trials.Add(
                    new QuotaForecastTrial(
                        model,
                        horizonHours is null ? "near-reset-proxy" : FormattableString.Invariant($"{horizonHours:g}h"),
                        anchor.CapturedAtUtc,
                        target.CapturedAtUtc,
                        anchor.ResetsAtUtc.Value,
                        lead,
                        prediction,
                        target.RemainingPercent!.Value,
                        radius is double r ? Math.Max(0, prediction - r) : null,
                        radius is double r2 ? Math.Min(100, prediction + r2) : null,
                        calibration.Count,
                        exhausted,
                        predictsExhaustion,
                        etaError,
                        exhaustedIndex >= 0 ? epoch[exhaustedIndex].CapturedAtUtc : exhausted is false ? anchor.ResetsAtUtc : null,
                        anchor.AccountKey));
            }
        }
        return trials;
    }

    public static IReadOnlyList<IReadOnlyList<QuotaForecastTrial>> ResetGenerations(IEnumerable<QuotaForecastTrial> trials)
    {
        return QuotaResetGenerationPolicy.Group(trials, x => x.ResetUtc);
    }

    public static IReadOnlyList<double> CalibrationErrors(
        IEnumerable<QuotaForecastTrial> trials,
        DateTimeOffset origin,
        DateTimeOffset reset,
        double leadHours)
    {
        QuotaForecastTrial[] available = trials.Where(x => x.OutcomeUtc < origin &&
                                                           x.LeadHours >= leadHours / 2 && x.LeadHours <= leadHours * 2).ToArray();
        // End-of-window validation genuinely needs completed cycles. Fixed horizons do not.
        if (available.Any(x => x.Target == "near-reset-proxy"))
            return ResetGenerations(
                    available.Where(x => x.ResetUtc < origin - ResetJitterTolerance &&
                                         !QuotaResetGenerationPolicy.SameTimestamp(x.ResetUtc, reset)))
                .Select(g => g.OrderBy(x => Math.Abs(x.LeadHours - leadHours)).ThenByDescending(x => x.OriginUtc).First())
                .OrderByDescending(x => x.ResetUtc).Take(40)
                .Select(x => Math.Abs(x.PredictedRemaining - x.ObservedRemaining)).ToArray();
        return ChronologicalEvidence.Blocks(available, x => x.OriginUtc, x => x.OutcomeUtc, x => x.ResetUtc)
            .TakeLast(40).Select(g => g.Max(x => Math.Abs(x.PredictedRemaining - x.ObservedRemaining))).ToArray();
    }

    public static double? ErrorRadius(IReadOnlyList<double> errors)
    {
        if (errors.Count < MinimumCalibrationGroups) return null;
        double[] sorted = errors.Order().ToArray();
        int rank = (int)Math.Ceiling((sorted.Length + 1) * NominalCoverage);
        // One point is a conservative meter-resolution policy, not a provider precision guarantee.
        return Math.Max(1, sorted[Math.Min(sorted.Length, rank) - 1]);
    }
}