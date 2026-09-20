// Taj's Tokens | CodexResponseEvidenceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexResponseEvidenceTests
{
    private const string Owner = "11111111-1111-4111-8111-111111111111";
    private static string Meta => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = Owner } });

    private static object Usage(long n)
    {
        return new
        {
            input_tokens = n,
            cached_input_tokens = 0,
            output_tokens = 0,
            reasoning_output_tokens = 0,
            total_tokens = n,
        };
    }

    private static string Response(string id = "response", string? thread = Owner)
    {
        return JsonSerializer.Serialize(
            new
            {
                type = "token_usage_record",
                payload = new
                {
                    thread_id = thread,
                    turn_id = "turn",
                    root_turn_id = "root",
                    session_id = "runtime-not-thread",
                    response_id = id,
                    usage = Usage(10),
                    turn_token_usage = Usage(20),
                    thread_token_usage = Usage(30),
                    content = "NEVER-RETAIN-ME",
                },
            });
    }

    [Fact]
    public void ParserPreservesSelectedSemanticsWithoutSourceContentOrSyntheticTime()
    {
        string file = $"rollout-{Owner}.jsonl";
        var parser = new CodexRolloutParser();
        var state = new RolloutParseState(file, "generation");
        Assert.Null(parser.Parse(new RawSessionRecord(file, 0, 1, Response()), state).ResponseObservation);
        parser.Parse(new RawSessionRecord(file, 1, 2, Meta), state);
        ParsedRolloutRecord parsed = parser.Parse(new RawSessionRecord(file, 2, 3, Response()), state);
        CodexResponseObservation row = parsed.ResponseObservation!;
        Assert.Null(parsed.TokenObservation);
        Assert.Null(row.ObservedAtUtc);
        Assert.Equal(Owner, row.OwnerThreadId);
        Assert.Equal("runtime-not-thread", row.RuntimeSessionId);
        Assert.Equal(10, row.Usage!.Counters.TotalTokens);
        Assert.Equal(20, row.TurnUsage!.Counters.TotalTokens);
        Assert.Equal(30, row.ThreadUsage!.Counters.TotalTokens);
        Assert.True(row.Usage.CacheWriteDefaulted);
        Assert.DoesNotContain("NEVER-RETAIN-ME", JsonSerializer.Serialize(row));
        string bad = Response(new string('x', 513), "other").Replace("\"input_tokens\":10", "\"input_tokens\":-1")
            .Replace("\"cached_input_tokens\":0", "\"cache_write_input_tokens\":null,\"cached_input_tokens\":0");
        CodexResponseObservation invalid = parser.Parse(new RawSessionRecord(file, 3, 4, bad), state).ResponseObservation!;
        Assert.Null(invalid.ResponseId);
        Assert.Null(invalid.Usage!.Counters.InputTokens);
        Assert.Null(invalid.Usage.Counters.CacheWriteInputTokens);
        Assert.False(invalid.Usage.CacheWriteDefaulted);
        Assert.Contains("owner-conflict", invalid.Diagnostics);
        Assert.Contains("input_tokens.invalid", invalid.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationReplayCopiesReplacementAndFailureKeepSupplementalEvidence(bool batch)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PROJECT.md"))) root = root.Parent;
        DirectoryInfo directory =
            Directory.CreateDirectory(Path.Combine(root!.FullName, ".codex", "temp", "response-" + Guid.NewGuid().ToString("N")));
        try
        {
            string db = Path.Combine(directory.FullName, "telemetry.db");
            string path = Path.Combine(directory.FullName, $"rollout-{Owner}.jsonl");
            var repository = new SqliteTelemetryRepository(db);
            await repository.InitializeAsync(default);
            var index = new SqliteCodexStateIndexStore(db);
            await index.InitializeAsync(default);
            await Sql(
                db,
                """
                INSERT INTO codex_state_thread_fingerprints VALUES('old',1,1,NULL,NULL,0,'hash','2026-01-01');
                UPDATE codex_state_sync SET watermark_updated_at_ms=123;
                UPDATE codex_state_index_schema SET version=5 WHERE component='codex-state-index';
                """);
            var upgradedIndex = new SqliteCodexStateIndexStore(db);
            await upgradedIndex.InitializeAsync(default);
            Assert.Empty(await upgradedIndex.GetFingerprintsAsync(default));
            Assert.Equal(0, await upgradedIndex.GetWatermarkAsync(default));
            using (var initial = new SqliteCodexObservatoryStore(db))
            {
                await initial.InitializeAsync(default);
            }
            await Sql(
                db,
                "DROP TABLE codex_response_observations; UPDATE observatory_schema SET version=7 WHERE component='codex-observatory';");
            using var store = new SqliteCodexObservatoryStore(db);
            await store.InitializeAsync(default);

            CodexSessionIngestionService Ingestion()
            {
                return new CodexSessionIngestionService(
                    new FileSystemCodexSessionEventProvider(),
                    repository,
                    store,
                    batch ? new SqliteCodexIngestionBatchWriter(db, store) : null);
            }

            CodexSessionIngestionService ingestion = Ingestion();
            string token = JsonSerializer.Serialize(
                    new
                    {
                        timestamp = "2026-01-01T00:00:00Z",
                        type = "event_msg",
                        payload = new { type = "token_count", info = new { total_token_usage = Usage(100) } },
                    })
                .Replace("\"input_tokens\":100", "\"cache_write_input_tokens\":0,\"input_tokens\":100");
            await File.WriteAllTextAsync(path, Meta + "\n" + Response() + "\n" + token + "\n");
            await Sql(
                db,
                "CREATE TRIGGER fail_response BEFORE INSERT ON codex_response_observations BEGIN SELECT RAISE(ABORT,'test'); END;");
            await Assert.ThrowsAsync<SqliteException>(() => ingestion.IngestAsync(path, default));
            Assert.Equal(0, await Scalar(db, "SELECT COUNT(*) FROM ingestion_checkpoints;"));
            Assert.Empty((await store.GetResponseEvidenceAsync(Owner, 10, default)).Rows);
            await Sql(db, "DROP TRIGGER fail_response;");
            DateTimeOffset before = DateTimeOffset.UtcNow;
            await ingestion.IngestAsync(path, default);
            CodexResponseObservation first = Assert.Single((await store.GetResponseEvidenceAsync(Owner, 10, default)).Rows).Observation;
            Assert.True(first.CapturedAtUtc >= before);
            Assert.Null(first.ObservedAtUtc);
            Assert.DoesNotContain(directory.FullName, first.SourceFile);
            string tokenIdentity = await TextScalar(db, "SELECT source_event_id || captured_at_utc FROM codex_native_token_events;");
            await Sql(db, "UPDATE ingestion_checkpoints SET parser_version='typed-v6-service-tier-evidence';");
            Assert.True((await Ingestion().IngestAsync(path, default)).RecordsScanned > 0);
            Assert.Equal(
                first.CapturedAtUtc,
                Assert.Single((await store.GetResponseEvidenceAsync(Owner, 10, default)).Rows).Observation.CapturedAtUtc);
            Assert.Equal(tokenIdentity, await TextScalar(db, "SELECT source_event_id || captured_at_utc FROM codex_native_token_events;"));
            Assert.Equal(1, await Scalar(db, "SELECT COUNT(*) FROM codex_native_token_events;"));
            await File.AppendAllTextAsync(path, Response() + "\n");
            await Ingestion().IngestAsync(path, default);
            CodexResponseEvidencePage repeated = await store.GetResponseEvidenceAsync(Owner, 10, default);
            Assert.Equal(2, repeated.Active);
            Assert.Equal((1, 0), repeated.CompareActiveCandidates());
            string copy = Path.Combine(directory.FullName, $"copy-{Owner}.jsonl");
            File.Copy(path, copy);
            await Ingestion().IngestAsync(copy, default);
            await File.WriteAllTextAsync(path, Meta + "\n" + Response("new") + "\n");
            await Ingestion().IngestAsync(path, default);
            CodexResponseEvidencePage replaced = await store.GetResponseEvidenceAsync(Owner, 10, default);
            Assert.Equal(5, replaced.Rows.Count);
            Assert.Equal(3, replaced.Active);
            Assert.Equal(2, replaced.Retired);
            Assert.True((await store.GetResponseEvidenceAsync(Owner, 1, default)).Truncated);
            Assert.True(await Scalar(db, "SELECT COUNT(*) FROM codex_native_token_events;") <= 2);
            Assert.Equal(0, await Scalar(db, "SELECT COUNT(*) FROM quota_snapshots;"));
            await Sql(db, "UPDATE codex_response_observations SET usage_snapshot='{}' WHERE response_id='new';");
            Assert.Equal(1, (await store.GetResponseEvidenceAsync(Owner, 10, default)).CorruptRows);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public void CandidateConflictsStayScopedAndRetiredOrIncompleteIdentityCannotClaimRepeats()
    {
        string file = $"rollout-{Owner}.jsonl";
        var state = new RolloutParseState(file, "one");
        var parser = new CodexRolloutParser();
        parser.Parse(new RawSessionRecord(file, 0, 1, Meta), state);
        CodexResponseObservation row = parser.Parse(new RawSessionRecord(file, 1, 2, Response()), state).ResponseObservation!;
        var page = new CodexResponseEvidencePage(
            [
                new CodexResponseEvidenceRow(row, true),
                new CodexResponseEvidenceRow(row with { SourceRecordId = "copy-occurrence", RuntimeSessionId = "different-runtime" }, true),
                new CodexResponseEvidenceRow(row with { SourceIdentity = "other-source" }, true),
                new CodexResponseEvidenceRow(row with { SourceIdentity = "other-source" }, false),
                new CodexResponseEvidenceRow(row with { ResponseId = "incomplete", RuntimeSessionId = null }, true),
                new CodexResponseEvidenceRow(row with { ResponseId = "incomplete", RuntimeSessionId = null }, true),
            ],
            false,
            0);
        Assert.Equal((0, 1), page.CompareActiveCandidates());
        Assert.Equal(1, page.Retired);
        CodexResponseObservation explicitZero = row with { Usage = row.Usage! with { CacheWriteDefaulted = false } };
        Assert.Equal(
            (1, 0),
            new CodexResponseEvidencePage(
                [new CodexResponseEvidenceRow(row, true), new CodexResponseEvidenceRow(explicitZero, true)],
                false,
                0).CompareActiveCandidates());
        CodexResponseObservation unknown = row with
        {
            Usage = row.Usage! with { Counters = row.Usage.Counters with { CacheWriteInputTokens = null } },
        };
        Assert.Equal(
            (0, 1),
            new CodexResponseEvidencePage([new CodexResponseEvidenceRow(row, true), new CodexResponseEvidenceRow(unknown, true)], false, 0)
                .CompareActiveCandidates());
    }

    private static async Task Sql(string db, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + db);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string db, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + db);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> TextScalar(string db, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + db);
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}