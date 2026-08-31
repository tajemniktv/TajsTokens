using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed class QuotaAlertEngine
{
    private readonly int[] _thresholds;
    private readonly HashSet<string> _emittedKeys = new(StringComparer.Ordinal);
    private TelemetrySnapshot? _previous;

    public QuotaAlertEngine(IEnumerable<int>? thresholds = null)
    {
        _thresholds = (thresholds ?? [30, 20, 10, 5])
            .Where(value => value is > 0 and < 100)
            .Distinct()
            .OrderByDescending(value => value)
            .ToArray();
    }

    public IReadOnlyList<AlertNotification> Evaluate(TelemetrySnapshot current)
    {
        var alerts = new List<AlertNotification>();

        if (current.QuotaDataFresh)
        {
            EvaluateQuotaAlerts(current, alerts);
        }

        EvaluateProviderHealthAlerts(current, alerts);
        _previous = current;
        return alerts;
    }

    private void EvaluateQuotaAlerts(TelemetrySnapshot current, ICollection<AlertNotification> alerts)
    {
        foreach (var quota in current.QuotaSnapshots.Where(snapshot =>
                     snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly &&
                     snapshot.RemainingPercent is not null))
        {
            var remaining = quota.RemainingPercent!.Value;
            var identity = BuildWindowIdentity(quota);
            var threshold = _thresholds
                .Where(value => remaining <= value)
                .DefaultIfEmpty(-1)
                .Min();

            if (threshold > 0)
            {
                var key = $"quota-low:{identity}:{threshold}";
                if (_emittedKeys.Add(key))
                {
                    var label = quota.Kind == QuotaWindowKind.FiveHour ? "5-hour" : "weekly";
                    var reset = quota.ResetsAtUtc is DateTimeOffset resetAt
                        ? $" Resets {resetAt.ToLocalTime():ddd HH:mm}."
                        : string.Empty;
                    alerts.Add(new AlertNotification(
                        key,
                        $"Codex {label} quota low",
                        $"{remaining:0.#}% remaining.{reset}"));
                }
            }

            var previousQuota = _previous?.QuotaSnapshots
                .Where(snapshot => snapshot.Kind == quota.Kind)
                .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
                .FirstOrDefault();

            if (previousQuota?.ResetsAtUtc is DateTimeOffset previousReset &&
                quota.ResetsAtUtc is DateTimeOffset currentReset &&
                currentReset != previousReset &&
                remaining > (previousQuota.RemainingPercent ?? 0) + 5)
            {
                var resetKey = $"quota-reset:{quota.Kind}:{currentReset.ToUnixTimeSeconds()}";
                if (_emittedKeys.Add(resetKey))
                {
                    var label = quota.Kind == QuotaWindowKind.FiveHour ? "5-hour" : "weekly";
                    alerts.Add(new AlertNotification(
                        resetKey,
                        $"Codex {label} quota refreshed",
                        $"New window detected with {remaining:0.#}% remaining."));
                }
            }
        }
    }

    private void EvaluateProviderHealthAlerts(TelemetrySnapshot current, ICollection<AlertNotification> alerts)
    {
        foreach (var source in current.Sources.Where(source =>
                     source.State is TelemetryHealthState.Unavailable or TelemetryHealthState.Error))
        {
            var previousState = _previous?.Sources
                .FirstOrDefault(previous => string.Equals(previous.Provider, source.Provider, StringComparison.OrdinalIgnoreCase))
                ?.State;

            if (previousState == source.State)
            {
                continue;
            }

            // Consecutive identical failures are suppressed by the previous-state check. Do not put
            // provider failures in the lifetime quota-key set: after a provider recovers, a later
            // outage is a new actionable transition and should notify again.
            var key = $"provider-health:{source.Provider}:{source.State}:{current.CapturedAtUtc.ToUnixTimeMilliseconds()}";
            alerts.Add(new AlertNotification(
                key,
                $"TajsTokens: {source.Provider} unavailable",
                source.Detail));
        }
    }

    private static string BuildWindowIdentity(QuotaSnapshot snapshot)
    {
        if (snapshot.ResetsAtUtc is DateTimeOffset reset)
        {
            return $"{snapshot.Provider}:{snapshot.Profile}:{snapshot.Kind}:{reset.ToUnixTimeSeconds()}";
        }

        return $"{snapshot.Provider}:{snapshot.Profile}:{snapshot.Kind}:{snapshot.CapturedAtUtc:yyyyMMddHH}";
    }
}
