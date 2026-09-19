namespace TajsTokens.Core.Models;

/// <summary>Supplemental physical occurrence, never an additional accounting event.</summary>
public sealed record CodexResponseObservation(
    string SourceRecordId, string SourceIdentity, string SourceFile, long StartByteOffset, long EndByteOffset,
    string OwnerThreadId, DateTimeOffset? ObservedAtUtc, DateTimeOffset CapturedAtUtc,
    string? ReportedThreadId, string? TurnId, string? RootTurnId, string? RuntimeSessionId, string? ResponseId,
    CodexResponseSnapshot? Usage, CodexResponseSnapshot? TurnUsage, CodexResponseSnapshot? ThreadUsage,
    string Diagnostics, string ContractVersion = "codex-response/v1");

public sealed record CodexResponseSnapshot(CodexTokenUsageSnapshot Counters, bool CacheWriteDefaulted);
public sealed record CodexResponseEvidenceRow(CodexResponseObservation Observation, bool IsActive);
public sealed record CodexResponseEvidencePage(IReadOnlyList<CodexResponseEvidenceRow> Rows, bool Truncated, int CorruptRows)
{
    public int Active => Rows.Count(x => x.IsActive);
    public int Retired => Rows.Count - Active;
    public int WithDiagnostics => Rows.Count(x => x.Observation.Diagnostics.Length > 0);
    // Deliberately per physical source until a compatible source-root/account scope is established.
    public (int Repeated, int Conflicting) CompareActiveCandidates()
    {
        var repeated = 0; var conflicts = 0;
        foreach (var group in Rows.Where(x => x.IsActive && x.Observation.ResponseId is not null &&
                     x.Observation.TurnId is not null && x.Observation.RootTurnId is not null && x.Observation.RuntimeSessionId is not null &&
                     x.Observation.ReportedThreadId == x.Observation.OwnerThreadId)
                     .Select(x => x.Observation).GroupBy(x => (x.SourceIdentity, x.ReportedThreadId, x.ResponseId)))
        {
            // An absent cache-write counter has an explicit native zero default. Preserve that
            // provenance on the occurrence, but do not label it a numeric/lineage conflict.
            var variants = group.Select(x => (x.TurnId, x.RootTurnId, x.RuntimeSessionId,
                x.Usage?.Counters, x.TurnUsage?.Counters, x.ThreadUsage?.Counters)).Distinct().Count();
            if (variants > 1) conflicts++;
            else if (group.Count() > 1) repeated++;
        }
        return (repeated, conflicts);
    }
}
