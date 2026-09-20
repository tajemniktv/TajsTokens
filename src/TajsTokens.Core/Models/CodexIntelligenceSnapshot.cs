using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record CodexSelection(DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    string? AccountKey = null, string? Model = null, string? Project = null, string? ThreadId = null)
{
    public bool HasWorkFilter => Model is not null || Project is not null || ThreadId is not null;
}

// Analytical projection of the existing canonical increments, not another durable accounting owner.
public sealed record CodexLedgerEntry(string ObservationId, string? SourceIdentity, string ThreadId,
    string Project, DateTimeOffset ObservedAtUtc, DateTimeOffset? CollectedAtUtc,
    string? AccountKey, AccountEvidenceClass Ownership, CodexPredictiveTokenEvent Workload);

public sealed record CodexLedgerBucket(DateTimeOffset StartUtc, long Tokens, long UncachedInput,
    long CacheRead, long CacheWrite, long Output, long Reasoning);
public sealed record CodexLedgerGroup(string Dimension, string Key, long Tokens, int Threads);
public sealed record CodexEvidenceHealth(string Source, string State, string Explanation);
public sealed record CodexInferenceManifest(string Id, IReadOnlyDictionary<string, string> Components,
    string Reproducibility);

public sealed record CodexIntelligenceSnapshot(DateTimeOffset AsOfUtc, CodexSelection Selection,
    IReadOnlyList<CodexLedgerEntry> Ledger, IReadOnlyList<CodexLedgerBucket> Timeline,
    IReadOnlyList<CodexLedgerGroup> Breakdown, IReadOnlyList<QuotaSnapshot> QuotaTimeline,
    IReadOnlyList<CurrentQuotaForecast> Current, TokenWorkloadForecast? Workload,
    ApiPriceWeight ApiEquivalent, IReadOnlyList<CodexServerObservation> ProviderEvidence,
    IReadOnlyList<CodexEvidenceHealth> EvidenceHealth, CodexInferenceManifest Manifest)
{
    public CodexCurrentState CurrentState { get; init; } = CodexCurrentState.Empty;
    public IReadOnlyList<CodexChatAccounting> Chats { get; init; } = [];
    public IReadOnlyList<Services.CodexPeriodReconciliation> HistoricalPeriods { get; init; } = [];
    public QuotaCostReport? Accounting { get; init; }
    public Services.CodexRegimeReport? Regime { get; init; }
}

public sealed record CodexChatAccounting(string ThreadId, long SelectedLocalTokens, string? AccountKey,
    DateTimeOffset CapturedAtUtc, DateTimeOffset? DataAsOfUtc, CodexTaskUsage Report, string Compatibility);
