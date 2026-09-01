namespace TajsTokens.Core.Models;

/// <summary>
/// Persistable forecast result scoped to one provider/profile quota stream.
/// </summary>
public sealed record ForecastSnapshot(
    string Provider,
    string Profile,
    Forecast Forecast,
    string? QuotaSource = null,
    QuotaObservationAuthority QuotaAuthority = QuotaObservationAuthority.Unknown,
    DateTimeOffset? QuotaCapturedAtUtc = null,
    int? QuotaWindowMinutes = null,
    DateTimeOffset? QuotaResetsAtUtc = null);
