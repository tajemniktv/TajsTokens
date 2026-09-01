using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public sealed class QuotaAlertEngine
{
    private int[] _thresholds;
    private readonly HashSet<string> _emittedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fallbackWindowGenerations = new(StringComparer.Ordinal);
    private TelemetrySnapshot? _previous;

    public QuotaAlertEngine(IEnumerable<int>? thresholds = null)
    {
        _thresholds = RuntimeSettings.NormalizeLowQuotaThresholds(thresholds);
    }

    public void UpdateThresholds(IEnumerable<int>? thresholds)
    {
        _thresholds = RuntimeSettings.NormalizeLowQuotaThresholds(thresholds);
    }

    public IReadOnlyList<AlertNotification> Evaluate(TelemetrySnapshot current)
    {
        var alerts = new List<AlertNotification>();

        EvaluateQuotaAlerts(current, alerts);
        EvaluateProviderHealthAlerts(current, alerts);
        _previous = current;
        return alerts;
    }

    private void EvaluateQuotaAlerts(TelemetrySnapshot current, ICollection<AlertNotification> alerts)
    {
        foreach (var quota in current.QuotaSnapshots.Where(snapshot =>
                     (snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly) &&
                     snapshot.RemainingPercent is not null &&
                     current.IsQuotaSnapshotFresh(snapshot)))
        {
            var remaining = quota.RemainingPercent!.Value;
            var previousQuota = FindPreviousQuota(quota);
            var fallbackGeneration = UpdateFallbackWindowGeneration(quota, previousQuota, remaining);
            var identity = BuildWindowIdentity(quota, fallbackGeneration);
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

            var replenished = previousQuota?.RemainingPercent is double previousRemaining &&
                              remaining > previousRemaining + 5;
            var resetChanged = previousQuota?.ResetsAtUtc is DateTimeOffset previousReset &&
                               quota.ResetsAtUtc is DateTimeOffset currentReset &&
                               currentReset != previousReset;
            var resetUnknownButRecovered = previousQuota is not null &&
                                           previousQuota.ResetsAtUtc is null &&
                                           quota.ResetsAtUtc is null &&
                                           replenished;

            if ((resetChanged && replenished) || resetUnknownButRecovered)
            {
                var resetIdentity = quota.ResetsAtUtc is DateTimeOffset resetAt
                    ? resetAt.ToUnixTimeSeconds().ToString()
                    : $"fallback-{fallbackGeneration}";
                var resetKey = $"quota-reset:{quota.Provider}:{quota.Profile}:{quota.Kind}:{resetIdentity}";
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

    private QuotaSnapshot? FindPreviousQuota(QuotaSnapshot quota) =>
        _previous?.QuotaSnapshots
            .Where(snapshot =>
                snapshot.Kind == quota.Kind &&
                string.Equals(snapshot.Provider, quota.Provider, StringComparison.Ordinal) &&
                string.Equals(snapshot.Profile, quota.Profile, StringComparison.Ordinal))
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .FirstOrDefault();

    private int UpdateFallbackWindowGeneration(QuotaSnapshot quota, QuotaSnapshot? previousQuota, double remaining)
    {
        if (quota.ResetsAtUtc is not null)
        {
            return 0;
        }

        var baseIdentity = BuildFallbackBaseIdentity(quota);
        var generation = _fallbackWindowGenerations.GetValueOrDefault(baseIdentity);
        if (previousQuota is not null &&
            previousQuota.ResetsAtUtc is null &&
            previousQuota.RemainingPercent is double previousRemaining &&
            remaining > previousRemaining + 5)
        {
            generation++;
            _fallbackWindowGenerations[baseIdentity] = generation;
        }
        else
        {
            _fallbackWindowGenerations.TryAdd(baseIdentity, generation);
        }

        return generation;
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

    private static string BuildWindowIdentity(QuotaSnapshot snapshot, int fallbackGeneration)
    {
        if (snapshot.ResetsAtUtc is DateTimeOffset reset)
        {
            return $"{snapshot.Provider}:{snapshot.Profile}:{snapshot.Kind}:{reset.ToUnixTimeSeconds()}";
        }

        return $"{BuildFallbackBaseIdentity(snapshot)}:{fallbackGeneration}";
    }

    private static string BuildFallbackBaseIdentity(QuotaSnapshot snapshot) =>
        $"{snapshot.Provider}:{snapshot.Profile}:{snapshot.Kind}:no-reset";
}
