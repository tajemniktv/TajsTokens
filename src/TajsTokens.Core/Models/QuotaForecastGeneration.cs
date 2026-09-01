using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public enum QuotaObservationAuthority
{
    Unknown = 0,
    EmbeddedObservation = 1,
    ProviderAuthoritative = 2
}

/// <summary>
/// The current forecast for one quota lane, explicitly anchored to the quota observation that
/// defines the current provider meter. Historical observations may inform the slope but cannot
/// silently replace this anchor merely because they have a later timestamp.
/// </summary>
public sealed record CurrentQuotaForecast(
    QuotaSnapshot Current,
    TelemetryHealthState State,
    Forecast? Forecast,
    string HistoryPolicy,
    string? Diagnostic = null)
{
    public bool IsFresh => State == TelemetryHealthState.Live;
    public QuotaObservationAuthority Authority => Current.Authority;
}
