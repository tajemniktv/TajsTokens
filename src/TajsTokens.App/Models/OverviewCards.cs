namespace TajsTokens.App.Models;

public sealed record QuotaCardViewModel(
    string Title,
    string RemainingPercent,
    string ResetCountdown,
    string BurnRate,
    string PredictedExhaustion,
    bool SurvivesUntilReset,
    string GaugeText,
    string SurvivalMessage);

public sealed record TokenSummaryCard(string Label, string Value, string Trend);

public sealed record ForecastPoint(string Label, double Value);

public sealed record AgentNode(string Name, string State, string Model, IReadOnlyList<AgentNode>? Children = null);

public sealed record EventItem(string Time, string Type, string Description);
