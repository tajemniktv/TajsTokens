// Taj's Tokens | CodexTimestampProvenanceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexTimestampProvenanceTests
{
    private const string Owner = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset EventAt = DateTimeOffset.Parse("2026-09-01T10:01:00Z");
    private static readonly DateTimeOffset Captured = DateTimeOffset.Parse("2026-09-03T10:00:00Z");
    private const string Meta = """{"timestamp":"2026-09-01T10:00:00Z","type":"session_meta","payload":{"id":"11111111-1111-4111-8111-111111111111"}}""";

    [Theory]
    [InlineData("10:00")]
    [InlineData("2026-09-01")]
    [InlineData("2026-09-01T10:00:00")]
    [InlineData("2026-13-01T10:00:00Z")]
    [InlineData("")]
    public void PartialOrMalformedStringsCannotSupplyDatesOrLocalTimezone(string timestamp)
    {
        var parser = new CodexRolloutParser();
        var state = new RolloutParseState($"rollout-{Owner}.jsonl", "source");
        parser.Parse(new RawSessionRecord(state.FilePath, 0, 1, Meta), state);
        ParsedRolloutRecord parsed = parser.Parse(new RawSessionRecord(state.FilePath, 1, 2,
            TokenJson(100, "\"" + timestamp + "\"")), state);
        Assert.Null(parsed.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Malformed, parsed.TokenObservation.SourceTimestampQuality);
        Assert.Null(parsed.UsageEvent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumericUnixTimestampsRemainSupported(bool milliseconds)
    {
        var state = new RolloutParseState($"rollout-{Owner}.jsonl", "source");
        var parser = new CodexRolloutParser();
        parser.Parse(new RawSessionRecord(state.FilePath, 0, 1, Meta), state);
        long numeric = milliseconds ? EventAt.ToUnixTimeMilliseconds() : EventAt.ToUnixTimeSeconds();
        ParsedRolloutRecord parsed = parser.Parse(new RawSessionRecord(state.FilePath, 1, 2,
            TokenJson(100, numeric.ToString(System.Globalization.CultureInfo.InvariantCulture))), state);
        Assert.Equal(EventAt, parsed.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Valid, parsed.TimestampQuality);
    }

    [Fact]
    public async Task UnverifiedDatesCannotEnterHourlyIntelligenceOrForecastInputs()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-quality-guard-");
        try
        {
            string database = Path.Combine(directory.FullName, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            using var store = new SqliteCodexObservatoryStore(database);
            await store.InitializeAsync(default);
            await store.ApplyCumulativeTokenObservationAsync(Observation("unverified", 100, EventAt) with
            {
                SourceTimestampQuality = CodexSourceTimestampQuality.LegacyUnverified,
            }, default);
            await store.UpsertContextObservationAsync(new CodexContextObservation("ctx", "session", null,
                EventAt, "model", 100, 1000, true, 10)
            {
                SourceEventAtUtc = EventAt, SourceTimestampQuality = CodexSourceTimestampQuality.Malformed,
            }, default);
            Assert.Equal(100, (await store.GetSummaryAsync(default)).NativeTokens.ReportedTotal);
            CodexTokenAccountingSnapshot accounting = await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(default);
            Assert.Empty(accounting.Hourly);
            Assert.Null(Assert.Single(accounting.Usage).SourceEventAtUtc);
            CodexForecastDataset dataset = await new SqliteForecastDatasetReader(database).ReadAsync("codex", "default",
                EventAt.AddHours(-1), EventAt.AddHours(1), default, includeQuota: false, includeLedger: true);
            Assert.Empty(dataset.Tokens);
            Assert.Empty(dataset.Context);
            Assert.Empty(dataset.Ledger);
            IntelligenceDashboard dashboard = await new SqliteIntelligenceService(database, repository).QueryAsync(
                new IntelligenceQuery(EventAt.AddHours(-1), EventAt.AddHours(1)) { UsageOnly = true }, default);
            Assert.Empty(dashboard.UsageHistory);
            Assert.Empty(dashboard.Dimensions);
            Assert.Empty(dashboard.Heatmap);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public void MalformedRootUsesValidNestedTimestampAndUntimedMetadataHasNoTimeline()
    {
        var parser = new CodexRolloutParser();
        var state = new RolloutParseState($"rollout-{Owner}.jsonl", "source");
        ParsedRolloutRecord meta = parser.Parse(new RawSessionRecord(state.FilePath, 0, 1,
            Meta.Replace("\"timestamp\":\"2026-09-01T10:00:00Z\",", "")), state);
        Assert.True(state.OwnershipEstablished);
        Assert.Null(meta.Session);
        Assert.Null(meta.Agent);
        Assert.Null(meta.WorkloadObservation!.ObservedAtUtc);
        Assert.Equal(default, state.StartedAtUtc);

        foreach (string json in new[]
                 {
                     """{"type":"turn_context","payload":{"model":"synthetic-model"}}""",
                     """{"type":"compacted","payload":{}}""",
                     """{"type":"event_msg","payload":{"type":"task_started"}}""",
                     """{"type":"event_msg","payload":{"type":"task_complete"}}""",
                 })
        {
            ParsedRolloutRecord unknown = parser.Parse(new RawSessionRecord(state.FilePath, 1, 2, json), state);
            Assert.Null(unknown.UsageEvent);
            Assert.Null(unknown.Session);
            Assert.Null(unknown.Agent);
            Assert.Equal(CodexSourceTimestampQuality.Missing, unknown.TimestampQuality);
            if (unknown.ContextObservation is { } context) Assert.Null(context.SourceEventAtUtc);
        }

        string nested = TokenJson(100, "\"bad\"").Replace("\"type\":\"token_count\"",
            "\"timestamp\":\"2026-09-01T12:01:00+02:00\",\"type\":\"token_count\"");
        ParsedRolloutRecord known = parser.Parse(new RawSessionRecord(state.FilePath, 2, 3, nested), state);
        Assert.Equal(EventAt, known.TokenObservation!.SourceEventAtUtc);
        Assert.Equal(CodexSourceTimestampQuality.Valid, known.TimestampQuality);
        Assert.Equal(EventAt, known.Session!.StartedAtUtc);
    }

    [Fact]
    public void ExistingConstructorsKeepSourceTimeButCaptureWhenCreated()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        CodexTokenCountObservation observation = Observation("known", 100, EventAt);
        Assert.Equal(EventAt, observation.SourceEventAtUtc);
        Assert.InRange(observation.CapturedAtUtc, before, DateTimeOffset.UtcNow);

        var oldState = new CodexTokenCounterState("file", 0, 100, 0, 0, 0, 0, 100, "known", EventAt);
        var (_, _, _, _, _, _, _, _, _, oldObserved, _) = oldState;
        Assert.Equal(EventAt, oldObserved);
        Assert.Equal(EventAt, oldState.LastSourceEventAtUtc);
        Assert.Equal(CodexTokenAccountingDecisionKind.StaleObservation,
            CodexTokenCounterReducer.Reduce(Observation("stale", 200, EventAt.AddMinutes(-1)), oldState).Kind);
    }

    [Fact]
    public void UnknownCaptureNeverBecomesTheChronologicalWatermark()
    {
        var first = CodexTokenCounterReducer.Reduce(Observation("known", 100, EventAt), null);
        var unknown = CodexTokenCounterReducer.Reduce(Observation("unknown", 200, Captured) with
        {
            SourceEventAtUtc = null, CapturedAtUtc = Captured,
            SourceTimestampQuality = CodexSourceTimestampQuality.Missing,
        }, first.NextState);
        Assert.Equal(100, unknown.Delta.ReportedTotalTokens);
        Assert.Equal(EventAt, unknown.NextState!.LastSourceEventAtUtc);
        var repeated = CodexTokenCounterReducer.Reduce(Observation("repeat", 200, Captured) with
        {
            SourceEventAtUtc = null, SourceTimestampQuality = CodexSourceTimestampQuality.Missing,
        }, unknown.NextState);
        Assert.Equal(0, repeated.Delta.ReportedTotalTokens);
        var later = CodexTokenCounterReducer.Reduce(Observation("later", 300, EventAt.AddMinutes(1)), repeated.NextState);
        Assert.Equal(100, later.Delta.ReportedTotalTokens);
        Assert.Equal(300, later.NextState!.TotalTokens);
        Assert.Equal(0, later.NextState.Epoch);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task VerifiedUpgradePreservesOldOccurrencesCaptureAndTotalsAndRepairsOnlySourceProvenance(bool batch, bool legacyIdentity)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-time-upgrade-");
        try
        {
            string database = Path.Combine(directory.FullName, "telemetry.db");
            string file = Path.Combine(directory.FullName, $"rollout-{Owner}.jsonl");
            string[] lines = [Meta, TokenJson(100, "\"2026-09-01T10:01:00Z\""), TokenJson(200, null),
                """{"timestamp":"bad","type":"compacted","payload":{}}"""];
            string bytes = string.Join('\n', lines) + "\n";
            await File.WriteAllTextAsync(file, bytes);
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            using var store = new SqliteCodexObservatoryStore(database);
            await store.InitializeAsync(default);
            string identity = legacyIdentity ? "fallback:0000000000000123" : CodexSessionIngestionService.GetSourceIdentity(file);
            var state = new RolloutParseState(file, identity);
            var parser = new CodexRolloutParser();
            long offset = 0;
            var records = new List<ParsedRolloutRecord>();
            foreach (string line in lines)
            {
                long next = offset + Encoding.UTF8.GetByteCount(line + "\n");
                ParsedRolloutRecord record = parser.Parse(new RawSessionRecord(file, offset, next, line), state);
                if (record.TokenObservation is { } token) record = record with
                {
                    TokenObservation = token with { CapturedAtUtc = Captured, SourceEventAtUtc = null,
                        SourceTimestampQuality = CodexSourceTimestampQuality.LegacyUnverified },
                };
                if (record.ContextObservation is { } context) record = record with
                {
                    ContextObservation = context with { CapturedAtUtc = Captured, SourceEventAtUtc = null,
                        SourceTimestampQuality = CodexSourceTimestampQuality.LegacyUnverified },
                };
                records.Add(record);
                offset = next;
            }
            await new SqliteCodexIngestionBatchWriter(database, store).WriteBatchAsync(identity, file, offset, records, default);
            // Reconstruct an older false-timed timeline projection for the same immutable record.
            await store.UpsertUsageEventAsync(new UsageEvent(records[^1].SourceRecordId, Owner, Captured,
                "compaction", "synthetic old projection", null), default);
            await repository.SaveCheckpointAsync(new FileIngestionCheckpoint(file, offset, Captured, Owner,
                "typed-v7-response-evidence", identity)
            {
                ConsumedPrefixSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bytes))),
            }, default);
            string beforeTokens = await Rows(database, "SELECT source_event_id,observed_at_utc,captured_at_utc,reported_total_tokens FROM codex_native_token_events ORDER BY source_event_id;");
            string beforeContexts = await Rows(database, "SELECT event_id,observed_at_utc,captured_at_utc FROM context_observations ORDER BY event_id;");
            var accounting = new SqliteNativeCodexAccountingProvider(database);
            Assert.Empty((await accounting.GetSnapshotAsync(default)).Hourly);

            CodexSessionIngestionService Ingestion() => new(new FileSystemCodexSessionEventProvider(), repository, store,
                batch ? new SqliteCodexIngestionBatchWriter(database, store) : null);
            CodexIngestionResult replay = await Ingestion().IngestAsync(file, default);
            Assert.Equal(identity, replay.SourceIdentity);
            Assert.Equal(lines.Length, replay.RecordsScanned);
            Assert.Equal(beforeTokens, await Rows(database, "SELECT source_event_id,observed_at_utc,captured_at_utc,reported_total_tokens FROM codex_native_token_events ORDER BY source_event_id;"));
            Assert.Equal(beforeContexts, await Rows(database, "SELECT event_id,observed_at_utc,captured_at_utc FROM context_observations ORDER BY event_id;"));
            Assert.DoesNotContain(await store.GetTimelineAsync(Owner, 20, default), x => x.EventId == records[^1].SourceRecordId);
            Assert.Equal(200, (await store.GetSummaryAsync(default)).NativeTokens.ReportedTotal);
            CodexTokenAccountingSnapshot refreshed = await accounting.GetSnapshotAsync(default);
            Assert.Equal(100, Assert.Single(refreshed.Hourly).Breakdown.Total);
            Assert.Null(Assert.Single(refreshed.Usage).SourceEventAtUtc);
            Assert.Equal(CodexSourceTimestampQuality.Mixed, Assert.Single(refreshed.Usage).SourceTimestampQuality);
            Assert.Equal(Captured, refreshed.AsOfUtc);

            CodexForecastDataset dataset = await new SqliteForecastDatasetReader(database).ReadAsync("codex", "default",
                EventAt.AddHours(-1), EventAt.AddHours(1), default, includeQuota: false, includeLedger: true);
            Assert.Equal(100, Assert.Single(dataset.Tokens).ReportedTotalTokens);
            Assert.Equal(EventAt, Assert.Single(dataset.Context).ObservedAtUtc);
            Assert.Single(dataset.Ledger);
            Assert.Equal(0, (await Ingestion().IngestAsync(file, default)).RecordsScanned);

            await File.AppendAllTextAsync(file, TokenJson(300, "\"2026-09-01T10:02:00Z\"") + "\n");
            Assert.Equal(1, (await Ingestion().IngestAsync(file, default)).RecordsScanned);
            Assert.Equal(300, (await store.GetSummaryAsync(default)).NativeTokens.ReportedTotal);
            Assert.Equal(0, (await Ingestion().IngestAsync(file, default)).RecordsScanned);

            // An actual prefix rewrite must replace the old projection on both storage paths.
            await File.WriteAllTextAsync(file, Meta + "\n" + TokenJson(50, "\"2026-09-01T10:03:00Z\"") + "\n");
            CodexIngestionResult replacement = await Ingestion().IngestAsync(file, default);
            Assert.NotEqual(identity, replacement.SourceIdentity);
            Assert.Equal(50, (await store.GetSummaryAsync(default)).NativeTokens.ReportedTotal);
            Assert.Equal(50, Assert.Single((await accounting.GetSnapshotAsync(default)).Hourly).Breakdown.Total);
            Assert.Equal(0, (await Ingestion().IngestAsync(file, default)).RecordsScanned);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    private static CodexTokenCountObservation Observation(string id, long total, DateTimeOffset at) =>
        new(id, "file", "session", null, at, "model", null,
            new CodexTokenUsageSnapshot(total, 0, 0, 0, 0, total),
            new CodexTokenUsageSnapshot(100, 0, 0, 0, 0, 100));

    private static string TokenJson(long total, string? timestamp) =>
        "{" + (timestamp is null ? "" : "\"timestamp\":" + timestamp + ",") +
        "\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":" + Usage(total) +
        ",\"last_token_usage\":" + Usage(Math.Min(100, total)) + "}}}";

    private static string Usage(long total) => $"{{\"input_tokens\":{total},\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"total_tokens\":{total}}}";

    private static async Task<string> Rows(string database, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return string.Join('\n', rows);
    }
}
