using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

/// <summary>Product current-status input, independent of collection triggers, logs and token generations.</summary>
public sealed record CodexCurrentState(DateTimeOffset CapturedAtUtc, IReadOnlyList<QuotaSnapshot> QuotaSnapshots,
    IReadOnlyList<QuotaLaneState> QuotaLanes, bool QuotaDataFresh, IReadOnlyList<ProviderHealthSnapshot> Sources)
{
    public static CodexCurrentState Empty { get; } = new(DateTimeOffset.MinValue, [], [], false, []);
    public QuotaLaneState? FindQuotaLane(QuotaSnapshot snapshot) => QuotaLanes.FirstOrDefault(lane =>
        lane.Kind == snapshot.Kind && lane.Provider == snapshot.Provider && lane.Profile == snapshot.Profile);
    public bool IsQuotaSnapshotFresh(QuotaSnapshot snapshot) => QuotaLanes.Count == 0 ? QuotaDataFresh :
        FindQuotaLane(snapshot) is { IsFresh: true } lane && lane.Snapshot == snapshot;
}
