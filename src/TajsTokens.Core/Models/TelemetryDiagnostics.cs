namespace TajsTokens.Core.Models;

/// <summary>Ephemeral presentation of the shared collector snapshot; not new source evidence.</summary>
public sealed record TelemetryDiagnosticEntry(string Name, string Status, string Summary, string Detail);

public sealed record TelemetryDiagnostics(
    string Summary,
    IReadOnlyList<TelemetryDiagnosticEntry> Sources,
    IReadOnlyList<TelemetryDiagnosticEntry> QuotaWindows);
