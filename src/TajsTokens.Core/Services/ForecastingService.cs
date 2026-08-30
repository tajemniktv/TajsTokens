using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed class ForecastingService : IForecastingService
{
    public Forecast BuildForecast(IReadOnlyList<QuotaSnapshot> snapshots, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("At least one quota snapshot is required.", nameof(snapshots));
        }

        var ordered = snapshots.OrderBy(x => x.CapturedAtUtc).ToArray();
        var latest = ordered[^1];

        if (ordered.Any(x => x.Kind != latest.Kind ||
                             !string.Equals(x.Provider, latest.Provider, StringComparison.Ordinal) ||
                             !string.Equals(x.Profile, latest.Profile, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Forecast snapshots must belong to one quota window/provider/profile.", nameof(snapshots));
        }

        if (latest.UsedPercent is null || latest.RemainingPercent is null)
        {
            return UnknownForecast(latest, nowUtc);
        }

        var observedRates = new List<double>(Math.Max(0, ordered.Length - 1));
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
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
                // A reset/window rollover occurred. Do not smear it into the burn-rate estimate.
                continue;
            }

            observedRates.Add(delta / hours);
        }

        if (observedRates.Count == 0)
        {
            return UnknownForecast(latest, nowUtc);
        }

        var burnRate = ComputeEwma(observedRates, 0.45);
        var elapsedSinceLatestHours = Math.Max(0, (nowUtc - latest.CapturedAtUtc).TotalHours);
        var projectedRemaining = Math.Max(0, latest.RemainingPercent.Value - (burnRate * elapsedSinceLatestHours));

        DateTimeOffset? exhaustion = null;
        if (burnRate > 0)
        {
            exhaustion = nowUtc.AddHours(projectedRemaining / burnRate);
        }

        bool? survives = null;
        double? sustainable = null;
        if (latest.ResetsAtUtc is not null && latest.ResetsAtUtc > nowUtc)
        {
            survives = exhaustion is null || exhaustion >= latest.ResetsAtUtc;
            sustainable = projectedRemaining / (latest.ResetsAtUtc.Value - nowUtc).TotalHours;
        }

        var sampleConfidence = Math.Clamp(observedRates.Count / 12.0, 0.2, 0.85);
        var freshnessHours = Math.Max(0, (nowUtc - latest.CapturedAtUtc).TotalHours);
        var freshnessFactor = Math.Clamp(1.0 - (freshnessHours / 6.0), 0.25, 1.0);

        return new Forecast(
            latest.Kind,
            nowUtc,
            burnRate,
            exhaustion,
            survives,
            sustainable,
            sampleConfidence * freshnessFactor);
    }

    private static Forecast UnknownForecast(QuotaSnapshot latest, DateTimeOffset nowUtc) =>
        new(latest.Kind, nowUtc, null, null, null, null, 0.15);

    private static double ComputeEwma(IReadOnlyList<double> values, double alpha)
    {
        var ewma = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            ewma = (alpha * values[i]) + ((1 - alpha) * ewma);
        }

        return ewma;
    }
}
