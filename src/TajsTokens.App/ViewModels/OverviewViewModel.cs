using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using TajsTokens.App.Models;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.App.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject
{
    [ObservableProperty]
    private QuotaCardViewModel fiveHourQuota;

    [ObservableProperty]
    private QuotaCardViewModel weeklyQuota;

    public IReadOnlyList<TokenSummaryCard> TokenSummaryCards { get; }
    public IReadOnlyList<ForecastPoint> HistoryPoints { get; }
    public IReadOnlyList<AgentNode> ActiveAgents { get; }
    public IReadOnlyList<EventItem> RecentEvents { get; }

    public OverviewViewModel()
    {
        var now = DateTimeOffset.UtcNow;
        var service = new ForecastingService();

        var fiveHourSnapshots = BuildSnapshots(QuotaWindowKind.FiveHour, now, now.AddHours(2.5), 46, 14, 300);
        var weeklySnapshots = BuildSnapshots(QuotaWindowKind.Weekly, now, now.AddDays(4.5), 28, 4.5, 10_080);

        var fiveHourForecast = service.BuildForecast(fiveHourSnapshots, now);
        var weeklyForecast = service.BuildForecast(weeklySnapshots, now);

        FiveHourQuota = BuildQuotaCard("5-hour quota", fiveHourSnapshots[^1], fiveHourForecast);
        WeeklyQuota = BuildQuotaCard("Weekly quota", weeklySnapshots[^1], weeklyForecast);

        TokenSummaryCards =
        [
            new("Uncached input", "28.7M", "+4.2% / 24h"),
            new("Cache read", "69.0M", "+1.1% / 24h"),
            new("Output", "11.6M", "+3.7% / 24h"),
            new("Reasoning output", "1.8M", "-0.4% / 24h"),
            new("Total", "111.1M", "+2.9% / 24h")
        ];

        HistoryPoints =
        [
            new("-8h", 16), new("-7h", 22), new("-6h", 18), new("-5h", 29),
            new("-4h", 31), new("-3h", 35), new("-2h", 41), new("-1h", 48), new("Now", 52)
        ];

        ActiveAgents =
        [
            new AgentNode(
                "Parent: repo-refactor-orchestrator",
                "Running",
                "gpt-5-codex",
                [
                    new AgentNode("Subagent: tests-sweeper", "Running", "gpt-5-mini"),
                    new AgentNode("Subagent: doc-sync", "Waiting", "gpt-5-mini"),
                    new AgentNode("Subagent: risk-analyzer", "Completed", "gpt-5-codex")
                ])
        ];

        RecentEvents =
        [
            new("2m ago", "Forecast", "5h exhaustion estimate moved later by 18m after burn slowdown."),
            new("17m ago", "Reset detection", "Detected 5h window reset and checkpointed quota baseline."),
            new("42m ago", "Announcement", "Stub provider captured an announcement event for policy monitoring."),
            new("1h ago", "Agent activity", "Nested subagent topology changed: doc-sync moved to waiting.")
        ];
    }

    private static List<QuotaSnapshot> BuildSnapshots(
        QuotaWindowKind kind,
        DateTimeOffset now,
        DateTimeOffset resetAt,
        double currentUsedPercent,
        double growthPercent,
        int windowMinutes)
    {
        var snapshots = new List<QuotaSnapshot>();
        for (var i = 6; i >= 0; i--)
        {
            var observed = now.AddMinutes(-i * 35);
            var used = Math.Max(0, currentUsedPercent - (i * growthPercent / 6));
            snapshots.Add(new QuotaSnapshot(kind, observed, used, windowMinutes, resetAt, "codex", "demo", "mock"));
        }

        return snapshots;
    }

    private static QuotaCardViewModel BuildQuotaCard(string title, QuotaSnapshot snapshot, Forecast forecast)
    {
        var remainingText = snapshot.RemainingPercent is double remaining ? $"{remaining:0.0}%" : "Unknown";
        var exhaustionText = forecast.EstimatedExhaustionAtUtc is null
            ? "Insufficient data"
            : forecast.EstimatedExhaustionAtUtc.Value.ToLocalTime().ToString("ddd HH:mm");
        var burnText = forecast.BurnRatePercentPerHour is double burn
            ? $"{burn:0.0} pp/h"
            : "Insufficient data";

        var survivalMessage = forecast.SurvivesUntilReset switch
        {
            true => "Likely to survive until reset.",
            false => "Likely to exhaust before reset.",
            null => "Not enough data to compare exhaustion with reset."
        };

        var severity = forecast.SurvivesUntilReset switch
        {
            false => InfoBarSeverity.Warning,
            true => InfoBarSeverity.Success,
            null => InfoBarSeverity.Informational
        };

        return new QuotaCardViewModel(
            title,
            remainingText,
            snapshot.ResetsAtUtc is null ? "Unknown" : FormatTimeSpan(snapshot.ResetsAtUtc.Value - DateTimeOffset.UtcNow),
            burnText,
            exhaustionText,
            snapshot.RemainingPercent is double gauge ? $"{gauge:0.0}% remaining" : "Quota unavailable",
            survivalMessage,
            severity);
    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan <= TimeSpan.Zero)
        {
            return "due now";
        }

        if (timeSpan.TotalHours >= 24)
        {
            return $"{timeSpan.TotalDays:0.#}d";
        }

        return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
    }
}
