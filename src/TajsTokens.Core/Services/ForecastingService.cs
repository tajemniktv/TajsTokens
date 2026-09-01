using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed class ForecastingService : IForecastingService
{
    private const double NearSustainablePressure = 0.85;

    public Forecast BuildForecast(IReadOnlyList<QuotaSnapshot> snapshots, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("At least one quota snapshot is required.", nameof(snapshots));
        }

        // Historical/backtest forecasts must never see observations from the future.
        var eligible = snapshots
            .Where(x => x.CapturedAtUtc <= nowUtc)
            .OrderBy(x => x.CapturedAtUtc)
            .ToArray();

        if (eligible.Length == 0)
        {
            throw new ArgumentException("At least one snapshot must be captured at or before the forecast time.", nameof(snapshots));
        }

        var latest = eligible[^1];
        if (eligible.Any(x => x.Kind != latest.Kind ||
                              !string.Equals(x.Provider, latest.Provider, StringComparison.Ordinal) ||
                              !string.Equals(x.Profile, latest.Profile, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Forecast snapshots must belong to one quota window/provider/profile.", nameof(snapshots));
        }

        if (latest.UsedPercent is null || latest.RemainingPercent is null)
        {
            return UnknownForecast(latest, nowUtc);
        }

        // The backend's reset identity defines the forecasting epoch. Never carry a terminal slope
        // across a reset/re-anchor, even when percentages happen to keep increasing numerically.
        var currentWindow = eligible
            .Where(x => x.WindowMinutes == latest.WindowMinutes && x.ResetsAtUtc == latest.ResetsAtUtc)
            .ToArray();

        var resetAt = latest.ResetsAtUtc;
        if (resetAt is not null && resetAt <= nowUtc)
        {
            // The observed epoch has already ended. A new provider sample is required before a
            // current-window forecast is meaningful.
            return UnknownForecast(latest, nowUtc);
        }

        var observedRates = new List<double>(Math.Max(0, currentWindow.Length - 1));
        var observedDeltas = new List<double>(Math.Max(0, currentWindow.Length - 1));
        for (var index = 1; index < currentWindow.Length; index++)
        {
            var previous = currentWindow[index - 1];
            var current = currentWindow[index];
            if (previous.UsedPercent is null || current.UsedPercent is null)
            {
                continue;
            }

            var hours = (current.CapturedAtUtc - previous.CapturedAtUtc).TotalHours;
            if (hours <= 0)
            {
                continue;
            }

            var delta = current.UsedPercent.Value - previous.UsedPercent.Value;
            if (delta < 0)
            {
                // Defensive fallback for a provider that lags reset metadata.
                continue;
            }

            observedDeltas.Add(delta);
            observedRates.Add(delta / hours);
        }

        var projectedRemainingNow = latest.RemainingPercent.Value;
        double? sustainable = null;
        if (resetAt is not null)
        {
            var hoursToReset = (resetAt.Value - nowUtc).TotalHours;
            if (hoursToReset > 0)
            {
                sustainable = projectedRemainingNow / hoursToReset;
            }
        }

        if (observedRates.Count == 0)
        {
            return new Forecast(
                latest.Kind,
                nowUtc,
                null,
                null,
                null,
                sustainable,
                0.15,
                ForecastState.Learning,
                null,
                resetAt is null ? null : projectedRemainingNow,
                null,
                false);
        }

        // Subscription meters are commonly quantized to whole-ish percentage points. A series of
        // unchanged samples means "no movement visible at this precision", not scientifically proven
        // zero burn. Preserve that uncertainty instead of returning a confident 0.0 pp/h ETA.
        var quantizedFlat = observedDeltas.All(delta => Math.Abs(delta) < 0.000_001);
        if (quantizedFlat)
        {
            return new Forecast(
                latest.Kind,
                nowUtc,
                null,
                null,
                null,
                sustainable,
                ComputeConfidence(observedRates, latest, nowUtc, quantized: true),
                ForecastState.IdleWithinMeterPrecision,
                null,
                resetAt is null ? null : projectedRemainingNow,
                "flat within meter precision",
                true);
        }

        var positiveRates = observedRates.Where(rate => rate > 0).ToArray();
        if (positiveRates.Length == 0)
        {
            return UnknownForecast(latest, nowUtc) with
            {
                SustainablePercentPerHour = sustainable,
                State = ForecastState.IdleWithinMeterPrecision,
                ProjectedRemainingAtResetPercent = resetAt is null ? null : projectedRemainingNow,
                Trend = "flat within meter precision",
                IsQuantizedFlat = true
            };
        }

        var alpha = latest.Kind == QuotaWindowKind.Weekly ? 0.25 : 0.45;
        var burnRate = ComputeEwma(positiveRates, alpha);
        var elapsedSinceLatestHours = Math.Max(0, (nowUtc - latest.CapturedAtUtc).TotalHours);
        projectedRemainingNow = Math.Max(0, latest.RemainingPercent.Value - (burnRate * elapsedSinceLatestHours));

        DateTimeOffset? exhaustionBeforeReset = null;
        bool? survives = null;
        double? projectedRemainingAtReset = null;
        double? burnPressure = null;

        if (resetAt is not null && resetAt > nowUtc)
        {
            var hoursToReset = (resetAt.Value - nowUtc).TotalHours;
            sustainable = hoursToReset > 0 ? projectedRemainingNow / hoursToReset : null;
            burnPressure = sustainable is > 0 ? burnRate / sustainable.Value : null;
            projectedRemainingAtReset = Math.Max(0, projectedRemainingNow - (burnRate * hoursToReset));

            var rawExhaustion = burnRate > 0
                ? nowUtc.AddHours(projectedRemainingNow / burnRate)
                : (DateTimeOffset?)null;

            survives = rawExhaustion is null || rawExhaustion >= resetAt.Value;
            if (survives == false)
            {
                // A current-window exhaustion ETA is useful only inside this current epoch. If the
                // arithmetic lands after reset, suppress it and report margin-at-reset instead.
                exhaustionBeforeReset = rawExhaustion;
            }
        }

        var state = survives switch
        {
            false => ForecastState.ExhaustionLikelyBeforeReset,
            true when burnPressure is >= NearSustainablePressure => ForecastState.NearSustainablePace,
            true => ForecastState.SafeUntilReset,
            _ => ForecastState.Learning
        };

        return new Forecast(
            latest.Kind,
            nowUtc,
            burnRate,
            exhaustionBeforeReset,
            survives,
            sustainable,
            ComputeConfidence(positiveRates, latest, nowUtc, quantized: false),
            state,
            burnPressure,
            projectedRemainingAtReset,
            ComputeTrend(positiveRates),
            false);
    }

    private static Forecast UnknownForecast(QuotaSnapshot latest, DateTimeOffset nowUtc) =>
        new(latest.Kind, nowUtc, null, null, null, null, 0.15, ForecastState.Learning);

    private static double ComputeEwma(IReadOnlyList<double> values, double alpha)
    {
        var ewma = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            ewma = (alpha * values[i]) + ((1 - alpha) * ewma);
        }

        return ewma;
    }

    private static double ComputeConfidence(
        IReadOnlyList<double> rates,
        QuotaSnapshot latest,
        DateTimeOffset nowUtc,
        bool quantized)
    {
        var sampleConfidence = Math.Clamp(rates.Count / 12.0, 0.15, 0.85);
        var freshnessHours = Math.Max(0, (nowUtc - latest.CapturedAtUtc).TotalHours);
        var freshnessFactor = Math.Clamp(1.0 - (freshnessHours / 6.0), 0.2, 1.0);

        if (rates.Count <= 1)
        {
            return sampleConfidence * freshnessFactor * (quantized ? 0.45 : 0.7);
        }

        var mean = rates.Average();
        var variance = rates.Sum(rate => Math.Pow(rate - mean, 2)) / rates.Count;
        var deviation = Math.Sqrt(variance);
        var variabilityFactor = mean <= 0
            ? 0.5
            : Math.Clamp(1.0 - (deviation / Math.Max(mean, 0.0001)), 0.35, 1.0);

        var quantizationFactor = quantized ? 0.45 : 1.0;
        return Math.Clamp(sampleConfidence * freshnessFactor * variabilityFactor * quantizationFactor, 0.05, 0.95);
    }

    private static string ComputeTrend(IReadOnlyList<double> rates)
    {
        if (rates.Count < 3)
        {
            return "stable/insufficient trend history";
        }

        var split = Math.Max(1, rates.Count / 2);
        var earlier = rates.Take(split).Average();
        var recent = rates.Skip(split).DefaultIfEmpty(rates[^1]).Average();

        if (earlier <= 0)
        {
            return recent > 0 ? "accelerating" : "stable";
        }

        var ratio = recent / earlier;
        return ratio switch
        {
            >= 1.25 => "accelerating",
            <= 0.75 => "slowing",
            _ => "stable"
        };
    }
}
