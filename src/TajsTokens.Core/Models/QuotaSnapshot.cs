using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record QuotaSnapshot(
    QuotaWindowKind Kind,
    DateTimeOffset CapturedAtUtc,
    double UsedTokens,
    double LimitTokens,
    DateTimeOffset ResetsAtUtc)
{
    public double RemainingTokens => Math.Max(0, LimitTokens - UsedTokens);
    public double RemainingPercent => LimitTokens <= 0 ? 0 : RemainingTokens / LimitTokens;
}
