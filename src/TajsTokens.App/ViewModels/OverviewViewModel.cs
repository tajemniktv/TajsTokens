using CommunityToolkit.Mvvm.ComponentModel;
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
        var now = DateTimeOffset.Now;
        var service = new ForecastingService();

        var fiveHourSnapshots = BuildSnapshots(QuotaWindowKind.FiveHour, now, 200_000, now.AddHours(2.5), 54_000, 11_000);
        var weeklySnapshots = BuildSnapshots(QuotaWindowKind.Weekly, now, 5_000_000, now.AddDays(4.5), 1_340_000, 225_000);

        var fiveHourForecast = service.BuildForecast(fiveHourSnapshots, now);
        var weeklyForecast = service.BuildForecast(weeklySnapshots, now);

        FiveHourQuota = BuildQuotaCard("5-hour quota", fiveHourSnapshots[^1], fiveHourForecast);
        WeeklyQuota = BuildQuotaCard("Weekly quota", weeklySnapshots[^1], weeklyForecast);

        TokenSummaryCards =
        [
            new("Input", "28.7k", "+4.2% / 24h"),
            new("Cached input", "6.9k", "+1.1% / 24h"),
            new("Output", "13.4k", "+3.7% / 24h"),
            new("Reasoning", "1.8k", "-0.4% / 24h"),
            new("Total", "50.8k", "+2.9% / 24h")
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
        double limit,
        DateTimeOffset resetAt,
        double currentUsed,
        double growth)
    {
        var snapshots = new List<QuotaSnapshot>();
        for (var i = 6; i >= 0; i--)
        {
            var observed = now.AddMinutes(-i * 35);
            snapshots.Add(new QuotaSnapshot(kind, observed, currentUsed - (i * growth / 6), limit, resetAt));
        }

        return snapshots;
    }

    private static QuotaCardViewModel BuildQuotaCard(string title, QuotaSnapshot snapshot, Forecast forecast)
    {
        var remainingPercent = snapshot.RemainingPercent * 100;
        var exhaustionText = forecast.EstimatedExhaustionAtUtc is null
            ? "No exhaustion predicted"
            : forecast.EstimatedExhaustionAtUtc.Value.LocalDateTime.ToString("ddd HH:mm");

        return new QuotaCardViewModel(
            title,
            $"{remainingPercent:0.0}%",
            FormatTimeSpan(snapshot.ResetsAtUtc - snapshot.CapturedAtUtc),
            $"{forecast.BurnRatePerHour:0} tok/h",
            exhaustionText,
            forecast.SurvivesUntilReset,
            $"{snapshot.RemainingTokens / 1000:0.0}k / {snapshot.LimitTokens / 1000:0}k tokens",
            forecast.SurvivesUntilReset ? "Likely to survive until reset." : "Likely to exhaust before reset.");
    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan.TotalHours >= 24)
        {
            return $"{timeSpan.TotalDays:0.#}d";
        }

        return $"{Math.Max(0, (int)timeSpan.TotalHours)}h {Math.Max(0, timeSpan.Minutes)}m";
    }
}
