namespace TajsTokens.Core.Models;

// These are provider-native quantities, NOT token_usage events or quota_snapshots.
public enum ServerEvidenceState { Available, Empty, Unavailable, Unsupported, AuthenticationRequired, Incomplete, Conflict, Invalid, Error, NoSupportedSeam }
public enum AccountEvidenceClass { ProviderVerified, ServerCorrelated, UserDeclaredSingleAccount, Unattributed, Conflicting }
public enum CodexServerSurface { AccountActivity, ThreadUsage, PlanHistory, GroupedAnalytics, DailyCounts, DailyRelativeUsage, QuotaMetadata }

public sealed record CodexAccountActivity(long? LifetimeTokens, long? PeakDailyTokens,
    long? LongestRunningTurnSec, long? CurrentStreakDays, long? LongestStreakDays,
    IReadOnlyList<CodexAccountDay>? DailyUsageBuckets);
public sealed record CodexAccountDay(string StartDate, long Tokens);
public sealed record CodexThreadUsage(string ThreadId, long EstimatedUsageCreditsMicros,
    long? EstimatedUsageUsdMicros, IReadOnlyList<CodexThreadUsageGroup> Groups);
public sealed record CodexThreadUsageGroup(string? Model, string? ReasoningEffort, string? Speed,
    long EstimatedUsageCreditsMicros, long? NetNewInputTokens, long? CachedInputTokens,
    long? InputTokens, long? OutputTokens, long? TotalTokens);
public sealed record CodexServerAccountBracket(string? BeforeAccountKey, DateTimeOffset? BeforeCollectedAtUtc,
    string? AfterAccountKey, DateTimeOffset? AfterCollectedAtUtc);

/// <summary>Only allowlisted, content-free fields are serializable here. No raw response or credentials.</summary>
public sealed record CodexServerObservation(string Id, CodexServerSurface Surface, string? ThreadId,
    DateTimeOffset FetchStartedAtUtc, DateTimeOffset CollectedAtUtc, string ContractVersion,
    string? ClientVersion, ServerEvidenceState State, string Detail)
{
    // Activity/thread responses do not carry accountId. A matching before/after rate-limit
    // account is a separately labelled correlation, never a fabricated native response field.
    public string? CorrelatedAccountKey { get; init; }
    public AccountEvidenceClass AccountEvidence { get; init; } = AccountEvidenceClass.Unattributed;
    public CodexServerAccountBracket? AccountBracket { get; init; }
    public CodexAccountActivity? Activity { get; init; }
    public CodexThreadUsage? ThreadUsage { get; init; }
    public CodexDailyReport? DailyReport { get; init; }
    public CodexQuotaMetadataReport? QuotaMetadata { get; init; }
}

// Daily reports are snapshots, not increments. Dates and units remain provider-native.
public sealed record CodexDailyReport(string SourceContract, string Endpoint, string StartDate,
    string EndDate, string? Units, string? GroupBy, string? DataFreshness,
    string? Plan, string? PolicyBefore, string? PolicyAfter, IReadOnlyList<CodexDailyReportRow> Days);
public sealed record CodexDailyReportRow(string Date, decimal? Credits, decimal? OnDemandCredits,
    long? UncachedInputTokens, long? CachedInputTokens, long? OutputTokens, long? TotalTokens,
    IReadOnlyDictionary<string, decimal>? SurfaceUsage);

public sealed record CodexServerCollection(IReadOnlyList<CodexServerObservation> Observations);
