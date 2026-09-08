namespace TajsTokens.Core.Models;

public enum ForecastReplayAvailability
{
    ReconstructedEventTime,
    CollectedByOrigin
}

public sealed record CodexPredictiveTokenEvent(
    string SessionId, DateTimeOffset ObservedAtUtc, DateTimeOffset? CapturedAtUtc,
    string? Model, string? ReasoningEffort,
    long UncachedInputTokens, long CacheReadTokens, long CacheWriteTokens,
    long NonReasoningOutputTokens, long ReasoningOutputTokens, long ReportedTotalTokens);

public sealed record CodexForecastDataset(
    IReadOnlyList<QuotaSnapshot> Quota,
    IReadOnlyList<CodexWorkloadObservation> Workload,
    IReadOnlyList<CodexPredictiveTokenEvent> Tokens,
    IReadOnlyList<CodexContextObservation> Context,
    DateTimeOffset CapturedAtUtc,
    string Coverage);

/// <summary>Derived features known at one origin, not durable source observations.</summary>
public sealed record CodexForecastFeatures(
    DateTimeOffset OriginUtc,
    double LookbackHours,
    long Tokens,
    double? CacheReadShare,
    double? ReasoningOutputShare,
    int TokenActiveRootSessions,
    int TokenActiveSubagentSessions,
    int TokenActiveUnknownSessions,
    int ObservedOpenTurns,
    double CompletedTurnWallHours,
    int CompletedTurns,
    int Compactions,
    double? LastInputWindowRatio,
    IReadOnlyDictionary<string, double> ModelTokenShares,
    IReadOnlyDictionary<string, double> EffortTokenShares,
    ForecastReplayAvailability Availability,
    int PeakObservedTurnOverlap = 0,
    int ObservedTokenEvents = 0,
    int ObservedContextEvents = 0);
