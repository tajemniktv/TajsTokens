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

        // A forecast for `nowUtc` must never use observations that have not happened yet. This also
        // makes historical/backtest forecasts deterministic when newer telemetry exists in the store.
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

        // Reset timestamps/window duration identify the provider's quota-window instance. Restrict the
        // burn calculation to the current instance so a reset crossing cannot look like ordinary burn,
        // even when the first post-reset sample is numerically higher than the final pre-reset sample.
        var currentWindow = eligible
            .Where(x => x.WindowMinutes == latest.WindowMinutes && x.ResetsAtUtc == latest.ResetsAtUtc)
            .ToArray();

        var observedRates = new List<double>(Math.Max(0, currentWindow.Length - 1));
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
                // Defensive fallback for providers that fail to advance reset metadata promptly.
                continue;
            }

            observedRates.Add(delta / hours);
        }

        if (observedRates.Count == 0)
        {
            return UnknownForecast(latest, nowUtc);
        }

        var burnRate = ComputeEwma(observedRates, 0.45);
        var elapsedSinceLatestHours = (nowUtc - latest.CapturedAtUtc).TotalHours;
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
        var freshnessHours = (nowUtc - latest.CapturedAtUtc).TotalHours;
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
