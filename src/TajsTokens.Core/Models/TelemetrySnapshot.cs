using TajsTokens.Core.Enums;

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

    public QuotaLaneState? FindQuotaLane(QuotaSnapshot snapshot) =>
        QuotaLanes.FirstOrDefault(lane =>
            lane.Kind == snapshot.Kind &&
            string.Equals(lane.Provider, snapshot.Provider, StringComparison.Ordinal) &&
            string.Equals(lane.Profile, snapshot.Profile, StringComparison.Ordinal));

    public bool IsQuotaSnapshotFresh(QuotaSnapshot snapshot) =>
        QuotaLanes.Count == 0
            ? QuotaDataFresh
            : FindQuotaLane(snapshot)?.IsFresh == true;

    public CurrentQuotaForecast? FindCurrentForecast(QuotaSnapshot snapshot) =>
        CurrentForecasts.FirstOrDefault(item =>
            item.Current.Kind == snapshot.Kind &&
            string.Equals(item.Current.Provider, snapshot.Provider, StringComparison.Ordinal) &&
            string.Equals(item.Current.Profile, snapshot.Profile, StringComparison.Ordinal) &&
            item.Current.CapturedAtUtc == snapshot.CapturedAtUtc);
}
