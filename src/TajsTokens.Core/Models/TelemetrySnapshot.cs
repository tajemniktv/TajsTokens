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
}
