using Microsoft.UI.Xaml.Controls;

namespace TajsTokens.App.Models;

public sealed record QuotaCardViewModel(
    string Title,
    string RemainingPercent,
    string ResetCountdown,
    string BurnRate,
    string PredictedExhaustion,
    string GaugeText,
    string SurvivalMessage,
    InfoBarSeverity Severity);

public sealed record TokenSummaryCard(string Label, string Value, string Detail);

public sealed record ForecastPoint(string Label, double Value, string Tooltip);

public sealed record DataSourceStatusCard(string Name, string State, string Detail);

public sealed record EventItem(string Time, string Type, string Description);

// Kept for the dedicated Agents phase. The Phase 1 Overview no longer renders synthetic agents.
public sealed record AgentNode(string Name, string State, string Model, IReadOnlyList<AgentNode>? Children = null);
