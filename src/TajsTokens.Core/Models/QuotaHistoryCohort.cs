// Taj's Tokens | QuotaHistoryCohort.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record QuotaHistoryCohort(
    string Provider,
    string Profile,
    QuotaWindowKind Kind,
    string Source,
    string? AccountKey,
    string? LimitId,
    string? PlanType,
    string? SessionId,
    int? WindowMinutes);