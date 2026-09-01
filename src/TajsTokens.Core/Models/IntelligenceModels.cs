using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record IntelligenceQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    AnalyticsBucketSize BucketSize = AnalyticsBucketSize.Hour,
    int MaxBuckets = 720);

public sealed record UsageHistoryBucket(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long NativeTokens,
    long UncachedInputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long NonReasoningOutputTokens,
    long ReasoningOutputTokens,
    long RootTokens,
    long SubagentTokens,
    int ActiveSessions,
    int Compactions,
    double? FiveHourQuotaDelta,
    double? WeeklyQuotaDelta)
{
    public long DisjointTokens => checked(
        UncachedInputTokens + CacheReadTokens + CacheWriteTokens + NonReasoningOutputTokens + ReasoningOutputTokens);
    public long IntegrityDelta => checked(NativeTokens - DisjointTokens);
    public bool IntegrityExact => IntegrityDelta == 0;
}

public sealed record UsageDimensionTotal(
    string Dimension,
    string Value,
    long NativeTokens,
    long UncachedInputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long NonReasoningOutputTokens,
    long ReasoningOutputTokens,
    int Sessions)
{
    public long DisjointTokens => checked(
        UncachedInputTokens + CacheReadTokens + CacheWriteTokens + NonReasoningOutputTokens + ReasoningOutputTokens);
    public long IntegrityDelta => checked(NativeTokens - DisjointTokens);
    public bool IntegrityExact => IntegrityDelta == 0;
}

public sealed record UsageHeatmapCell(
    DayOfWeek Day,
    int Hour,
    long NativeTokens,
    int ActiveBuckets);

public sealed record QuotaBurnInterval(
    string IntervalId,
    QuotaWindowKind Kind,
    string Provider,
    string Profile,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double BeforeUsedPercent,
    double AfterUsedPercent,
    double DeltaUsedPercent,
    DateTimeOffset? ResetsAtUtc,
    string BeforeSource,
    string AfterSource,
    long NativeTokens,
    long RootTokens,
    long SubagentTokens,
    int RootSessions,
    int SubagentSessions,
    int Compactions,
    string? DominantModel,
    string? DominantReasoningEffort,
    double Confidence);

public sealed record QuotaContributor(
    string SessionId,
    string DisplayName,
    string Repository,
    string? Model,
    string? ReasoningEffort,
    bool IsSubagent,
    long NativeTokens,
    long UncachedInputTokens,
    long CacheReadTokens,
    int Compactions,
    double TokenShare,
    double AttributionScore);

public sealed record QuotaBurnDetail(
    QuotaBurnInterval Interval,
    IReadOnlyList<QuotaContributor> Contributors,
    string Methodology);

public sealed record QuotaResetEvent(
    string EventId,
    QuotaWindowKind Kind,
    string Provider,
    string Profile,
    DateTimeOffset DetectedAtUtc,
    DateTimeOffset EffectiveAtUtc,
    double? BeforeUsedPercent,
    double? AfterUsedPercent,
    DateTimeOffset? PreviousResetAtUtc,
    DateTimeOffset? CurrentResetAtUtc,
    QuotaResetClassification Classification,
    double Confidence,
    string Source,
    string Explanation);

public sealed record ScenarioRequest(
    double DurationHours,
    int RootAgents,
    int Subagents,
    double IntensityMultiplier = 1.0,
    string? Model = null,
    string? ReasoningEffort = null);

public sealed record ScenarioHistorySample(
    QuotaWindowKind Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double QuotaDeltaPercent,
    int RootAgents,
    int Subagents,
    string? DominantModel,
    string? DominantReasoningEffort);

public sealed record ScenarioWindowEstimate(
    QuotaWindowKind Kind,
    bool HasEnoughHistory,
    int SampleCount,
    double? ExpectedQuotaDeltaPercent,
    double? LowerQuotaDeltaPercent,
    double? UpperQuotaDeltaPercent,
    double Confidence,
    string Explanation);

public sealed record ScenarioEstimate(
    ScenarioRequest Request,
    ScenarioWindowEstimate FiveHour,
    ScenarioWindowEstimate Weekly,
    string Methodology);

public sealed record IntelligenceDashboard(
    IntelligenceQuery Query,
    IReadOnlyList<UsageHistoryBucket> UsageHistory,
    IReadOnlyList<UsageDimensionTotal> Dimensions,
    IReadOnlyList<UsageHeatmapCell> Heatmap,
    IReadOnlyList<QuotaBurnInterval> QuotaBurnIntervals,
    IReadOnlyList<QuotaResetEvent> ResetEvents,
    IReadOnlyList<ForecastSnapshot> FiveHourForecasts,
    IReadOnlyList<ForecastSnapshot> WeeklyForecasts);

public sealed record IntelligenceRefreshResult(
    int ForecastsPersisted,
    int ResetEventsDetected);
