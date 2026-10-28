using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace TajsTokens.Core.Tests;

public sealed class CompressedRolloutIngestionTests : IDisposable
{
    private const string Name = "rollout-2026-08-31T00-00-00-11111111-1111-4111-8111-111111111111.jsonl";
    private const string Extra = "{\"timestamp\":\"2026-08-31T00:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":0,\"cache_write_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0,\"total_tokens\":100}}}}\n";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Compression", Guid.NewGuid().ToString("N"));
    private readonly string _database;
    private readonly SqliteTelemetryRepository _repository;
    private readonly SqliteCodexObservatoryStore _store;

    public CompressedRolloutIngestionTests()
    {
        Directory.CreateDirectory(_directory);
        _database = Path.Combine(_directory, "telemetry.db");
        _repository = new(_database);
        _store = new(_database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainCompressedBothAndRestoredAppendPreserveOccurrenceAndOriginalCapture(bool batch)
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = await Fixture();
        await File.WriteAllBytesAsync(file, bytes);
        var ingestion = await Ingestion(batch);
        CodexIngestionResult plain = await ingestion.IngestAsync(file, default);
        Dictionary<string, string> before = await NativeFacts();
        long total = await Total();
        await Compress(file + ".zst", bytes);
        CodexIngestionResult both = await ingestion.IngestAsync(file + ".zst", default);
        Assert.Equal(plain.SourceIdentity, both.SourceIdentity);
        Assert.Equal(0, both.RecordsScanned);
        Assert.Equal("plain", (await _repository.GetCheckpointAsync(file, default))!.PhysicalEncoding);
        File.Delete(file);
        CodexIngestionResult compressed = await ingestion.IngestAsync(file, default);
        Assert.Equal(plain.SourceIdentity, compressed.SourceIdentity);
        Assert.Equal(0, compressed.RecordsScanned);
        Assert.Equal(bytes.Length, compressed.SourceLength);
        Assert.True(compressed.ReachedCurrentEndOfFile);
        Assert.Equal(new FileInfo(file + ".zst").Length, compressed.PhysicalSourceLength);
        FileIngestionCheckpoint checkpoint = (await _repository.GetCheckpointAsync(file, default))!;
        Assert.Equal("decoded-utf8-bytes-v1", checkpoint.CoordinateKind);
        Assert.Equal("zstandard", checkpoint.PhysicalEncoding);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), checkpoint.ConsumedPrefixSha256);
        Assert.True(await ingestion.IsUnchangedAsync(file + ".zst", default));
        AssertFacts(before, await NativeFacts());
        await File.WriteAllBytesAsync(file, bytes.Concat(Encoding.UTF8.GetBytes(Extra)).ToArray());
        var restarted = await Ingestion(batch);
        CodexIngestionResult resumed = await restarted.IngestAsync(file, default);
        Assert.Equal(plain.SourceIdentity, resumed.SourceIdentity);
        Assert.Equal(1, resumed.RecordsScanned);
        Assert.Equal(total + 100, await Total());
        AssertFacts(before, await NativeFacts());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(0, (await restarted.IngestAsync(file, default)).RecordsScanned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveMoveKeepsOriginalLogicalLocatorAndLaterRewriteRetiresAllPriorAmounts(bool batch)
    {
        string active = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "sessions", "2026")).FullName, Name);
        string archived = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "archived_sessions")).FullName, Name);
        byte[] bytes = await Fixture();
        await Compress(active + ".zst", bytes);
        var ingestion = await Ingestion(batch);
        CodexIngestionResult first = await ingestion.IngestAsync(active, default);
        Dictionary<string, string> before = await NativeFacts();
        long total = await Total();
        File.Move(active + ".zst", archived + ".zst");
        CodexIngestionResult moved = await ingestion.IngestAsync(archived + ".zst", default);
        Assert.Equal(first.SourceIdentity, moved.SourceIdentity);
        Assert.Equal(active, moved.LogicalSourcePath);
        Assert.Equal(0, moved.RecordsScanned);
        AssertFacts(before, await NativeFacts());
        await File.WriteAllBytesAsync(archived, bytes.Concat(Encoding.UTF8.GetBytes(Extra)).ToArray());
        CodexIngestionResult appended = await ingestion.IngestAsync(archived, default);
        Assert.Equal(first.SourceIdentity, appended.SourceIdentity);
        Assert.Equal(total + 100, await Total());
        AssertFacts(before, await NativeFacts());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        string replacement = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(archived)).Replace("fixture", "replacement", StringComparison.Ordinal);
        await File.WriteAllTextAsync(archived, replacement);
        CodexIngestionResult rewritten = await ingestion.IngestAsync(archived, default);
        Assert.NotEqual(first.SourceIdentity, rewritten.SourceIdentity);
        Assert.Equal(total + 100, await Total());
        Assert.Equal(total + 100, await Scalar("SELECT SUM(json_extract(evidence_json,'$.ReportedTotalTokens')) FROM codex_retired_numeric_observations WHERE observation_kind='native-token'"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Theory]
    [InlineData("truncated", CodexRolloutReadFailure.TruncatedCompressedFrame)]
    [InlineData("empty", CodexRolloutReadFailure.TruncatedCompressedFrame)]
    [InlineData("checksum", CodexRolloutReadFailure.InvalidCompressedFrame)]
    [InlineData("invalid", CodexRolloutReadFailure.InvalidCompressedFrame)]
    [InlineData("window", CodexRolloutReadFailure.UnsupportedWindow)]
    [InlineData("dictionary", CodexRolloutReadFailure.UnsupportedDictionary)]
    public async Task InvalidCompressedReplacementCannotModifySelectedEvidenceOrCheckpoint(string failure, CodexRolloutReadFailure expected)
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = await Fixture();
        await File.WriteAllBytesAsync(file, bytes);
        var ingestion = await Ingestion(true);
        await ingestion.IngestAsync(file, default);
        Dictionary<string, string> before = await NativeFacts();
        FileIngestionCheckpoint checkpoint = (await _repository.GetCheckpointAsync(file, default))!;
        await Compress(file + ".zst", bytes);
        byte[] encoded = await File.ReadAllBytesAsync(file + ".zst");
        encoded = failure switch
        {
            "truncated" => encoded[..^2],
            "empty" => [],
            "checksum" => encoded[..^1].Append((byte)(encoded[^1] ^ 0x80)).ToArray(),
            "invalid" => [1, 2, 3, 4, 5, 6],
            // Frame headers advertise a 64 MiB window or an unprovided dictionary.
            "window" => [0x28, 0xB5, 0x2F, 0xFD, 0, 0x80, 1, 0, 0],
            "dictionary" => [0x28, 0xB5, 0x2F, 0xFD, 1, 0, 42, 1, 0, 0],
            _ => throw new InvalidOperationException(),
        };
        await File.WriteAllBytesAsync(file + ".zst", encoded);
        File.Delete(file);
        CodexRolloutReadException error = await Assert.ThrowsAsync<CodexRolloutReadException>(() => ingestion.IngestAsync(file, default));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(checkpoint, await _repository.GetCheckpointAsync(file, default));
        AssertFacts(before, await NativeFacts());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task LegacyArchiveLocatorMismatchStillRetiresTheWholePriorGeneration()
    {
        string active = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "sessions")).FullName, Name);
        string archived = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "archived_sessions")).FullName, Name);
        byte[] bytes = await Fixture();
        await File.WriteAllBytesAsync(active, bytes);
        var ingestion = await Ingestion(true);
        CodexIngestionResult first = await ingestion.IngestAsync(active, default);
        FileIngestionCheckpoint checkpoint = (await _repository.GetCheckpointAsync(active, default))!;
        object? contextCount = await Scalar("SELECT COUNT(*) FROM context_observations");
        File.Move(active, archived);
        // Reproduce the old metadata-only move: active native rows kept their original safe
        // locator, while the file registry/checkpoint started pointing at the archive.
        await _store.UpsertRolloutFileAsync(first.SourceIdentity!, archived, first.SessionId, bytes.Length, DateTimeOffset.UtcNow, default);
        await _repository.SaveCheckpointAsync(checkpoint with
        {
            FilePath = archived, LogicalSourcePath = null, CoordinateKind = null,
            PhysicalEncoding = null, PhysicalSourceIdentity = null, UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, default);
        await ingestion.IngestAsync(archived, default);
        await File.WriteAllTextAsync(archived, Encoding.UTF8.GetString(bytes).Replace("fixture", "replacement", StringComparison.Ordinal));
        await ingestion.IngestAsync(archived, default);
        Assert.Equal(1650, await Total());
        Assert.Equal(1650L, await Scalar("SELECT SUM(json_extract(evidence_json,'$.ReportedTotalTokens')) FROM codex_retired_numeric_observations WHERE observation_kind='native-token'"));
        Assert.Equal(contextCount, await Scalar("SELECT COUNT(*) FROM codex_retired_numeric_observations WHERE observation_kind='context'"));
        Assert.Equal(contextCount, await Scalar("SELECT COUNT(*) FROM context_observations"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task MissingDuringOpenCannotReplaceSelectedEvidenceWithAnUnverifiedPhysicalIdentity()
    {
        string file = Path.Combine(_directory, Name);
        await File.WriteAllBytesAsync(file, await Fixture());
        var ingestion = await Ingestion(true);
        await ingestion.IngestAsync(file, default);
        var collector = new CodexObservatoryService(new MissingIngestion(), _store, [_directory]);
        CodexObservatoryRefreshResult result = await collector.RefreshAsync(default);
        Assert.Equal(1, result.ReadFailures[CodexRolloutReadFailure.MissingFile]);
        Assert.Equal(1650, await Total());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task ReadOnlyAuditUsesDecodedBoundsAndDoesNotCountBothSiblings()
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = await Fixture();
        await File.WriteAllBytesAsync(file, bytes);
        ReconciliationAuditReport plain = await CodexReconciliationAudit.RunAsync([file]);
        await Compress(file + ".zst", bytes);
        ReconciliationAuditReport both = await CodexReconciliationAudit.RunAsync([file, file + ".zst"]);
        File.Delete(file);
        ReconciliationAuditReport compressed = await CodexReconciliationAudit.RunAsync([file]);
        Assert.Equal(1, both.FilesExamined);
        Assert.Equal(1, compressed.StableFiles);
        Assert.Equal(plain.TokenObservations, both.TokenObservations);
        Assert.Equal(plain.TokenObservations, compressed.TokenObservations);
        Assert.Equal(plain.IncumbentTokens, compressed.IncumbentTokens);
        Assert.Equal(plain.HighWatermarkTokens, compressed.HighWatermarkTokens);
        Assert.False(File.Exists(_database));
    }

    [Fact]
    public async Task LegacyPlainByteCheckpointMigratesAndAdoptsCompressedSiblingWithoutRestamping()
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = await Fixture();
        await File.WriteAllBytesAsync(file, bytes);
        var ingestion = await Ingestion(true);
        CodexIngestionResult original = await ingestion.IngestAsync(file, default);
        Dictionary<string, string> before = await NativeFacts();
        await Execute("""
            ALTER TABLE ingestion_checkpoints DROP COLUMN coordinate_kind;
            ALTER TABLE ingestion_checkpoints DROP COLUMN logical_source_path;
            ALTER TABLE ingestion_checkpoints DROP COLUMN physical_encoding;
            ALTER TABLE ingestion_checkpoints DROP COLUMN physical_source_identity;
            PRAGMA user_version=15;
            """);
        await Compress(file + ".zst", bytes);
        File.Delete(file);
        var migratedRepository = new SqliteTelemetryRepository(_database);
        await migratedRepository.InitializeAsync(default);
        var migrated = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), migratedRepository, _store,
            new SqliteCodexIngestionBatchWriter(_database, _store));
        CodexIngestionResult adopted = await migrated.IngestAsync(file, default);
        Assert.Equal(original.SourceIdentity, adopted.SourceIdentity);
        Assert.Equal(0, adopted.RecordsScanned);
        AssertFacts(before, await NativeFacts());
        Assert.Equal("decoded-utf8-bytes-v1", (await migratedRepository.GetCheckpointAsync(file, default))!.CoordinateKind);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task CompressedInheritedPrefixDoesNotMergeTheChildWithItsParent()
    {
        string root = Path.Combine(_directory, Name);
        string child = Path.Combine(_directory, "rollout-2026-08-31T00-00-00-22222222-2222-4222-8222-222222222222.jsonl");
        await Compress(root + ".zst", await Fixture());
        await Compress(child + ".zst", await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodexRollouts", "child-inherited-prefix.jsonl")));
        var ingestion = await Ingestion(true);
        CodexIngestionResult parentResult = await ingestion.IngestAsync(root, default);
        CodexIngestionResult childResult = await ingestion.IngestAsync(child, default);
        Assert.NotEqual(parentResult.SourceIdentity, childResult.SourceIdentity);
        Assert.Equal(1650L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events WHERE session_id='11111111-1111-4111-8111-111111111111'"));
        Assert.Equal(550L, await Scalar("SELECT SUM(reported_total_tokens) FROM codex_native_token_events WHERE session_id='22222222-2222-4222-8222-222222222222'"));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task EqualNamesInDifferentSourceRootsCannotAdoptEachOthersOccurrence()
    {
        string one = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "one", "sessions")).FullName, Name);
        string two = Path.Combine(Directory.CreateDirectory(Path.Combine(_directory, "two", "sessions")).FullName, Name);
        byte[] bytes = await Fixture();
        await Compress(one + ".zst", bytes);
        await Compress(two + ".zst", bytes);
        var ingestion = await Ingestion(true);
        CodexIngestionResult first = await ingestion.IngestAsync(one, default);
        CodexIngestionResult second = await ingestion.IngestAsync(two, default);
        Assert.NotEqual(first.SourceIdentity, second.SourceIdentity);
        Assert.StartsWith("rollout/v1:", first.SourceIdentity);
        Assert.Equal(two, second.LogicalSourcePath);
        Assert.True(second.RecordsScanned > 0);
    }

    [Fact]
    public async Task ValidEmptyReplacementSelectsAnEmptyGenerationAndPreservesPriorEvidence()
    {
        string file = Path.Combine(_directory, Name);
        await File.WriteAllBytesAsync(file, await Fixture());
        var ingestion = await Ingestion(true);
        await ingestion.IngestAsync(file, default);
        long total = await Total();
        await Compress(file + ".zst", []);
        File.Delete(file);
        CodexIngestionResult empty = await ingestion.IngestAsync(file, default);
        Assert.True(empty.ReachedCurrentEndOfFile);
        Assert.Equal(0, empty.RecordsScanned);
        Assert.Equal(0, await Total());
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
        Assert.Equal(total, await Scalar("SELECT SUM(json_extract(evidence_json,'$.ReportedTotalTokens')) FROM codex_retired_numeric_observations WHERE observation_kind='native-token'"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM rollout_files"));
    }

    [Fact]
    public async Task ConcatenatedAndSkippableFramesUseOriginalUtf8CrLfOffsetsAndKeepInterruptedTailPending()
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"label\":\"żółć\"}\r\n{}\r\n{\"partial\":true}");
        int split = Array.IndexOf(bytes, (byte)'\n') + 1;
        byte[] first = Compressed(bytes[..split]);
        byte[] last = Compressed(bytes[split..]);
        await File.WriteAllBytesAsync(file + ".zst", first.Concat(new byte[] { 0x50, 0x2A, 0x4D, 0x18, 0, 0, 0, 0 }).Concat(last).ToArray());
        var provider = new FileSystemCodexSessionEventProvider();
        var records = new List<RawSessionRecord>();
        await foreach (RawSessionRecord record in provider.ReadNewJsonLinesAsync(file, 0, default)) records.Add(record);
        Assert.Equal(2, records.Count);
        Assert.Equal(split, records[0].EndByteOffset);
        Assert.Equal(split, records[1].StartByteOffset);
        Assert.Equal("{\"label\":\"żółć\"}", records[0].Payload);
        var ingestion = await Ingestion(true);
        CodexIngestionResult result = await ingestion.IngestAsync(file, default);
        Assert.False(result.ReachedCurrentEndOfFile);
        Assert.False(await ingestion.IsUnchangedAsync(file, default));
        await Compress(file + ".zst", bytes.Append((byte)'\n').ToArray());
        Assert.True((await ingestion.IngestAsync(file, default)).ReachedCurrentEndOfFile);
    }

    [Fact]
    public async Task OpenedRepresentationRemainsStableAcrossAtomicReplacementAndReportsSelectionChange()
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = await Fixture();
        await Compress(file + ".zst", bytes);
        await using CodexRolloutSource source = (await CodexRolloutSource.OpenAsync(file, default))!;
        string replacement = file + ".replacement";
        await Compress(replacement, Encoding.UTF8.GetBytes("{}\n"));
        File.Move(file + ".zst", file + ".retained");
        File.Move(replacement, file + ".zst");
        await using Stream decoded = source.OpenDecodedStream();
        using var retained = new MemoryStream();
        await decoded.CopyToAsync(retained);
        Assert.Equal(bytes, retained.ToArray());
        Assert.Equal(CodexRolloutReadFailure.RepresentationChanged,
            Assert.Throws<CodexRolloutReadException>(source.EnsureSelected).Failure);
    }

    [Fact]
    public async Task FilesystemDiscoveryPrefersPlainAndReportsCorruptCompressedCoverage()
    {
        string sessions = Directory.CreateDirectory(Path.Combine(_directory, "sessions")).FullName;
        string file = Path.Combine(sessions, Name);
        byte[] bytes = await Fixture();
        await Compress(file + ".zst", bytes);
        await File.WriteAllTextAsync(Path.Combine(sessions, "not-a-rollout.jsonl.backup"), "{}\n");
        var ingestion = await Ingestion(true);
        var collector = new CodexObservatoryService(ingestion, _store, [sessions]);
        CodexObservatoryRefreshResult initial = await collector.RefreshAsync(default);
        Assert.Equal(1, initial.FilesDiscovered);
        Assert.Equal(0, initial.Errors);
        Assert.Equal(1650, await Total());
        Assert.Equal(0, (await collector.RefreshAsync(default)).FilesScanned);
        await File.WriteAllBytesAsync(file, bytes);
        await File.WriteAllBytesAsync(file + ".zst", [1, 2, 3, 4]);
        Assert.Equal(0, (await collector.RefreshAsync(default)).Errors); // Plain is authoritative.
        File.Delete(file);
        CodexObservatoryRefreshResult corrupt = await collector.RefreshAsync(default);
        Assert.Equal(1, corrupt.Errors);
        Assert.Equal(1, corrupt.ReadFailures[CodexRolloutReadFailure.InvalidCompressedFrame]);
        Assert.Equal(1650, await Total());
    }

    [Fact]
    public async Task Utf8BomRemainsInCoordinatesAndHashButDoesNotHideTheOwnerHeader()
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(await Fixture()).ToArray();
        await Compress(file + ".zst", bytes);
        var ingestion = await Ingestion(true);
        CodexIngestionResult result = await ingestion.IngestAsync(file, default);
        Assert.Equal(1650, await Total());
        Assert.Equal(bytes.Length, result.LastCompleteRecordOffset);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), (await _repository.GetCheckpointAsync(file, default))!.ConsumedPrefixSha256);
    }

    [Fact]
    public async Task CompleteWhitespaceLinesAdvanceTheDecodedCheckpointWithoutAddingRecords()
    {
        string file = Path.Combine(_directory, Name);
        byte[] bytes = (await Fixture()).Concat(Encoding.UTF8.GetBytes("\r\n  \r\n")).ToArray();
        await Compress(file + ".zst", bytes);
        var ingestion = await Ingestion(true);
        CodexIngestionResult result = await ingestion.IngestAsync(file, default);
        Assert.True(result.ReachedCurrentEndOfFile);
        Assert.Equal(bytes.Length, result.LastCompleteRecordOffset);
        Assert.True(await ingestion.IsUnchangedAsync(file, default));
        Assert.Equal(1650, await Total());
        Assert.Equal(new FileInfo(file + ".zst").Length, await Scalar("SELECT size_bytes FROM rollout_files"));
        byte[] appended = bytes.Concat(Encoding.UTF8.GetBytes(" \r\n")).ToArray();
        await Compress(file + ".zst", appended);
        await ingestion.IngestAsync(file, default);
        FileIngestionCheckpoint checkpoint = (await _repository.GetCheckpointAsync(file, default))!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(appended)), checkpoint.ConsumedPrefixSha256);
        Assert.Equal(0, (await (await Ingestion(true)).IngestAsync(file, default)).RecordsScanned);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task OversizedRecordIsRejectedBeforeAnyReplacementWrites()
    {
        string file = Path.Combine(_directory, Name);
        await File.WriteAllBytesAsync(file, await Fixture());
        var ingestion = await Ingestion(true);
        await ingestion.IngestAsync(file, default);
        FileIngestionCheckpoint checkpoint = (await _repository.GetCheckpointAsync(file, default))!;
        Dictionary<string, string> before = await NativeFacts();
        await using (var output = File.Create(file + ".zst"))
        await using (var compressed = new CompressionStream(output, 3, leaveOpen: true))
        {
            byte[] block = Enumerable.Repeat((byte)' ', 65536).ToArray();
            for (int i = 0; i < 1024; i++) await compressed.WriteAsync(block);
            await compressed.WriteAsync(new byte[] { (byte)' ' });
        }
        File.Delete(file);
        Assert.Equal(CodexRolloutReadFailure.RecordTooLarge,
            (await Assert.ThrowsAsync<CodexRolloutReadException>(() => ingestion.IngestAsync(file, default))).Failure);
        Assert.Equal(checkpoint, await _repository.GetCheckpointAsync(file, default));
        AssertFacts(before, await NativeFacts());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM codex_source_retirements"));
    }

    [Fact]
    public async Task CancelledDecodeDoesNotTouchTheDatabaseAndReleasesItsHandle()
    {
        string file = Path.Combine(_directory, Name);
        await Compress(file + ".zst", await Fixture());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), _repository, _store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingestion.IngestAsync(file, cancellation.Token));
        Assert.False(File.Exists(_database));
        File.Delete(file + ".zst");
    }

    private async Task<CodexSessionIngestionService> Ingestion(bool batch)
    {
        await _repository.InitializeAsync(default);
        await _store.InitializeAsync(default);
        return new(new FileSystemCodexSessionEventProvider(), _repository, _store,
            batch ? new SqliteCodexIngestionBatchWriter(_database, _store) : null);
    }
    private static async Task<byte[]> Fixture() => await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodexRollouts", "root-counter-reset.jsonl"));
    private static byte[] Compressed(byte[] bytes)
    {
        using var compressor = new Compressor(3);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor.Wrap(bytes).ToArray();
    }
    private static Task Compress(string file, byte[] bytes) => File.WriteAllBytesAsync(file, Compressed(bytes));
    private async Task<long> Total() => Convert.ToInt64(await Scalar("SELECT COALESCE(SUM(reported_total_tokens),0) FROM codex_native_token_events"));
    private async Task<object?> Scalar(string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<Dictionary<string, string>> NativeFacts()
    {
        await using var connection = new SqliteConnection("Data Source=" + _database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_event_id, json_array(uncached_input_tokens,cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,reasoning_output_tokens,reported_total_tokens,observed_at_utc,captured_at_utc) FROM codex_native_token_events";
        var result = new Dictionary<string, string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }
    private static void AssertFacts(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        foreach ((string id, string facts) in before) Assert.Equal(facts, after[id]);
    }
    private sealed class MissingIngestion : ICodexSessionIngestionService
    {
        public Task<CodexIngestionResult> IngestAsync(string filePath, CancellationToken cancellationToken)
            => Task.FromResult(CodexIngestionResult.MissingSource);
    }
    public void Dispose()
    {
        _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true);
    }
}
