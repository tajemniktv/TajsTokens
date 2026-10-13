// Taj's Tokens | LocalLimitsModels.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>A content-free, source-qualified projection for explicitly authorized local clients.</summary>
public sealed record LocalLimitsEnvelope(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset? LastRefreshAtUtc,
    string Status,
    IReadOnlyList<LocalLimitResource> Resources);

public sealed record LocalLimitResource(
    string Id,
    string Provider,
    string Window,
    string AccountIdentity,
    string? AccountId,
    string Source,
    string Authority,
    string State,
    double? UsedPercent,
    double? RemainingPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAtUtc,
    DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? CollectedAtUtc);
