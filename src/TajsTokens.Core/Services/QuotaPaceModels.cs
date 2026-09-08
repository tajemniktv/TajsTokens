using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Time-based candidates shared by production and walk-forward evaluation.</summary>
public static class QuotaPaceModels
{
    public static readonly string[] Candidates = ["persistence", "legacy-ewma", "epoch", "recent-30m", "recent-2h", "recent-6h", "recent-24h", "damped-2h", "damped-6h"];

    public static double ProjectRemaining(IReadOnlyList<QuotaSnapshot> epoch, string model, double leadHours)
    {
        var rate = Estimate(epoch, model);
        var effectiveHours = model switch
        {
            "damped-2h" => 2 * (1 - Math.Exp(-leadHours / 2)),
            "damped-6h" => 6 * (1 - Math.Exp(-leadHours / 6)),
            _ => leadHours
        };
        return Math.Clamp(epoch[^1].RemainingPercent!.Value - rate * effectiveHours, 0, 100);
    }

    public static double Estimate(IReadOnlyList<QuotaSnapshot> epoch, string model)
    {
        if (epoch.Count < 2 || model == "persistence") return 0;
        var latest = epoch[^1];
        if (model == "legacy-ewma")
        {
            double? rate = null;
            var alpha = latest.Kind == QuotaWindowKind.Weekly ? 0.25 : 0.45;
            for (var i = 1; i < epoch.Count; i++)
            {
                var hours = (epoch[i].CapturedAtUtc - epoch[i - 1].CapturedAtUtc).TotalHours;
                if (hours <= 0) continue;
                var next = Math.Max(0, (epoch[i].UsedPercent!.Value - epoch[i - 1].UsedPercent!.Value) / hours);
                rate = rate is null ? next : alpha * next + (1 - alpha) * rate;
            }
            return rate ?? 0;
        }
        var lookback = model switch
        {
            "recent-30m" => 0.5,
            "recent-2h" or "damped-2h" or "damped-6h" => 2,
            "recent-6h" => 6,
            "recent-24h" => 24,
            "epoch" => double.PositiveInfinity,
            _ => throw new ArgumentException("Unknown pace model", nameof(model))
        };
        var first = epoch[0];
        // Include the observation bracketing the lookback, rather than interpolating a
        // fictitious meter observation or dividing a full delta by a truncated duration.
        foreach (var point in epoch.Take(epoch.Count - 1))
        {
            if ((latest.CapturedAtUtc - point.CapturedAtUtc).TotalHours >= lookback) first = point;
            else break;
        }
        var elapsed = (latest.CapturedAtUtc - first.CapturedAtUtc).TotalHours;
        return elapsed > 0 ? Math.Max(0, (latest.UsedPercent!.Value - first.UsedPercent!.Value) / elapsed) : 0;
    }
}
