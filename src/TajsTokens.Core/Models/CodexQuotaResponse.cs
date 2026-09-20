// Taj's Tokens | CodexQuotaResponse.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Core.Models;

/// <summary>Response scope survives even when Codex reports no supported quota windows.</summary>
public sealed record CodexQuotaResponse(IReadOnlyList<QuotaSnapshot> Snapshots, string? AccountKey)
{
    public CodexServerObservation? MetadataObservation { get; init; }
}

/// <summary>Named native buckets, not additive quota gauges or workload-credit estimates.</summary>
public sealed record CodexQuotaMetadataReport(string Contract, IReadOnlyList<CodexQuotaLimitMetadata> Limits);

public sealed record CodexQuotaLimitMetadata(
    string ResponseKey,
    string? LimitId,
    string? LimitName,
    string? PlanType,
    string? RateLimitReachedType,
    bool? SpendControlReached,
    string? NormalModelSlug,
    CodexQuotaCredits? Credits,
    CodexSpendControlLimit? IndividualLimit,
    CodexQuotaReportedWindow? Primary,
    CodexQuotaReportedWindow? Secondary);

public sealed record CodexQuotaCredits(bool? HasCredits, bool? Unlimited, string? Balance);

public sealed record CodexSpendControlLimit(string? Limit, string? Used, long? RemainingPercent, long? ResetsAt);

public sealed record CodexQuotaReportedWindow(double? UsedPercent, long? WindowDurationMins, long? ResetsAt);