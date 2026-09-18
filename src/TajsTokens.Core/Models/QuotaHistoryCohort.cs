using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record QuotaHistoryCohort(string Provider, string Profile, QuotaWindowKind Kind,
    string Source, string? AccountKey, string? LimitId, string? PlanType, string? SessionId, int? WindowMinutes);
