// Taj's Tokens | CodexReconciliationAudit.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Ingestion;

/// <summary>Read-only, aggregate-only research. Never supplies canonical accounting.</summary>
public static class CodexReconciliationAudit
{
    public static async Task<ReconciliationAuditReport> RunAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var reader = new FileSystemCodexSessionEventProvider();
        var parser = new CodexRolloutParser();
        var report = new ReconciliationAuditReport();
        var fingerprints = new Dictionary<string, int>();
        var sequences = new List<CodexSequenceComparison.Sequence>();
        foreach (string path in paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            report.FilesExamined++;
            try
            {
                var before = new FileInfo(path);
                long length = before.Length;
                DateTime written = before.LastWriteTimeUtc;
                var parseState = new RolloutParseState(path, path);
                var state = new ReconciliationExperimentState();
                var responses = new CodexResponseUsageComparison();
                var localFingerprints = new HashSet<string>();
                var sequence = new List<string>();
                long unownedRecords = 0;
                long unownedTokenRecords = 0;
                long observations = 0;
                await foreach (RawSessionRecord raw in reader.ReadNewJsonLinesAsync(path, 0, cancellationToken))
                {
                    if (raw.EndByteOffset > length) break;
                    ParsedRolloutRecord parsed = parser.Parse(raw, parseState);
                    if (parseState.OwnershipEstablished)
                        responses.Observe(raw.Payload, parseState.OwnSessionId!);
                    if (!parseState.OwnershipEstablished)
                    {
                        unownedRecords++;
                        if (parsed.EventClass == "token_count") unownedTokenRecords++;
                    }
                    if (parsed.TokenObservation is not { } row) continue;
                    observations++;
                    state.Apply(row);
                    // Same session/time/model/counters across physical files is a repeat candidate,
                    // not proof of common request identity. Hashes never leave this invocation.
                    string payload = JsonSerializer.Serialize(
                        new
                        {
                            row.SessionId,
                            row.ObservedAtUtc,
                            row.Model,
                            row.ReasoningEffort,
                            row.TotalTokenUsage,
                            row.LastTokenUsage,
                        });
                    localFingerprints.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
                    // Deliberately omit owner only for candidate classification, never accounting.
                    // Keep timestamp/model and complete category vectors: scalar coincidences are insufficient.
                    string comparable = JsonSerializer.Serialize(
                        new
                        {
                            row.ObservedAtUtc,
                            row.Model,
                            row.ReasoningEffort,
                            row.TotalTokenUsage,
                            row.LastTokenUsage,
                        });
                    sequence.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(comparable))));
                }
                var after = new FileInfo(path);
                if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
                {
                    report.ChangedFilesExcluded++;
                    continue;
                }
                report.StableFiles++;
                responses.Complete();
                foreach ((string key, long count) in responses.Counts)
                    report.ResponseComparison[key] = report.ResponseComparison.GetValueOrDefault(key) + count;
                report.PreOwnershipRecordsExcluded += unownedRecords;
                report.PreOwnershipTokenRecordsExcluded += unownedTokenRecords;
                if (!parseState.OwnershipEstablished)
                {
                    report.FilesWithoutEstablishedOwner++;
                    if (parseState.ExpectedSessionId is null) report.FilesWithoutFilenameOwner++;
                    report.TokenRecordsInUnownedFiles += unownedTokenRecords;
                }
                else if (unownedRecords > 0)
                {
                    report.FilesWithExcludedPrefix++;
                    report.TokenRecordsInExcludedPrefixes += unownedTokenRecords;
                }
                if (sequence.Count > 0) sequences.Add(new CodexSequenceComparison.Sequence(parseState.OwnSessionId, sequence));
                report.TokenObservations += observations;
                report.IncumbentTokens += state.IncumbentTotal;
                report.HighWatermarkTokens += state.ContainmentTotal;
                report.LineageTokens += state.LineageTotal;
                report.IncompleteCounterObservations += state.IncompleteCounters;
                report.TotalsOnlyDrops += state.TotalsOnlyDrops;
                report.EqualTotalCategoryChanges += state.EqualTotalCategoryChanges;
                foreach ((string key, ReconciliationTransitionTotals value) in state.Transitions)
                {
                    if (!report.Transitions.TryGetValue(key, out ReconciliationTransitionTotals? aggregate))
                        report.Transitions.Add(key, aggregate = new ReconciliationTransitionTotals());
                    aggregate.Add(value);
                }
                if (state.IncumbentTotal != state.ContainmentTotal || state.IncumbentTotal != state.LineageTotal)
                    report.DisagreeingFiles++;
                foreach (string fingerprint in localFingerprints)
                    fingerprints[fingerprint] = fingerprints.GetValueOrDefault(fingerprint) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Do not expose exception messages containing source paths or record content.
                report.FailedFilesExcluded++;
            }
        }
        report.CrossFileRepeatFingerprints = fingerprints.Count(x => x.Value > 1);
        report.ExtraFileOccurrences = fingerprints.Values.Sum(x => (long)Math.Max(0, x - 1));
        report.SequenceRelationships = CodexSequenceComparison.Compare(sequences);
        return report;
    }
}

public sealed class ReconciliationAuditReport
{
    public string Version => "reconciliation-audit/v6";
    public Dictionary<string, long> ResponseComparison { get; set; } = [];

    public string Interpretation => "Physical-file experiment, not canonical usage. Candidates use scalar counters; " +
                                    "cross-file matches are not request identity. Changed/unreadable/malformed files excluded. " +
                                    "Ordered owned-token sequences classify equal/prefix/divergent/non-prefix overlap candidates, not byte copies. " +
                                    "Pre-ownership records (including possible inherited history) remain excluded by the production parser. " +
                                    "Transition buckets describe pre-step scalar watermark relationships and usable snapshot presence, not lineage. " +
                                    "Below-watermark buckets distinguish falling, repeated and recovering totals against the previous complete snapshot. " +
                                    "Their token deltas partition physical-file totals, including agreeing transitions; differences can cancel. " +
                                    "Response comparison uses per-file thread/response keys and single pending records, breaks at lifecycle boundaries, " +
                                    "and compares vectors without claiming native pairing, complete coverage or category consistency. Counts overlap; " +
                                    "response records never add tokens and no identity values leave the audit. " +
                                    "No account attribution, deduplication, quota fit or production promotion.";

    public Dictionary<string, ReconciliationTransitionTotals> Transitions { get; set; } = [];
    public long PreOwnershipRecordsExcluded { get; set; }
    public long PreOwnershipTokenRecordsExcluded { get; set; }
    public int FilesWithoutEstablishedOwner { get; set; }
    public int FilesWithoutFilenameOwner { get; set; }
    public int FilesWithExcludedPrefix { get; set; }
    public long TokenRecordsInUnownedFiles { get; set; }
    public long TokenRecordsInExcludedPrefixes { get; set; }
    public CodexSequenceComparison.Summary? SequenceRelationships { get; set; }
    public int FilesExamined { get; set; }
    public int StableFiles { get; set; }
    public int ChangedFilesExcluded { get; set; }
    public int FailedFilesExcluded { get; set; }
    public long TokenObservations { get; set; }
    public long IncompleteCounterObservations { get; set; }
    public long TotalsOnlyDrops { get; set; }
    public long EqualTotalCategoryChanges { get; set; }
    public int DisagreeingFiles { get; set; }
    public long IncumbentTokens { get; set; }
    public long HighWatermarkTokens { get; set; }
    public long LineageTokens { get; set; }
    public int CrossFileRepeatFingerprints { get; set; }
    public long ExtraFileOccurrences { get; set; }
}

/// <summary>Content-free aggregate; no paths, identifiers, timestamps or payload samples.</summary>
public sealed class ReconciliationTransitionTotals
{
    public long Observations { get; set; }
    public long Disagreements { get; set; }
    public long IncumbentTokens { get; set; }
    public long HighWatermarkTokens { get; set; }
    public long LineageTokens { get; set; }

    public void Add(ReconciliationTransitionTotals other)
    {
        Observations += other.Observations;
        Disagreements += other.Disagreements;
        IncumbentTokens += other.IncumbentTokens;
        HighWatermarkTokens += other.HighWatermarkTokens;
        LineageTokens += other.LineageTokens;
    }
}

/// <summary>Serializable experimental state shared by corpus audit and restart fixtures.</summary>
public sealed class ReconciliationExperimentState
{
    public CodexTokenUsageSnapshot? PreviousCompleteSnapshot { get; set; }
    public long EqualTotalCategoryChanges { get; set; }
    public CodexTokenCounterState? Incumbent { get; set; }
    public long IncumbentTotal { get; set; }
    public long ContainmentTotal { get; set; }
    public long LineageTotal { get; set; }
    public long HighWatermark { get; set; }
    public long IncompleteCounters { get; set; }
    public long TotalsOnlyDrops { get; set; }
    public bool AmbiguousWithoutLast => TotalsOnlyDrops > 0;
    public List<long> Heads { get; set; } = [];
    public HashSet<string> Occurrences { get; set; } = [];
    public Dictionary<string, ReconciliationTransitionTotals> Transitions { get; set; } = [];

    public void Apply(CodexTokenCountObservation row)
    {
        bool cumulative = row.TotalTokenUsage is { IsComplete: true, IsNonNegative: true };
        bool last = row.LastTokenUsage is { IsComplete: true, IsNonNegative: true };
        string relationship = !cumulative ? "unusable-cumulative"
            : PreviousCompleteSnapshot is null ? "first-cumulative"
            : row.TotalTokens < HighWatermark ? "below-watermark/" +
                                                (row.TotalTokens < PreviousCompleteSnapshot.TotalTokens ? "falling"
                                                    : row.TotalTokens == PreviousCompleteSnapshot.TotalTokens ? "repeated" : "recovering")
            : row.TotalTokens == HighWatermark ? "at-watermark" : "above-watermark";
        string key = Occurrences.Contains(row.SourceEventId)
            ? "occurrence-retry"
            : relationship + (last ? "/usable-last" : "/unusable-last");
        long incumbent = IncumbentTotal;
        long containment = ContainmentTotal;
        long lineage = LineageTotal;
        ApplyCounters(row);
        long a = IncumbentTotal - incumbent;
        long b = ContainmentTotal - containment;
        long c = LineageTotal - lineage;
        if (!Transitions.TryGetValue(key, out ReconciliationTransitionTotals? aggregate))
            Transitions.Add(key, aggregate = new ReconciliationTransitionTotals());
        aggregate.Add(
            new ReconciliationTransitionTotals
            {
                Observations = 1,
                Disagreements = a != b || a != c ? 1 : 0,
                IncumbentTokens = a,
                HighWatermarkTokens = b,
                LineageTokens = c,
            });
    }

    private void ApplyCounters(CodexTokenCountObservation row)
    {
        bool duplicate = !Occurrences.Add(row.SourceEventId);
        CodexTokenAccountingDecision decision = CodexTokenCounterReducer.Reduce(row, Incumbent, duplicate);
        IncumbentTotal += decision.Delta.ReportedTotalTokens;
        if (decision.AdvanceState) Incumbent = decision.NextState;
        if (duplicate) return;
        if (row.TotalTokenUsage is not { IsComplete: true, IsNonNegative: true })
        {
            IncompleteCounters++;
            return;
        }
        long total = row.TotalTokens;
        if (PreviousCompleteSnapshot is { } previous && previous.TotalTokens == total && previous != row.TotalTokenUsage)
            EqualTotalCategoryChanges++;
        PreviousCompleteSnapshot = row.TotalTokenUsage;
        ContainmentTotal += Math.Max(0, total - HighWatermark);
        if (row.LastTokenUsage is { IsComplete: true, IsNonNegative: true, TotalTokens: { } last } && last <= total)
        {
            if (!Heads.Contains(total))
            {
                Heads.Remove(total - last);
                LineageTotal += last;
            }
        }
        else
        {
            if (total < HighWatermark) TotalsOnlyDrops++;
            LineageTotal += Math.Max(0, total - HighWatermark);
        }
        Heads.Remove(total);
        Heads.Add(total);
        if (Heads.Count > 32) Heads.RemoveAt(0);
        HighWatermark = Math.Max(HighWatermark, total);
    }
}