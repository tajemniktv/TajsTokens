using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SupersededEvidenceRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajsTokens.RetiredEvidence", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly SqliteCodexObservatoryStore _store;

    public SupersededEvidenceRetentionTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "telemetry.db");
        _store = new(_database);
    }

    [Fact]
    public async Task ReplacementPreservesEveryNumericKindAndOriginalMissingnessWithoutCountingItAgain()
    {
        await Seed();
        await Retire();
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(7L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations"));
        Assert.Equal(6L, await Scalar("SELECT COUNT(DISTINCT observation_kind) FROM codex_retired_numeric_observations"));
        Assert.Equal("old", await Scalar("SELECT source_identity FROM codex_retired_source_generations"));
        Assert.Equal("new", await Scalar("SELECT replacement_source_identity FROM codex_source_retirements"));
        Assert.Equal(1234L, await Scalar("SELECT physical_size_bytes FROM codex_retired_source_generations"));
        Assert.Equal("superseded-source-identity", await Scalar("SELECT reason FROM codex_source_retirements"));

        using JsonDocument native = await Evidence("native-token");
        Assert.Equal(57, native.RootElement.GetProperty("ReportedTotalTokens").GetInt64());
        Assert.Equal(30, native.RootElement.GetProperty("UncachedInputTokens").GetInt64());
        Assert.Equal(20, native.RootElement.GetProperty("CacheReadTokens").GetInt64());
        Assert.Equal(2, native.RootElement.GetProperty("ReasoningOutputTokens").GetInt64());
        Assert.Equal("2026-10-01T10:00:00.0000000+00:00", native.RootElement.GetProperty("ObservedAtUtc").GetString());
        Assert.Equal("2026-10-02T10:00:00.0000000+00:00", native.RootElement.GetProperty("CapturedAtUtc").GetString());
        Assert.Equal(JsonValueKind.Null, native.RootElement.GetProperty("SourceEventAtUtc").ValueKind);
        Assert.Equal("Missing", native.RootElement.GetProperty("SourceTimestampQuality").GetString());
        Assert.Equal(JsonValueKind.Null, native.RootElement.GetProperty("Model").ValueKind);
        using JsonDocument counter = await Evidence("counter-state");
        Assert.Equal(90, counter.RootElement.GetProperty("TotalTokens").GetInt64());
        Assert.Equal(0, counter.RootElement.GetProperty("HasCumulativeBaseline").GetInt32());
        using JsonDocument workload = await Evidence("workload");
        Assert.Equal(33, workload.RootElement.GetProperty("DurationMilliseconds").GetInt64());
        Assert.Equal(13, workload.RootElement.GetProperty("TimeToFirstTokenMilliseconds").GetInt64());
        Assert.Equal(1024, workload.RootElement.GetProperty("StartByteOffset").GetInt64());
        Assert.Equal(JsonValueKind.Null, workload.RootElement.GetProperty("ObservedAtUtc").ValueKind);
        using JsonDocument context = await Evidence("context");
        Assert.Equal(JsonValueKind.Null, context.RootElement.GetProperty("InputTokens").ValueKind);
        Assert.Equal(100, context.RootElement.GetProperty("ContextWindowTokens").GetInt64());
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations WHERE observation_kind='quota' AND json_extract(evidence_json,'$.UsedPercent')=0"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations WHERE observation_kind='quota' AND json_type(evidence_json,'$.UsedPercent')='null'"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations WHERE observation_kind='quota' AND json_extract(evidence_json,'$.AccountKey')=''"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations WHERE evidence_json LIKE '%SECRET-MUST-NOT-RETAIN%'"));
        using JsonDocument usage = await Evidence("usage-timeline");
        Assert.False(usage.RootElement.TryGetProperty("Summary", out _));
        Assert.Equal(7, usage.RootElement.GetProperty("TokenDelta").GetDouble());

        // The unrelated source remains selected. No archived amount enters normal accounting.
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_native_token_events"));
        Assert.Equal("other-record", await Scalar("SELECT source_event_id FROM codex_native_token_events"));
        Assert.Equal(9L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM quota_snapshots"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_workload_observations"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM context_observations"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM usage_events"));
    }

    [Fact]
    public async Task RetirementPreservesExactStoredFloatingPointValues()
    {
        await Seed();
        await Execute("UPDATE quota_snapshots SET used_percent=12.345678901234567 WHERE observation_id='zero'; UPDATE usage_events SET token_delta=12.345678901234567;");
        double quota = (double)(await Scalar("SELECT used_percent FROM quota_snapshots WHERE observation_id='zero'"))!;
        double delta = (double)(await Scalar("SELECT token_delta FROM usage_events"))!;
        await Retire();
        using JsonDocument archivedQuota = JsonDocument.Parse((string)(await Scalar("SELECT evidence_json FROM codex_retired_numeric_observations WHERE observation_kind='quota' AND json_extract(evidence_json,'$.ObservationId')='zero'"))!);
        using JsonDocument archivedUsage = await Evidence("usage-timeline");
        Assert.Equal(quota, archivedQuota.RootElement.GetProperty("UsedPercent").GetDouble());
        Assert.Equal(delta, archivedUsage.RootElement.GetProperty("TokenDelta").GetDouble());
    }

    [Fact]
    public async Task SuccessfulRetirementIsIdempotentAndLaterSelectionsKeepEarlierHistory()
    {
        await Seed();
        await Retire();
        object? firstRetired = await Scalar("SELECT retired_at_utc FROM codex_source_retirements");
        await Retire();
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(7L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations"));
        Assert.Equal(firstRetired, await Scalar("SELECT retired_at_utc FROM codex_source_retirements"));
        await Retire("next");
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(7L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_retired_source_generations WHERE source_identity='old'"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreservationOrDeletionFailureRollsBackTheEntireRetirement(bool failPreservation)
    {
        await Seed();
        await Execute(failPreservation ? """
            CREATE TRIGGER fail_archive BEFORE INSERT ON codex_retired_numeric_observations
            WHEN NEW.observation_kind='quota' BEGIN SELECT RAISE(ABORT, 'fixture archive failure'); END;
            """ : """
            CREATE TRIGGER fail_delete BEFORE DELETE ON codex_native_token_events
            WHEN OLD.source_file='safe-old' BEGIN SELECT RAISE(ABORT, 'fixture deletion failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => Retire());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_retired_source_generations"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_native_token_events"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM quota_snapshots"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_workload_observations"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM rollout_files WHERE source_identity='old'"));
    }

    [Fact]
    public async Task UnknownLegacyGenerationAssociationIsRetainedWithoutInventingAnIdentity()
    {
        await Seed();
        await Execute("DELETE FROM rollout_records WHERE source_record_id='old-record';");
        await Retire();
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations WHERE observation_kind='native-token' AND source_identity IS NULL"));
        using JsonDocument native = await Evidence("native-token");
        Assert.Equal(57, native.RootElement.GetProperty("ReportedTotalTokens").GetInt64());
    }

    [Fact]
    public async Task MigrationKeepsExistingEvidenceAndDoesNotInventPastRetirements()
    {
        await Seed();
        await Execute("""
            DROP TABLE codex_retired_numeric_observations;
            DROP TABLE codex_retired_source_generations;
            DROP TABLE codex_source_retirements;
            UPDATE observatory_schema SET version=10 WHERE component='codex-observatory';
            """);
        using var migrated = new SqliteCodexObservatoryStore(_database);
        await migrated.InitializeAsync(default);
        Assert.Equal(11L, await Scalar("SELECT version FROM observatory_schema WHERE component='codex-observatory'"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_native_token_events"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM quota_snapshots"));
        await Retire();
        Assert.Equal(7L, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations"));
    }

    private async Task Seed()
    {
        await _store.InitializeAsync(default);
        await Execute("""
            INSERT INTO rollout_files VALUES('old','safe-old','session',1234,'2026-10-02T10:00:00Z');
            INSERT INTO rollout_files VALUES('other','safe-other','other-session',88,'2026-10-02T10:00:00Z');
            INSERT INTO rollout_records VALUES('old-record','old','safe-old','session','token_count',128,'2026-10-01T10:00:00Z');
            INSERT INTO codex_native_token_events(source_event_id,source_file,session_id,observed_at_utc,counter_epoch,
                uncached_input_tokens,cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,reasoning_output_tokens,
                reported_total_tokens,captured_at_utc,source_timestamp_quality)
            VALUES('old-record','safe-old','session','2026-10-01T10:00:00.0000000+00:00',2,30,20,0,5,2,57,'2026-10-02T10:00:00.0000000+00:00','Missing'),
                  ('other-record','safe-other','other-session','2026-10-01T10:00:00Z',0,9,0,0,0,0,9,'2026-10-02T10:00:00Z','Valid');
            INSERT INTO codex_counter_state(session_id,source_file,counter_epoch,input_tokens,cached_input_tokens,
                cache_write_input_tokens,output_tokens,reasoning_output_tokens,total_tokens,last_source_event_id,
                last_observed_at_utc,has_cumulative_baseline)
            VALUES('session','safe-old',2,80,30,0,10,2,90,'old-record','2026-10-01T10:00:00Z',0);
            INSERT INTO context_observations(event_id,session_id,observed_at_utc,context_window_tokens,is_compaction,record_bytes,captured_at_utc,source_timestamp_quality)
            VALUES('old-record:ctx','session','2026-10-01T10:00:00Z',100,0,128,'2026-10-02T10:00:00Z','Missing');
            INSERT INTO codex_workload_observations(source_record_id,source_identity,source_file,start_byte_offset,end_byte_offset,
                session_id,event_type,captured_at_utc,contract_version,duration_ms,time_to_first_token_ms)
            VALUES('old-record','old','safe-old',1024,2048,'session','task_complete','2026-10-02T10:00:00Z','codex-workload/v1',33,13);
            INSERT INTO quota_snapshots(provider,profile,kind,captured_at_utc,used_percent,window_minutes,source,
                observation_id,source_identity,session_id,collected_at_utc)
            VALUES('codex','default','FiveHour','2026-10-01T10:00:00Z',0,300,'codex-rollout:safe','zero','old','session','2026-10-02T10:00:00Z'),
                  ('codex','default','FiveHour','2026-10-01T10:00:00Z',NULL,300,'codex-rollout:safe','absent','old','session','2026-10-02T10:00:00Z');
            INSERT INTO usage_events VALUES('old-record:token','session','2026-10-01T10:00:00Z','token_count','SECRET-MUST-NOT-RETAIN',7);
            """);
    }

    private async Task Retire(string replacement = "new")
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        await SqliteCodexSourceGeneration.RetirePreviousAsync(connection, transaction, "safe-old", replacement, default);
        using var selected = connection.CreateCommand();
        selected.Transaction = transaction;
        selected.CommandText = "INSERT INTO rollout_files VALUES($id,'safe-old','session',500,'2026-10-03T10:00:00Z') ON CONFLICT(source_identity) DO NOTHING;";
        selected.Parameters.AddWithValue("$id", replacement);
        await selected.ExecuteNonQueryAsync();
        transaction.Commit();
    }

    private async Task<JsonDocument> Evidence(string kind) => JsonDocument.Parse((string)(await Scalar(
        "SELECT evidence_json FROM codex_retired_numeric_observations WHERE observation_kind='" + kind + "'"))!);

    private async Task<object?> Scalar(string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
