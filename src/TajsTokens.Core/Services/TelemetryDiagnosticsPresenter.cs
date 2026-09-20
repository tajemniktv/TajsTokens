// Taj's Tokens | TelemetryDiagnosticsPresenter.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>Explains existing health and forecast decisions without probing or classifying raw errors.</summary>
public static class TelemetryDiagnosticsPresenter
{
    public static TelemetryDiagnostics Present(TelemetrySnapshot snapshot)
    {
        TelemetryDiagnosticEntry[] sources = snapshot.Sources.Select(source => new TelemetryDiagnosticEntry(
            source.Provider,
            source.State.ToString(),
            source.LastSuccessUtc is { } success
                ? $"Last successful read: {success.ToLocalTime():g}"
                : "No successful read recorded in this app session.",
            source.Detail + (source.State == TelemetryHealthState.Live ? string.Empty : "\n\n" + RecoveryHint(source.Provider)))).ToArray();
        TelemetryDiagnosticEntry[] windows = new[] { QuotaWindowKind.FiveHour, QuotaWindowKind.Weekly }
            .Select(kind => ExplainWindow(snapshot, kind)).ToArray();
        return new TelemetryDiagnostics(
            snapshot.CapturedAtUtc == DateTimeOffset.MinValue
                ? "Waiting for the first collector refresh. Refresh now to retry; opening Diagnostics does not start another collection loop."
                : $"Collector snapshot: {snapshot.CapturedAtUtc.ToLocalTime():g} · {snapshot.Trigger}. " +
                  (snapshot.PersistenceAvailable
                      ? "Owned history is available."
                      : "Owned history is unavailable; live readings may still be usable."),
            sources,
            windows);
    }

    private static TelemetryDiagnosticEntry ExplainWindow(TelemetrySnapshot snapshot, QuotaWindowKind kind)
    {
        string name = kind == QuotaWindowKind.FiveHour ? "5-hour quota" : "Weekly quota";
        QuotaLaneState? lane = snapshot.QuotaLanes.FirstOrDefault(x => x.Kind == kind && x.Provider == "codex" && x.Profile == "default");
        if (lane?.NotReportedByProvider == true)
            return new TelemetryDiagnosticEntry(
                name,
                "Not reported",
                "Codex did not include this window in its successful response.",
                "This is not a failed read and does not mean unlimited usage. It will appear if Codex reports it again.");
        QuotaSnapshot? current = lane?.Snapshot;
        if (current is null)
            return new TelemetryDiagnosticEntry(
                name,
                "Unavailable",
                "No quota observation is available for this window.",
                "Check Codex app-server health below and refresh. No usage or reset value is assumed.");
        string detail = $"{QuotaAccountScope.Describe(current.AccountKey)}\nSource: {current.Source}\n" +
                        $"Observed: {current.CapturedAtUtc.ToLocalTime():g}\nReset: {current.ResetsAtUtc?.ToLocalTime().ToString("g") ?? "not reported"}";
        if (!snapshot.IsQuotaSnapshotFresh(current))
            return new TelemetryDiagnosticEntry(
                name,
                "Stale",
                "Last-known quota only; forecasts and alerts are paused for this lane.",
                detail + "\nRefresh the provider before using it as current quota.");
        CurrentQuotaForecast? forecast = snapshot.FindCurrentForecast(current);
        string summary = current.RemainingPercent is { } remaining ? $"{remaining:0.#}% remaining" : "Remaining quota was not reported";
        if (forecast is { IsFresh: true })
        {
            summary += forecast.Forecast is { } prediction ? $" · forecast: {prediction.State}" : " · forecast unavailable";
            detail += $"\n\n{forecast.HistoryPolicy}";
            if (!string.IsNullOrWhiteSpace(forecast.Diagnostic)) detail += $"\n{forecast.Diagnostic}";
            if (forecast.Forecast?.Evidence is { } evidence)
                detail += $"\nModel: {evidence.Model}\n{evidence.UncertaintyDescription}";
        }
        else
        {
            detail += "\n\nNo fresh forecast result matches this observation yet. Another observation's outlook is not substituted.";
        }
        return new TelemetryDiagnosticEntry(name, "Live", summary, detail);
    }

    private static string RecoveryHint(string provider)
    {
        return provider switch
        {
            "Codex app-server" =>
                "Check that the Codex CLI is available and signed in, then refresh. Account changes and omitted windows are not repaired by deleting history.",
            "SQLite" =>
                "Check the data-folder path and permissions in Settings. Preserve the data and deployment backups before attempting recovery; do not delete the database to retry.",
            "Codex rollouts" =>
                "Open Rollout coverage for indexed/discovered path comparisons, or Native sources for availability. Unindexed paths are not automatically missing tasks.",
            _ =>
                "Review the reported detail, check source availability in Native sources or Settings, then refresh. This message does not infer a cause from an error string.",
        };
    }
}