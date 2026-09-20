// Taj's Tokens | QuotaAlertEngine.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

public sealed class QuotaAlertEngine
{
    private readonly HashSet<string> _emittedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fallbackWindowGenerations = new(StringComparer.Ordinal);
    private CodexCurrentState? _previous;
    private int[] _thresholds;

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
        return Evaluate(CodexIntelligenceProjection.CurrentState(current));
    }

    public IReadOnlyList<AlertNotification> Evaluate(CodexCurrentState current)
    {
        var alerts = new List<AlertNotification>();

        EvaluateQuotaAlerts(current, alerts);
        EvaluateProviderHealthAlerts(current, alerts);
        _previous = current;
        return alerts;
    }

    public IReadOnlyList<AlertNotification> Evaluate(CodexIntelligenceSnapshot current)
    {
        return Evaluate(current.CurrentState);
    }

    private void EvaluateQuotaAlerts(CodexCurrentState current, ICollection<AlertNotification> alerts)
    {
        foreach (QuotaSnapshot quota in current.QuotaSnapshots.Where(snapshot =>
                     snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly &&
                     snapshot.RemainingPercent is not null &&
                     current.IsQuotaSnapshotFresh(snapshot)))
        {
            double remaining = quota.RemainingPercent!.Value;
            QuotaSnapshot? previousQuota = FindPreviousQuota(quota);
            int fallbackGeneration = UpdateFallbackWindowGeneration(quota, previousQuota, remaining);
            string identity = BuildWindowIdentity(quota, fallbackGeneration);
            int threshold = _thresholds
                .Where(value => remaining <= value)
                .DefaultIfEmpty(-1)
                .Min();

            if (threshold > 0)
            {
                string key = $"quota-low:{identity}:{threshold}";
                if (_emittedKeys.Add(key))
                {
                    string label = quota.Kind == QuotaWindowKind.FiveHour ? "5-hour" : "weekly";
                    string reset = quota.ResetsAtUtc is DateTimeOffset resetAt
                        ? $" Resets {resetAt.ToLocalTime():ddd HH:mm}."
                        : string.Empty;
                    alerts.Add(
                        new AlertNotification(
                            key,
                            $"Codex {label} quota low",
                            $"{remaining:0.#}% remaining.{reset}"));
                }
            }

            bool replenished = previousQuota?.RemainingPercent is double previousRemaining &&
                               remaining > previousRemaining + 5;
            bool resetChanged = previousQuota?.ResetsAtUtc is DateTimeOffset previousReset &&
                                quota.ResetsAtUtc is DateTimeOffset currentReset &&
                                currentReset != previousReset;
            bool resetUnknownButRecovered = previousQuota is not null &&
                                            previousQuota.ResetsAtUtc is null &&
                                            quota.ResetsAtUtc is null &&
                                            replenished;

            if (resetChanged && replenished || resetUnknownButRecovered)
            {
                string resetIdentity = quota.ResetsAtUtc is DateTimeOffset resetAt
                    ? resetAt.ToUnixTimeSeconds().ToString()
                    : $"fallback-{fallbackGeneration}";
                string resetKey =
                    $"quota-reset:{quota.Provider}:{quota.Profile}:{quota.Kind}:{resetIdentity}:{quota.AccountKey ?? "unknown"}:{quota.Source}";
                if (_emittedKeys.Add(resetKey))
                {
                    string label = quota.Kind == QuotaWindowKind.FiveHour ? "5-hour" : "weekly";
                    alerts.Add(
                        new AlertNotification(
                            resetKey,
                            $"Codex {label} quota refreshed",
                            $"New window detected with {remaining:0.#}% remaining."));
                }
            }
        }
    }

    private QuotaSnapshot? FindPreviousQuota(QuotaSnapshot quota)
    {
        return _previous?.QuotaSnapshots
            .Where(snapshot =>
                snapshot.Kind == quota.Kind &&
                string.Equals(snapshot.Provider, quota.Provider, StringComparison.Ordinal) &&
                string.Equals(snapshot.Profile, quota.Profile, StringComparison.Ordinal) &&
                snapshot.Source == quota.Source && snapshot.AccountKey == quota.AccountKey)
            .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
            .FirstOrDefault();
    }

    private int UpdateFallbackWindowGeneration(QuotaSnapshot quota, QuotaSnapshot? previousQuota, double remaining)
    {
        if (quota.ResetsAtUtc is not null)
        {
            return 0;
        }

        string baseIdentity = BuildFallbackBaseIdentity(quota);
        int generation = _fallbackWindowGenerations.GetValueOrDefault(baseIdentity);
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

    private void EvaluateProviderHealthAlerts(CodexCurrentState current, ICollection<AlertNotification> alerts)
    {
        foreach (ProviderHealthSnapshot source in current.Sources.Where(source =>
                     source.State is TelemetryHealthState.Unavailable or TelemetryHealthState.Error))
        {
            TelemetryHealthState? previousState = _previous?.Sources
                .FirstOrDefault(previous => string.Equals(previous.Provider, source.Provider, StringComparison.OrdinalIgnoreCase))
                ?.State;

            if (previousState == source.State)
            {
                continue;
            }

            // Consecutive identical failures are suppressed by the previous-state check. Do not put
            // provider failures in the lifetime quota-key set: after a provider recovers, a later
            // outage is a new actionable transition and should notify again.
            string key = $"provider-health:{source.Provider}:{source.State}:{current.CapturedAtUtc.ToUnixTimeMilliseconds()}";
            alerts.Add(
                new AlertNotification(
                    key,
                    $"TajsTokens: {source.Provider} unavailable",
                    source.Detail));
        }
    }

    private static string BuildWindowIdentity(QuotaSnapshot snapshot, int fallbackGeneration)
    {
        if (snapshot.ResetsAtUtc is DateTimeOffset reset)
        {
            return
                $"{snapshot.Provider}:{snapshot.Profile}:{snapshot.Kind}:{reset.ToUnixTimeSeconds()}:{snapshot.AccountKey ?? "unknown"}:{snapshot.Source}";
        }

        return $"{BuildFallbackBaseIdentity(snapshot)}:{fallbackGeneration}";
    }

    private static string BuildFallbackBaseIdentity(QuotaSnapshot snapshot)
    {
        return $"{snapshot.Provider}:{snapshot.Profile}:{snapshot.Kind}:no-reset:{snapshot.AccountKey ?? "unknown"}:{snapshot.Source}";
    }
}