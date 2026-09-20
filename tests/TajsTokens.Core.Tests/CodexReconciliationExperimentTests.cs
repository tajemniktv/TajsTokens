// Taj's Tokens | CodexReconciliationExperimentTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using Xunit.Abstractions;
using ExperimentState = TajsTokens.Infrastructure.Ingestion.ReconciliationExperimentState;

#endregion

namespace TajsTokens.Core.Tests;

/// <summary>
///     Original research counter experiments, not ports of third-party algorithms. These scalar
///     baselines isolate assumptions; they do not establish canonical identity or change production.
/// </summary>
public sealed class CodexReconciliationExperimentTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-18T10:00:00Z");

    [Fact]
    public async Task CorpusAuditUsesOwnedPrefixAndKeepsCopiesWithoutLeakingIdentity()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        string directory = Path.Combine(root!.FullName, ".codex", "temp", "reconciliation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string id = "00000000-0000-0000-0000-000000000001";

            string Meta(string owner)
            {
                return JsonSerializer.Serialize(new { type = "session_meta", timestamp = Start, payload = new { id = owner } });
            }

            string Tokens(int total)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        type = "event_msg",
                        timestamp = Start,
                        payload = new
                        {
                            type = "token_count",
                            info = new
                            {
                                total_token_usage = new
                                {
                                    input_tokens = total,
                                    cached_input_tokens = 0,
                                    cache_write_input_tokens = 0,
                                    output_tokens = 0,
                                    reasoning_output_tokens = 0,
                                    total_tokens = total,
                                },
                            },
                        },
                    });
            }

            string source = Path.Combine(directory, $"rollout-{id}.jsonl");
            string copy = Path.Combine(directory, $"copy-{id}.jsonl");
            string broken = Path.Combine(directory, $"broken-{id}.jsonl");
            string unowned = Path.Combine(directory, "no-filename-owner.jsonl");
            string response = JsonSerializer.Serialize(
                new
                {
                    type = "token_usage_record",
                    payload = new
                    {
                        thread_id = id,
                        turn_id = "private-turn",
                        root_turn_id = "private-root",
                        session_id = "private-session",
                        response_id = "private-response",
                        usage = new
                        {
                            input_tokens = 100,
                            cached_input_tokens = 0,
                            output_tokens = 0,
                            reasoning_output_tokens = 0,
                            total_tokens = 100,
                        },
                    },
                });
            await File.WriteAllTextAsync(
                source,
                string.Join('\n', Meta("parent"), Tokens(900), Meta(id), response, Tokens(100), Tokens(20), Tokens(110)) + "\n");
            File.Copy(source, copy);
            await File.WriteAllTextAsync(broken, string.Join('\n', Meta(id), response, "{malformed}") + "\n");
            await File.WriteAllTextAsync(unowned, Tokens(700) + "\n");
            ReconciliationAuditReport report = await CodexReconciliationAudit.RunAsync([source, copy, source, broken, unowned]);
            Assert.Equal(4, report.FilesExamined);
            Assert.Equal(3, report.StableFiles);
            Assert.Equal(1, report.FailedFilesExcluded);
            Assert.Equal(6, report.TokenObservations);
            Assert.Equal(2, report.ResponseComparison["response-records"]);
            Assert.Equal(2, report.ResponseComparison["invalid-legacy-vector-pair"]);
            Assert.Equal(380, report.IncumbentTokens);
            Assert.Equal(220, report.HighWatermarkTokens);
            Assert.Equal(220, report.LineageTokens);
            Assert.Equal(3, report.CrossFileRepeatFingerprints);
            Assert.Equal(3, report.ExtraFileOccurrences);
            Assert.Equal(2, report.DisagreeingFiles);
            Assert.Equal(report.TokenObservations, report.Transitions.Values.Sum(x => x.Observations));
            Assert.Equal(report.IncumbentTokens, report.Transitions.Values.Sum(x => x.IncumbentTokens));
            Assert.Equal(report.HighWatermarkTokens, report.Transitions.Values.Sum(x => x.HighWatermarkTokens));
            Assert.Equal(report.LineageTokens, report.Transitions.Values.Sum(x => x.LineageTokens));
            Assert.Equal(2, report.Transitions["above-watermark/unusable-last"].Disagreements);
            Assert.Equal(5, report.PreOwnershipRecordsExcluded);
            Assert.Equal(3, report.PreOwnershipTokenRecordsExcluded);
            Assert.Equal(2, report.FilesWithExcludedPrefix);
            Assert.Equal(1, report.FilesWithoutEstablishedOwner);
            Assert.Equal(1, report.FilesWithoutFilenameOwner);
            Assert.Equal(1, report.TokenRecordsInUnownedFiles);
            Assert.Equal(2, report.TokenRecordsInExcludedPrefixes);
            Assert.Equal(1, report.SequenceRelationships!.EqualSequences);
            Assert.Equal(0, report.SequenceRelationships.CrossOwnerPairs);
            string serialized = JsonSerializer.Serialize(report);
            Assert.DoesNotContain(id, serialized);
            Assert.DoesNotContain(directory, serialized);
            Assert.DoesNotContain("private-response", serialized);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MissingCumulativeCountersAreNotAZeroWatermark()
    {
        var state = new ExperimentState();
        var row = new CodexTokenCountObservation(
            "last-only",
            "source",
            "session",
            null,
            Start,
            null,
            null,
            null,
            new CodexTokenUsageSnapshot(50, 0, 0, 0, 0, 50));
        state.Apply(row);
        Assert.Equal(50, state.IncumbentTotal);
        Assert.Equal(1, state.IncompleteCounters);
        Assert.Empty(state.Heads);
        Assert.Equal(0, state.LineageTotal);
        Assert.Equal(50, state.Transitions["unusable-cumulative/usable-last"].IncumbentTokens);
    }

    [Fact]
    public void EqualScalarTotalDoesNotHideCategoryChangesAcrossRestart()
    {
        var state = new ExperimentState();
        state.Apply(Row("source", 0, 100, null));
        state = JsonSerializer.Deserialize<ExperimentState>(JsonSerializer.Serialize(state))!;
        var reshuffled = new CodexTokenCountObservation(
            "source:1",
            "source",
            "session",
            null,
            Start.AddSeconds(1),
            "model",
            "effort",
            new CodexTokenUsageSnapshot(90, 0, 0, 10, 0, 100));
        state.Apply(reshuffled);
        state.Apply(reshuffled); // Retrying the same physical occurrence is not another change.
        Assert.Equal(1, state.EqualTotalCategoryChanges);
        Assert.Equal(100, state.HighWatermark);
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return ["monotonic totals", new long[] { 100, 120, 150 }, new long?[] { null, null, null }, 150L, 150L, 150L];
        yield return ["totals-only A/B/A (ambiguous)", new long[] { 100, 20, 110 }, new long?[] { null, null, null }, 190L, 110L, 110L];
        yield return ["A/B/A with last counters", new long[] { 100, 20, 110 }, new long?[] { 100, 20, 10 }, 130L, 110L, 130L];
        yield return ["A/B/repeated A snapshot", new long[] { 100, 20, 100 }, new long?[] { 100, 20, 100 }, 220L, 100L, 120L];
        yield return ["real reset with observed increments", new long[] { 100, 20, 30 }, new long?[] { 100, 20, 10 }, 130L, 100L, 130L];
        yield return ["identical counts do not prove identity", new long[] { 100, 100 }, new long?[] { 100, 100 }, 100L, 100L, 100L];
        yield return ["partial history with last", new long[] { 1000, 1100 }, new long?[] { 10, 20 }, 30L, 1100L, 30L];
    }

    [Fact]
    public void BelowWatermarkRecoveryIsNotAnotherDropAcrossRestart()
    {
        var state = new ExperimentState();
        state.Apply(Row("source", 0, 100, 100));
        state.Apply(Row("source", 1, 20, 20));
        state = JsonSerializer.Deserialize<ExperimentState>(JsonSerializer.Serialize(state))!;
        state.Apply(Row("source", 2, 20, 20));
        state.Apply(Row("source", 3, 30, 10));
        ReconciliationTransitionTotals falling = state.Transitions["below-watermark/falling/usable-last"];
        ReconciliationTransitionTotals repeated = state.Transitions["below-watermark/repeated/usable-last"];
        ReconciliationTransitionTotals recovering = state.Transitions["below-watermark/recovering/usable-last"];
        Assert.Equal(1, falling.Observations);
        Assert.Equal(20, falling.IncumbentTokens);
        Assert.Equal(1, repeated.Observations);
        Assert.Equal(0, repeated.Disagreements);
        Assert.Equal(1, recovering.Observations);
        Assert.Equal(10, recovering.IncumbentTokens);
        Assert.Equal(0, recovering.HighWatermarkTokens);
        Assert.Equal(10, recovering.LineageTokens);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CompareAssumptionsAndEveryRestartBoundary(
        string name,
        long[] totals,
        long?[] last,
        long incumbent,
        long containment,
        long lineage)
    {
        CodexTokenCountObservation[] records = totals.Select((n, i) => Row("source-a", i, n, last[i])).ToArray();
        for (int restart = 0; restart <= records.Length; restart++)
        {
            var state = new ExperimentState();
            foreach (CodexTokenCountObservation row in records.Take(restart)) Apply(state, row);
            state = JsonSerializer.Deserialize<ExperimentState>(JsonSerializer.Serialize(state))!;
            foreach (CodexTokenCountObservation row in records.Skip(restart)) Apply(state, row);
            Assert.Equal(incumbent, state.IncumbentTotal);
            Assert.Equal(containment, state.ContainmentTotal);
            Assert.Equal(lineage, state.LineageTotal);
            Assert.Equal(records.Length, state.Transitions.Values.Sum(x => x.Observations));
            Assert.Equal(incumbent, state.Transitions.Values.Sum(x => x.IncumbentTokens));
            Assert.Equal(containment, state.Transitions.Values.Sum(x => x.HighWatermarkTokens));
            Assert.Equal(lineage, state.Transitions.Values.Sum(x => x.LineageTokens));
            if (name.Contains("ambiguous")) Assert.True(state.AmbiguousWithoutLast);
        }
        output.WriteLine(
            $"{name}: incumbent={incumbent}; high-watermark-only={containment}; bounded total-minus-last={lineage}. Not ground truth.");
    }

    [Fact]
    public void CopiedPhysicalSourcesRemainSeparateAndProcessingOrderDoesNotCanonicalizeThem()
    {
        CodexTokenCountObservation[] a = new[] { Row("original", 0, 100, 100), Row("original", 1, 120, 20) };
        CodexTokenCountObservation[] copy = new[] { Row("copy", 0, 100, 100), Row("copy", 1, 120, 20) };
        foreach (IEnumerable<CodexTokenCountObservation> records in new[]
                 {
                     a.Concat(copy), copy.Concat(a), new[] { a[0], copy[0], a[1], copy[1] },
                 })
        {
            var states = new Dictionary<string, ExperimentState>();
            foreach (CodexTokenCountObservation row in records)
            {
                if (!states.TryGetValue(row.SourceFile, out ExperimentState? state))
                    states.Add(row.SourceFile, state = new ExperimentState());
                Apply(state, row);
            }
            Assert.Equal(240, states.Values.Sum(s => s.IncumbentTotal));
            Assert.Equal(240, states.Values.Sum(s => s.LineageTotal));
        }
        // Identical payloads could also be independent work. No cross-file equality deletion is proposed.
    }

    [Fact]
    public void BoundedLineageEvictionMakesOldReplayAmbiguousRatherThanProvingNewWork()
    {
        var state = new ExperimentState();
        for (int i = 1; i <= 33; i++) Apply(state, Row("source", i, i * 100, i * 100));
        Assert.Equal(32, state.Heads.Count);
        long before = state.LineageTotal;
        Apply(state, Row("source", 34, 100, 100));
        Assert.Equal(before + 100, state.LineageTotal);
        // This bounded heuristic cannot recognize the evicted replay: a concrete negative fixture.
    }

    [Fact]
    public void ExactOccurrenceRetryIsIdempotentButDistinctEqualCountersRemainAmbiguous()
    {
        var state = new ExperimentState();
        CodexTokenCountObservation first = Row("source", 0, 100, 100);
        Apply(state, first);
        Apply(state, first);
        Assert.Equal(100, state.IncumbentTotal);
        Assert.Equal(100, state.LineageTotal);
        Apply(state, Row("source", 1, 100, 100));
        Assert.Equal(100, state.LineageTotal);
        Assert.Equal(0, state.Transitions["occurrence-retry"].Disagreements);
        Assert.Equal(0, state.Transitions["occurrence-retry"].IncumbentTokens);
        // If independent native request IDs proved two requests, true workload would be 200.
        // Counters alone cannot resolve that alternative; this result is not a deduplication oracle.
    }

    private static CodexTokenCountObservation Row(string file, int sequence, long total, long? last)
    {
        return new CodexTokenCountObservation(
            file + ":" + sequence,
            file,
            "session",
            null,
            Start.AddSeconds(sequence),
            "model",
            "effort",
            new CodexTokenUsageSnapshot(total, 0, 0, 0, 0, total),
            last is { } n ? new CodexTokenUsageSnapshot(n, 0, 0, 0, 0, n) : null);
    }

    private static void Apply(ExperimentState state, CodexTokenCountObservation row)
    {
        state.Apply(row);
    }
}