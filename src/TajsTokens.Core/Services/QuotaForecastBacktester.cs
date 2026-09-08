using TajsTokens.Core.Models;
using TajsTokens.Core.Enums;

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
    int CalibrationEpochs,
    bool? ObservedExhaustion,
    bool PredictedExhaustion,
    double? ExhaustionEtaBracketErrorHours,
    DateTimeOffset? ExhaustionLabelAvailableAtUtc = null);

/// <summary>
/// Source-homogeneous chronological replay. Calibration sees only outcomes from earlier
/// generations already observed at the origin. No target is interpolated across a reset.
/// </summary>
public static class QuotaForecastBacktester
{
    public const int MinimumCalibrationEpochs = 8;
    public const double NominalCoverage = 0.8;

    // Keep the incumbent until comparable, matured account-local target outcomes support a switch.
    public static string DefaultModel(QuotaSnapshot anchor) => "legacy-ewma";

    public static string SelectModel(IReadOnlyDictionary<string, IReadOnlyList<QuotaForecastTrial>> candidates,
        DateTimeOffset origin, DateTimeOffset reset, double lead, string fallback)
    {
        var scores = candidates.Select(pair => (Model: pair.Key,
            Errors: CalibrationErrors(pair.Value, origin, reset, lead)))
            .Where(x => x.Errors.Count >= MinimumCalibrationEpochs)
            .Select(x => (x.Model, Error: x.Errors.Average())).ToArray();
        if (scores.Length == 0) return fallback;
        var best = scores.Min(x => x.Error);
        // A 10% practical-equivalence band avoids changing models for tiny noisy wins.
        var preferred = new[] { fallback, "persistence", "epoch", "recent-2h", "recent-30m", "recent-6h", "recent-24h", "damped-2h", "damped-6h", "legacy-ewma" };
        return preferred.First(model => scores.Any(x => x.Model == model && x.Error <= best * 1.1 + 0.1));
    }

    public static IReadOnlyList<QuotaForecastTrial> ReplayAdaptive(IReadOnlyList<QuotaSnapshot> stream,
        double? horizonHours, CancellationToken cancellationToken = default)
    {
        if (stream.Count == 0) return [];
        var candidates = QuotaPaceModels.Candidates.ToDictionary(model => model,
            model => Replay(stream, model, horizonHours, cancellationToken));
        var fallback = DefaultModel(stream[^1]);
        return ReplayAdaptive(candidates, fallback);
    }

    public static IReadOnlyList<QuotaForecastTrial> ReplayAdaptive(
        IReadOnlyDictionary<string, IReadOnlyList<QuotaForecastTrial>> candidates, string fallback)
    {
        var selected = new List<QuotaForecastTrial>();
        var lookup = candidates.ToDictionary(x => x.Key, x => x.Value.ToDictionary(t => t.OriginUtc));
        foreach (var trial in candidates[fallback])
        {
            var model = SelectModel(candidates, trial.OriginUtc, trial.ResetUtc, trial.LeadHours, fallback);
            var point = lookup[model][trial.OriginUtc];
            var errors = CalibrationErrors(selected, trial.OriginUtc, trial.ResetUtc, trial.LeadHours);
            var radius = ErrorRadius(errors);
            selected.Add(point with
            {
                Model = "adaptive/" + model,
                LowerRemaining = radius is double r ? Math.Max(0, point.PredictedRemaining - r) : null,
                UpperRemaining = radius is double r2 ? Math.Min(100, point.PredictedRemaining + r2) : null,
                CalibrationEpochs = errors.Count
            });
        }
        return selected;
    }

    public static IReadOnlyList<IReadOnlyList<QuotaSnapshot>> SplitEpochs(IEnumerable<QuotaSnapshot> snapshots)
    {
        var result = new List<IReadOnlyList<QuotaSnapshot>>();
        List<QuotaSnapshot>? current = null;
        QuotaSnapshot? previous = null;
        foreach (var row in snapshots.OrderBy(x => x.CapturedAtUtc))
        {
            if (previous is not null && (row.Provider != previous.Provider || row.Profile != previous.Profile ||
                row.Kind != previous.Kind || row.Source != previous.Source))
                throw new ArgumentException("Replay requires one provider/profile/kind/source stream.", nameof(snapshots));
            var valid = row.UsedPercent is double used && double.IsFinite(used) && used is >= 0 and <= 100 &&
                        row.ResetsAtUtc is not null && row.CapturedAtUtc <= row.ResetsAtUtc;
            if (!valid)
            {
                current = null;
                previous = row;
                continue;
            }
            if (current is null || previous is null || row.ResetsAtUtc != previous.ResetsAtUtc ||
                row.WindowMinutes != previous.WindowMinutes || row.UsedPercent < previous.UsedPercent)
            {
                current = [];
                result.Add(current);
            }
            // Ambiguous equal-time readings are not slope intervals. A conflicting one starts
            // a new segment, preventing any pre-conflict history from contributing afterwards.
            if (current.Count > 0 && row.CapturedAtUtc == current[^1].CapturedAtUtc)
            {
                if (row.UsedPercent == current[^1].UsedPercent) continue;
                current = [];
                result.Add(current);
            }
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
        foreach (var epoch in SplitEpochs(stream).Where(x => x.Count >= 3))
        {
            DateTimeOffset? lastOrigin = null;
            for (var i = 1; i < epoch.Count - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var anchor = epoch[i];
                if (anchor.UsedPercent >= 100) continue; // Already exhausted is a fact, not a successful forecast.
                if (lastOrigin is not null && anchor.CapturedAtUtc - lastOrigin < TimeSpan.FromMinutes(30)) continue;
                if (anchor.CapturedAtUtc - epoch[0].CapturedAtUtc < TimeSpan.FromMinutes(15)) continue;
                var targetTime = horizonHours is double horizon ? anchor.CapturedAtUtc.AddHours(horizon) : anchor.ResetsAtUtc!.Value;
                if (targetTime > anchor.ResetsAtUtc) continue;
                var target = horizonHours is null ? epoch[^1] : epoch.Skip(i + 1).FirstOrDefault(x => x.CapturedAtUtc >= targetTime);
                if (target is null || Math.Abs((target.CapturedAtUtc - targetTime).TotalMinutes) > 5 || target.CapturedAtUtc <= anchor.CapturedAtUtc) continue;
                lastOrigin = anchor.CapturedAtUtc;
                var rate = QuotaPaceModels.Estimate(epoch.Take(i + 1).ToArray(), model);
                var lead = (target.CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
                var prediction = QuotaPaceModels.ProjectRemaining(epoch.Take(i + 1).ToArray(), model, lead);
                var calibration = CalibrationErrors(trials, anchor.CapturedAtUtc, anchor.ResetsAtUtc!.Value, lead);
                var radius = ErrorRadius(calibration);
                var exhaustedIndex = -1;
                for (var j = i + 1; j < epoch.Count; j++)
                    if (epoch[j].UsedPercent >= 100) { exhaustedIndex = j; break; }
                // A last sample shortly before reset is NOT proof of survival. Negative labels
                // require a reading at reset; positive labels require observed saturation.
                bool? exhausted = exhaustedIndex >= 0 ? true : epoch[^1].CapturedAtUtc == anchor.ResetsAtUtc ? false : null;
                var hoursToReset = (anchor.ResetsAtUtc!.Value - anchor.CapturedAtUtc).TotalHours;
                var predictsExhaustion = QuotaPaceModels.ProjectRemaining(epoch.Take(i + 1).ToArray(), model, hoursToReset) <= 0;
                double? etaError = null;
                if (exhaustedIndex >= 0 && rate > 0 && predictsExhaustion && anchor.RemainingPercent > 0)
                {
                    var eta = anchor.RemainingPercent!.Value / rate;
                    var decay = model switch { "damped-2h" => 2d, "damped-6h" => 6d, _ => 0d };
                    if (decay > 0) eta = -decay * Math.Log(1 - eta / decay);
                    var bracketStart = (epoch[exhaustedIndex - 1].CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
                    var bracketEnd = (epoch[exhaustedIndex].CapturedAtUtc - anchor.CapturedAtUtc).TotalHours;
                    etaError = Math.Max(0, Math.Max(bracketStart - eta, eta - bracketEnd));
                }
                trials.Add(new QuotaForecastTrial(model, horizonHours is null ? "near-reset-proxy" : FormattableString.Invariant($"{horizonHours:g}h"), anchor.CapturedAtUtc,
                    target.CapturedAtUtc, anchor.ResetsAtUtc.Value, lead, prediction, target.RemainingPercent!.Value,
                    radius is double r ? Math.Max(0, prediction - r) : null,
                    radius is double r2 ? Math.Min(100, prediction + r2) : null,
                    calibration.Count, exhausted, predictsExhaustion, etaError,
                    exhaustedIndex >= 0 ? epoch[exhaustedIndex].CapturedAtUtc : exhausted is false ? anchor.ResetsAtUtc : null));
            }
        }
        return trials;
    }

    public static IReadOnlyList<double> CalibrationErrors(IEnumerable<QuotaForecastTrial> trials,
        DateTimeOffset origin, DateTimeOffset reset, double leadHours) => trials
        .Where(x => x.OutcomeUtc < origin && x.ResetUtc < origin && x.ResetUtc != reset &&
                    x.LeadHours >= leadHours / 2 && x.LeadHours <= leadHours * 2)
        // One score per reset generation, not hundreds of correlated polling samples.
        .GroupBy(x => x.ResetUtc)
        .Select(g => g.OrderBy(x => Math.Abs(x.LeadHours - leadHours)).ThenByDescending(x => x.OriginUtc).First())
        .OrderByDescending(x => x.ResetUtc).Take(40)
        .Select(x => Math.Abs(x.PredictedRemaining - x.ObservedRemaining)).ToArray();

    public static double? ErrorRadius(IReadOnlyList<double> errors)
    {
        if (errors.Count < MinimumCalibrationEpochs) return null;
        var sorted = errors.Order().ToArray();
        var rank = (int)Math.Ceiling((sorted.Length + 1) * NominalCoverage);
        // One point is a conservative meter-resolution policy, not a provider precision guarantee.
        return Math.Max(1, sorted[Math.Min(sorted.Length, rank) - 1]);
    }
}
