using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class TokenCounterEvidenceTests : IDisposable
{
    private static readonly DateTimeOffset EventAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajsTokens.CounterEvidence", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly SqliteCodexObservatoryStore _store;

    public TokenCounterEvidenceTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "telemetry.db");
        _store = new(_database);
    }

    [Fact]
    public async Task IncompleteInputsSurviveWithoutBecomingZeroOrAnAdditiveEvent()
    {
        var input = Observation("partial", new(null, 0, null, 4, null, null), null) with
        {
            SourceEventAtUtc = null, SourceTimestampQuality = CodexSourceTimestampQuality.Missing,
        };
        await _store.ApplyCumulativeTokenObservationAsync(input, default);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_native_token_events"));
        using JsonDocument evidence = JsonDocument.Parse((string)(await Scalar("SELECT evidence_json FROM codex_token_counter_observations"))!);
        JsonElement counters = evidence.RootElement.GetProperty("TotalTokenUsage");
        Assert.Equal(JsonValueKind.Null, counters.GetProperty("InputTokens").ValueKind);
        Assert.Equal(0, counters.GetProperty("CachedInputTokens").GetInt64());
        Assert.Equal(4, counters.GetProperty("OutputTokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("LastTokenUsage").ValueKind);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("Model").ValueKind);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("SourceEventAtUtc").ValueKind);
        Assert.Null(await Scalar("SELECT source_identity FROM codex_token_counter_observations"));
        Assert.DoesNotContain(_directory, (string)(await Scalar("SELECT source_file FROM codex_token_counter_observations"))!);
    }

    [Fact]
    public async Task ReplayRetainsBothSnapshotsAndFirstCaptureWithoutAddingUsage()
    {
        var total = new CodexTokenUsageSnapshot(100, 40, 0, 20, 5, 120);
        var last = new CodexTokenUsageSnapshot(10, 4, 0, 2, 1, 12);
        var input = Observation("repeat", total, last);
        await _store.ApplyCumulativeTokenObservationAsync(input, default);
        object? original = await Scalar("SELECT evidence_json FROM codex_token_counter_observations");
        object? amount = await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events");
        await _store.ApplyCumulativeTokenObservationAsync(input with { CapturedAtUtc = EventAt.AddDays(9) }, default);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"));
        Assert.Equal(original, await Scalar("SELECT evidence_json FROM codex_token_counter_observations"));
        Assert.Equal(amount, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
        Assert.Equal(Utc(input.CapturedAtUtc), await Scalar("SELECT input_first_captured_at_utc FROM codex_token_counter_observations"));
        using JsonDocument evidence = JsonDocument.Parse((string)original!);
        Assert.Equal(120, evidence.RootElement.GetProperty("TotalTokenUsage").GetProperty("TotalTokens").GetInt64());
        Assert.Equal(12, evidence.RootElement.GetProperty("LastTokenUsage").GetProperty("TotalTokens").GetInt64());
        Assert.Equal(6, evidence.RootElement.GetProperty("LastTokenUsage").EnumerateObject().Count());
    }

    [Fact]
    public async Task ConflictingInputRemainsAVariantWithoutOverwritingSelectedDelta()
    {
        await _store.ApplyCumulativeTokenObservationAsync(Observation("collision", new(100, 0, 0, 0, 0, 100), null), default);
        await _store.ApplyCumulativeTokenObservationAsync(Observation("collision", new(900, 0, 0, 0, 0, 900), null), default);
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"));
        Assert.Equal(100L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(DISTINCT interpretation_sha256) FROM codex_token_counter_observations"));
    }

    [Fact]
    public async Task MigrationDoesNotInventOriginalInputsAndReplayKeepsKnownOccurrenceTime()
    {
        var input = Observation("legacy", new(100, 0, 0, 0, 0, 100), null);
        await _store.ApplyCumulativeTokenObservationAsync(input, default);
        await Execute("DROP TABLE codex_token_counter_observations; UPDATE observatory_schema SET version=9 WHERE component='codex-observatory';");
        using var migrated = new SqliteCodexObservatoryStore(_database);
        await migrated.InitializeAsync(default);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"));
        Assert.Equal(100L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
        var recovered = input with { CapturedAtUtc = EventAt.AddDays(8) };
        await migrated.ApplyCumulativeTokenObservationAsync(recovered, default);
        Assert.Equal(Utc(input.CapturedAtUtc), await Scalar("SELECT occurrence_first_captured_at_utc FROM codex_token_counter_observations"));
        Assert.Equal(Utc(recovered.CapturedAtUtc), await Scalar("SELECT input_first_captured_at_utc FROM codex_token_counter_observations"));
        Assert.Equal(Utc(input.ObservedAtUtc), await Scalar("SELECT observed_at_utc FROM codex_token_counter_observations"));
        Assert.Equal(100L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
    }

    [Fact]
    public async Task CounterFailureRollsBackOriginalInputsAndReducerStateTogether()
    {
        await _store.InitializeAsync(default);
        await Execute("""
            CREATE TRIGGER fail_token BEFORE INSERT ON codex_native_token_events
            WHEN NEW.source_event_id = 'fail' BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => _store.ApplyCumulativeTokenObservationsAsync([
            Observation("ok", new(100, 0, 0, 0, 0, 100), null),
            Observation("fail", new(200, 0, 0, 0, 0, 200), null)], default));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_native_token_events"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_counter_state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenuineRewriteKeepsOriginalInputsWhileActiveGenerationCountsOnce(bool batch)
    {
        string file = Path.Combine(_directory, "rollout-2026-08-31T00-00-00-11111111-1111-4111-8111-111111111111.jsonl");
        string text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodexRollouts", "root-counter-reset.jsonl"));
        await File.WriteAllTextAsync(file, text);
        var repository = new SqliteTelemetryRepository(_database);
        await repository.InitializeAsync(default);
        var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), repository,
            _store, batch ? new SqliteCodexIngestionBatchWriter(_database, _store) : null);
        await ingestion.IngestAsync(file, default);
        long rows = (long)(await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"))!;
        long tokens = (long)(await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"))!;
        Assert.True(rows > 0);
        Assert.Equal(rows, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations WHERE source_identity IS NOT NULL"));
        object? firstCapture = await Scalar("SELECT MIN(input_first_captured_at_utc) FROM codex_token_counter_observations");
        // Different bytes in an already consumed prefix, same native quantities.
        await File.WriteAllTextAsync(file, text.Replace("C:\\\\fixture\\\\repo", "C:\\\\fixture\\\\changed", StringComparison.Ordinal));
        await ingestion.IngestAsync(file, default);
        Assert.Equal(rows * 2, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"));
        Assert.Equal(2L, await Scalar("SELECT COUNT(DISTINCT source_identity) FROM codex_token_counter_observations"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM rollout_files"));
        Assert.Equal(tokens, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events"));
        Assert.Equal(firstCapture, await Scalar("SELECT MIN(input_first_captured_at_utc) FROM codex_token_counter_observations"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_retired_source_generations"));
        Assert.Equal(tokens, await Scalar("SELECT SUM(json_extract(evidence_json,'$.ReportedTotalTokens')) FROM codex_retired_numeric_observations WHERE observation_kind='native-token'"));
        await ingestion.IngestAsync(file, default);
        Assert.Equal(rows * 2, await Scalar("SELECT COUNT(*) FROM codex_token_counter_observations"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    private CodexTokenCountObservation Observation(string id, CodexTokenUsageSnapshot? total, CodexTokenUsageSnapshot? last) =>
        new(id, Path.Combine(_directory, "source.jsonl"), "session", null, EventAt, null, null, total, last)
        { CapturedAtUtc = EventAt.AddDays(1) };

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

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
