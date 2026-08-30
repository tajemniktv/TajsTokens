using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed class ForecastingService : IForecastingService
{
    public Forecast BuildForecast(IReadOnlyList<QuotaSnapshot> snapshots, DateTimeOffset nowUtc)
    {
        if (snapshots.Count < 2)
        {
            return new Forecast(
                QuotaWindowKind.FiveHour,
                nowUtc,
                0,
                null,
                true,
                0,
                0.2);
        }

        var ordered = snapshots.OrderBy(x => x.CapturedAtUtc).ToArray();
        var latest = ordered[^1];

        var weightedRates = new List<double>(ordered.Length - 1);
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];
            var hours = (current.CapturedAtUtc - previous.CapturedAtUtc).TotalHours;
            if (hours <= 0)
            {
                continue;
            }

            var delta = current.UsedTokens - previous.UsedTokens;
            weightedRates.Add(Math.Max(0, delta / hours));
        }

        var burnRate = ComputeEwma(weightedRates, 0.45);
        var sustainablePerHour = latest.LimitTokens / Math.Max((latest.ResetsAtUtc - latest.CapturedAtUtc).TotalHours, 1);

        DateTimeOffset? exhaustion = null;
        if (burnRate > 0)
        {
            exhaustion = latest.CapturedAtUtc.AddHours(latest.RemainingTokens / burnRate);
        }

        var survives = exhaustion is null || exhaustion >= latest.ResetsAtUtc;
        var confidence = Math.Clamp(weightedRates.Count / 12.0, 0.25, 0.85);

        return new Forecast(
            latest.Kind,
            nowUtc,
            burnRate,
            exhaustion,
            survives,
            sustainablePerHour,
            confidence);
    }

    private static double ComputeEwma(IReadOnlyList<double> values, double alpha)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ewma = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            ewma = (alpha * values[i]) + ((1 - alpha) * ewma);
        }

        return ewma;
    }
}
