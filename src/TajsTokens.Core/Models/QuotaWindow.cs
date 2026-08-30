using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record QuotaWindow(
    QuotaWindowKind Kind,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    double LimitTokens)
{
    public TimeSpan Duration => WindowEndUtc - WindowStartUtc;
}
