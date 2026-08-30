using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record QuotaWindow(
    QuotaWindowKind Kind,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    int? WindowMinutes,
    string Provider,
    string Profile)
{
    public TimeSpan? Duration => WindowStartUtc is not null && WindowEndUtc is not null
        ? WindowEndUtc - WindowStartUtc
        : WindowMinutes is not null
            ? TimeSpan.FromMinutes(WindowMinutes.Value)
            : null;
}
