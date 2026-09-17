using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public sealed record QuotaSnapshot(
    QuotaWindowKind Kind,
    DateTimeOffset CapturedAtUtc,
    double? UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAtUtc,
    string Provider,
    string Profile,
    string Source,
    string? AccountKey = null)
{
    // Optional native provenance. Null on legacy rows means unavailable, not inferred.
    public string? ObservationId { get; init; }
    public string? SourceIdentity { get; init; }
    public string? SessionId { get; init; }
    public string? LimitId { get; init; }
    public string? PlanType { get; init; }
    public string? Lane { get; init; }
    public DateTimeOffset? CollectedAtUtc { get; init; }
    public bool HasSourceTimestamp { get; init; }

    public double? RemainingPercent => UsedPercent is null
        ? null
        : Math.Clamp(100d - UsedPercent.Value, 0d, 100d);

    public QuotaObservationAuthority Authority => ClassifyAuthority(Source);

    public static QuotaObservationAuthority ClassifyAuthority(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return QuotaObservationAuthority.Unknown;
        }

        return source.Contains("app-server", StringComparison.OrdinalIgnoreCase)
            ? QuotaObservationAuthority.ProviderAuthoritative
            : source.Contains("rollout", StringComparison.OrdinalIgnoreCase)
                ? QuotaObservationAuthority.EmbeddedObservation
                : QuotaObservationAuthority.Unknown;
    }
}
