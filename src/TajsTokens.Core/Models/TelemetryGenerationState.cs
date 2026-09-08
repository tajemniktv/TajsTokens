using TajsTokens.Core.Enums;

namespace TajsTokens.Core.Models;

public enum TelemetryDataQuality
{
    Primary = 0,
    Fallback = 1
}

/// <summary>
/// Freshness for one quota lane. A provider response may refresh one supported lane while another
/// remains stale/unavailable, so quota health must not be collapsed into one process-wide boolean.
/// </summary>
public sealed record QuotaLaneState(
    QuotaWindowKind Kind,
    string Provider,
    string Profile,
    QuotaSnapshot? Snapshot,
    TelemetryHealthState State,
    DateTimeOffset? LastSuccessUtc = null,
    bool NotReportedByProvider = false)
{
    public bool IsFresh => State == TelemetryHealthState.Live;
}
/// <summary>
/// Structured provenance for the currently displayed token-accounting generation. Freshness,
/// fallback quality, local-only coverage and reconciliation status are independent dimensions.
/// </summary>
public sealed record TokenAccountingGenerationState(
    string Source,
    TelemetryHealthState State,
    TelemetryDataQuality Quality,
    string Coverage,
    DateTimeOffset? AsOfUtc,
    long? Revision,
    CodexAccountingReconciliation? Reconciliation = null,
    string? Diagnostic = null)
{
    public bool IsFresh => State == TelemetryHealthState.Live;
    public bool IsFallback => Quality == TelemetryDataQuality.Fallback;
}
