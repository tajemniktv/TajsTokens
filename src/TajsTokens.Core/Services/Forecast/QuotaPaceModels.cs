// Taj's Tokens | QuotaPaceModels.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>
///     Point estimates of observed meter movement, shared by production and walk-forward evaluation.
///     Zero is not a measurement of exact physical consumption: the production forecast explicitly
///     censors an all-flat epoch, and horizon bands include meter-resolution uncertainty when calibrated.
/// </summary>
public static class QuotaPaceModels
{
    public const string ProductionFallback = "time-ewma-2h";
    public const string CalculationVersion = "quota-pace-calculation/v1";
    public static readonly string[] Candidates =
    [
        "persistence", "legacy-ewma", "time-ewma-2h", "time-ewma-6h", "epoch", "recent-30m", "recent-2h", "recent-6h", "recent-24h",
        "damped-2h", "damped-6h",
    ];

    // Legacy remains an explicit research comparator, never a selectable production model.
    public static IReadOnlyList<string> ProductionCandidates { get; } =
        Array.AsReadOnly(Candidates.Where(x => x != "legacy-ewma").ToArray());

    public static bool IsProductionEligible(string model) => ProductionCandidates.Contains(model);

    public static double ProjectRemaining(IReadOnlyList<QuotaSnapshot> epoch, string model, double leadHours)
    {
        double rate = Estimate(epoch, model);
        return ProjectRemaining(epoch[^1], model, leadHours, rate);
    }

    internal static double ProjectRemaining(QuotaSnapshot anchor, string model, double leadHours, double rate)
    {
        double effectiveHours = model switch
        {
            "damped-2h" => 2 * (1 - Math.Exp(-leadHours / 2)),
            "damped-6h" => 6 * (1 - Math.Exp(-leadHours / 6)),
            _ => leadHours,
        };
        return Math.Clamp(anchor.RemainingPercent!.Value - rate * effectiveHours, 0, 100);
    }

    public static double Estimate(IReadOnlyList<QuotaSnapshot> epoch, string model)
        => Measure(epoch, model).Rate;

    private sealed record Measurement(double Rate, double Numerator, double Hours, int First, int Intervals);

    private static Measurement Measure(IReadOnlyList<QuotaSnapshot> epoch, string model)
    {
        if (epoch.Count < 2 || model == "persistence") return new(0, 0, 0, 0, 0);
        QuotaSnapshot latest = epoch[^1];
        if (model is "time-ewma-2h" or "time-ewma-6h")
        {
            // Integrate interval rates against elapsed-time weights. Flat polling does not apply
            // a fixed per-poll decay, and minute-scale rounded-meter jumps do not get a full vote.
            double tau = model == "time-ewma-2h" ? 2d : 6d;
            double weightedMovement = 0d;
            double exposure = 0d;
            int intervals = 0;
            for (int i = 1; i < epoch.Count; i++)
            {
                double hours = (epoch[i].CapturedAtUtc - epoch[i - 1].CapturedAtUtc).TotalHours;
                if (hours <= 0) continue;
                double age = (latest.CapturedAtUtc - epoch[i].CapturedAtUtc).TotalHours;
                double weight = Math.Exp(-age / tau) * tau * (1 - Math.Exp(-hours / tau));
                weightedMovement += Math.Max(0, epoch[i].UsedPercent!.Value - epoch[i - 1].UsedPercent!.Value) / hours * weight;
                exposure += weight;
                intervals++;
            }
            return new(exposure > 0 ? weightedMovement / exposure : 0, weightedMovement, exposure, 0, intervals);
        }
        if (model == "legacy-ewma")
        {
            double? rate = null;
            double alpha = latest.Kind == QuotaWindowKind.Weekly ? 0.25 : 0.45;
            for (int i = 1; i < epoch.Count; i++)
            {
                double hours = (epoch[i].CapturedAtUtc - epoch[i - 1].CapturedAtUtc).TotalHours;
                if (hours <= 0) continue;
                double next = Math.Max(0, (epoch[i].UsedPercent!.Value - epoch[i - 1].UsedPercent!.Value) / hours);
                rate = rate is null ? next : alpha * next + (1 - alpha) * rate;
            }
            return new(rate ?? 0, 0, 0, 0, epoch.Count - 1); // Legacy has no production calculation receipt.
        }
        double lookback = model switch
        {
            "recent-30m" => 0.5,
            "recent-2h" or "damped-2h" or "damped-6h" => 2,
            "recent-6h" => 6,
            "recent-24h" => 24,
            "epoch" => double.PositiveInfinity,
            _ => throw new ArgumentException("Unknown pace model", nameof(model)),
        };
        QuotaSnapshot first = epoch[0];
        int firstIndex = 0;
        // Include the observation bracketing the lookback, rather than interpolating a
        // fictitious meter observation or dividing a full delta by a truncated duration.
        for (int i = 0; i < epoch.Count - 1; i++)
        {
            QuotaSnapshot point = epoch[i];
            if ((latest.CapturedAtUtc - point.CapturedAtUtc).TotalHours >= lookback) { first = point; firstIndex = i; }
            else break;
        }
        double elapsed = (latest.CapturedAtUtc - first.CapturedAtUtc).TotalHours;
        double movement = Math.Max(0, latest.UsedPercent!.Value - first.UsedPercent!.Value);
        return new(elapsed > 0 ? movement / elapsed : 0, movement, elapsed, firstIndex, epoch.Count - firstIndex - 1);
    }

    public static QuotaPaceReceipt Receipt(IReadOnlyList<QuotaSnapshot> inputs, QuotaSnapshot anchor,
        DateTimeOffset generatedAtUtc, string model, string reason, bool supported)
    {
        if (!IsProductionEligible(model)) throw new ArgumentException("Ineligible production pace model.", nameof(model));
        var measurement = supported ? Measure(inputs, model) : new Measurement(0, 0, 0, 0, 0);
        DateTimeOffset from = inputs.Count > 0 ? inputs[measurement.First].CapturedAtUtc : anchor.CapturedAtUtc;
        DateTimeOffset to = inputs.Count > 0 ? inputs[^1].CapturedAtUtc : anchor.CapturedAtUtc;
        double? clock = anchor.ResetsAtUtc is { } reset && reset > generatedAtUtc ? (reset - generatedAtUtc).TotalHours : null;
        double? remaining = anchor.UsedPercent is { } used && double.IsFinite(used) && used is >= 0 and <= 100
            ? anchor.RemainingPercent : null;
        int unknownCollection = 0, laterCollected = 0;
        for (int i = measurement.First; i < inputs.Count; i++)
        {
            if (inputs[i].CollectedAtUtc is null) unknownCollection++;
            if (inputs[i].CollectedAtUtc > (inputs[i].ReplayMeasuredAtUtc ?? inputs[i].CapturedAtUtc)) laterCollected++;
        }
        return new(CalculationVersion, model, ProductionFallback, reason, QuotaHistoryPolicy.Cohort(anchor), anchor.ObservationId,
            anchor.CapturedAtUtc, anchor.HasSourceTimestamp ? anchor.ReplayMeasuredAtUtc ?? anchor.CapturedAtUtc : null,
            anchor.CollectedAtUtc, generatedAtUtc, from, to, inputs.Count - measurement.First, measurement.Intervals,
            unknownCollection, laterCollected, supported,
            supported && measurement.Hours > 0 ? measurement.Numerator : null,
            supported && measurement.Hours > 0 ? measurement.Hours : null, supported ? measurement.Rate : null,
            remaining, clock, clock is { } hours ? remaining / hours : null,
            "Installation-observed compatible meter lane; not local-work attribution or proof of account completeness. " +
            "Observed integer steps have rounding/lag uncertainty; no exact consumption or calibrated probability is inferred. " +
            "Fresh later flat readings add elapsed exposure; same-clock cached copies add no interval. " +
            "Collection-qualified replay can move the effective clock; the original measurement time is retained separately. " +
            (model == "persistence" ? "Persistence predicts no displayed movement, not measured zero burn." :
                "Rate spreads displayed movement over observation intervals; unknown backend delay remains unresolved."));
    }
}
