// Taj's Tokens | CodexHistoricalAnalytics.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

// Historical-period basis points and current-full-allowance task percentages are different quantities.
public sealed record CodexPlanHistoryReport(
    DateTimeOffset? DataAsOfUtc,
    DateTimeOffset? CoverageStartUtc,
    bool? CoverageComplete,
    bool Approximate,
    int? BoundaryToleranceSeconds,
    IReadOnlyList<CodexPlanPeriod> Periods);

public sealed record CodexPlanPeriod(
    string Id,
    int WindowMinutes,
    string PlanType,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    bool? AccountingComplete,
    decimal? UsedBasisPoints,
    IReadOnlyList<CodexPlanBreakdown> Breakdowns);

public sealed record CodexPlanBreakdown(string Dimension, IReadOnlyList<CodexPlanValue> Rows);

public sealed record CodexPlanValue(string Key, decimal BasisPoints);

public sealed record CodexTaskUsageReport(
    DateTimeOffset? DataAsOfUtc,
    IReadOnlyList<string> RequestedThreadIds,
    IReadOnlyList<CodexTaskUsage> Threads);

public sealed record CodexTaskAmounts(decimal? FiveHourLimitPercent, decimal? WeeklyLimitPercent, decimal? BalanceUsageCredits);

public sealed record CodexTaskUsage(
    string ThreadId,
    string DataStatus,
    string UsageSource,
    CodexTaskAmounts Amounts,
    IReadOnlyList<CodexTaskGroup> Groups);

public sealed record CodexTaskGroup(
    string? ProductExperience,
    string? Model,
    string? ReasoningEffort,
    string? Speed,
    CodexTaskAmounts Amounts);