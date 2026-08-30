using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record QuotaSnapshot(
    QuotaWindowKind Kind,
    DateTimeOffset CapturedAtUtc,
    double? UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAtUtc,
    string Provider,
    string Profile,
    string Source)
{
    public double? RemainingPercent => UsedPercent is null
        ? null
        : Math.Clamp(100d - UsedPercent.Value, 0d, 100d);
}
