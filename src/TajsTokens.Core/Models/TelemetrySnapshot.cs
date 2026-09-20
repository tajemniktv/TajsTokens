// Taj's Tokens | TelemetrySnapshot.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;

#endregion

namespace TajsTokens.Core.Models;

public sealed record TelemetrySnapshot(
    DateTimeOffset CapturedAtUtc,
    RefreshTrigger Trigger,
    IReadOnlyList<TokenUsage> TokenUsages,
    IReadOnlyList<TokenTimeBucket> HourlyBuckets,
    IReadOnlyList<QuotaSnapshot> QuotaSnapshots,
    bool TokenDataFresh,
    bool QuotaDataFresh,
    bool PersistenceAvailable,
    IReadOnlyList<ProviderHealthSnapshot> Sources,
    IReadOnlyList<TelemetryRefreshEvent> Events)
{
    public IReadOnlyList<QuotaLaneState> QuotaLanes { get; init; } = [];
    public IReadOnlyList<CurrentQuotaForecast> CurrentForecasts { get; init; } = [];
    public TokenAccountingGenerationState? TokenGeneration { get; init; }
    public TokenWorkloadForecast? TokenForecast { get; init; }

    public static TelemetrySnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        RefreshTrigger.Startup,
        [],
        [],
        [],
        false,
        false,
        false,
        [],
        []);

    public bool HasAnyData => TokenUsages.Count > 0 || HourlyBuckets.Count > 0 || QuotaSnapshots.Count > 0;

    public QuotaLaneState? FindQuotaLane(QuotaSnapshot snapshot)
    {
        return QuotaLanes.FirstOrDefault(lane =>
            lane.Kind == snapshot.Kind &&
            string.Equals(lane.Provider, snapshot.Provider, StringComparison.Ordinal) &&
            string.Equals(lane.Profile, snapshot.Profile, StringComparison.Ordinal));
    }

    public bool IsQuotaSnapshotFresh(QuotaSnapshot snapshot)
    {
        return QuotaLanes.Count == 0
            ? QuotaDataFresh
            : FindQuotaLane(snapshot) is { IsFresh: true } lane && lane.Snapshot == snapshot;
    }

    // Match the complete observation, not merely its lane and capture time. Different sources
    // may report at the same instant, and changed reset/meter metadata is a different anchor.
    public CurrentQuotaForecast? FindCurrentForecast(QuotaSnapshot snapshot)
    {
        return CurrentForecasts.FirstOrDefault(item => item.Current == snapshot);
    }
}