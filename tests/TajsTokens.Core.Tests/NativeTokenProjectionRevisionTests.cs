using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Core.Tests;

public sealed class NativeTokenProjectionRevisionTests
{
    [Fact]
    public async Task ContextOnlyBatchReusesNumericProjectionsButAdvancesEvidenceGeneration()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync();
        var provider = new SqliteNativeCodexAccountingProvider(f.Database);
        var first = await provider.GetSnapshotAsync(default);
        var writer = new SqliteCodexIngestionBatchWriter(f.Database, f.Store);
        await writer.WriteBatchAsync("source", "rollout.jsonl", 20,
            [ParsedRolloutRecord.StorageOnly("context", "item_completed", 10, f.At, "session")], default);
        var changed = await provider.GetSnapshotAsync(default);
        Assert.True(changed.Revision > first.Revision);
        Assert.Same(first.Usage, changed.Usage);
        Assert.Same(first.Hourly, changed.Hourly);
        Assert.Same(changed, await provider.GetSnapshotAsync(default));
    }

    [Fact]
    public async Task UpdateDeleteAndRollbackMaintainProjectionAndChronology()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.AddAsync();
        var provider = new SqliteNativeCodexAccountingProvider(f.Database);
        var first = await provider.GetSnapshotAsync(default);
        await using var connection = new SqliteConnection($"Data Source={f.Database}");
        await connection.OpenAsync();
        using (var rollback = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = rollback;
            command.CommandText = "DELETE FROM codex_native_token_events;";
            await command.ExecuteNonQueryAsync();
            rollback.Rollback();
        }
        Assert.Same(first, await provider.GetSnapshotAsync(default));
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE codex_native_token_events SET model='changed', source_event_at_utc=$at;";
        update.Parameters.AddWithValue("$at", f.At.AddHours(2).ToString("O"));
        await update.ExecuteNonQueryAsync();
        var repaired = await provider.GetSnapshotAsync(default);
        Assert.Equal("changed", Assert.Single(repaired.Usage).Model);
        Assert.Equal(f.At.AddHours(2), Assert.Single(repaired.Hourly).StartUtc);
        Assert.Equal(first.AsOfUtc, repaired.AsOfUtc);
        Assert.Equal(110, Assert.Single(repaired.Usage).Breakdown.Total);
        update.CommandText = "UPDATE codex_native_token_events SET model=model;";
        update.Parameters.Clear();
        await update.ExecuteNonQueryAsync();
        Assert.Same(repaired, await provider.GetSnapshotAsync(default));
        update.CommandText = "DELETE FROM codex_native_token_events;";
        await update.ExecuteNonQueryAsync();
        var removed = await provider.GetSnapshotAsync(default);
        Assert.Empty(removed.Usage);
        Assert.Empty(removed.Hourly);
        Assert.True(removed.Revision > repaired.Revision);
    }

    [Fact]
    public async Task RestoredDatabaseWithEqualRevisionCannotReusePreviousTotals()
    {
        using var first = new Fixture();
        using var replacement = new Fixture();
        await first.InitializeAsync();
        await replacement.InitializeAsync();
        await first.AddAsync(110);
        await replacement.AddAsync(220);
        var provider = new SqliteNativeCodexAccountingProvider(first.Database);
        var before = await provider.GetSnapshotAsync(default);
        // Restore uses a different physical generation with the same monotonic revision values.
        SqliteConnection.ClearAllPools();
        File.Move(replacement.Database, first.Database, true);
        var after = await provider.GetSnapshotAsync(default);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(220, Assert.Single(after.Usage).Breakdown.Total);
    }

    [Fact]
    public async Task RolloutLastSeenCoalescesOnlyUnchangedMetadata()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.Store.UpsertRolloutFileAsync("source", "rollout.jsonl", "session", 10, f.At, default);
        await f.Store.UpsertRolloutFileAsync("source", "rollout.jsonl", "session", 10, f.At.AddMinutes(2), default);
        Assert.Equal(f.At, Assert.Single(await f.Store.GetRolloutStorageAsync(10, default)).LastSeenAtUtc);
        await f.Store.UpsertRolloutFileAsync("source", "rollout.jsonl", "session", 10, f.At.AddMinutes(6), default);
        Assert.Equal(f.At.AddMinutes(6), Assert.Single(await f.Store.GetRolloutStorageAsync(10, default)).LastSeenAtUtc);
        await f.Store.UpsertRolloutFileAsync("source", "rollout.jsonl", "session", 20, f.At.AddMinutes(6).AddSeconds(10), default);
        Assert.Equal(f.At.AddMinutes(6).AddSeconds(10), Assert.Single(await f.Store.GetRolloutStorageAsync(10, default)).LastSeenAtUtc);
    }

    [Fact]
    public async Task WarmIngestionDoesNotRestampCheckpointAndStillVerifiesChangedPrefix()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        string file = Path.Combine(f.Directory.FullName, "rollout.jsonl");
        await File.WriteAllTextAsync(file, "{}\n");
        var checkpoints = new CountingCheckpoints(f.Repository);
        var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), checkpoints);
        var first = await ingestion.IngestAsync(file, default);
        Assert.Equal(3, first.PrefixBytesHashed);
        Assert.Equal(3, first.CompleteRecordBytesDecoded);
        var checkpoint = await f.Repository.GetCheckpointAsync(file, default);
        var unchanged = await ingestion.IngestAsync(file, default);
        Assert.Equal(0, unchanged.PrefixBytesHashed);
        Assert.Equal(0, unchanged.CompleteRecordBytesDecoded);
        Assert.Equal(0, unchanged.CheckpointWrites);
        Assert.Equal(1, checkpoints.Writes);
        Assert.Equal(checkpoint, await f.Repository.GetCheckpointAsync(file, default));
        await File.WriteAllTextAsync(file, "[]\n{}\n");
        var rewritten = await ingestion.IngestAsync(file, default);
        Assert.Equal(6, rewritten.PrefixBytesHashed); // Verification and replay share the hash stream.
        Assert.Equal(6, rewritten.CompleteRecordBytesDecoded);
        Assert.NotEqual(first.SourceIdentity, rewritten.SourceIdentity);
        Assert.Equal(2, rewritten.RecordsScanned);
        Assert.Equal(2, checkpoints.Writes);
    }

    private sealed class CountingCheckpoints(ISessionIngestionCheckpointStore inner) : ISessionIngestionCheckpointStore
    {
        public int Writes { get; private set; }
        public Task<FileIngestionCheckpoint?> GetCheckpointAsync(string path, CancellationToken token) => inner.GetCheckpointAsync(path, token);
        public Task SaveCheckpointAsync(FileIngestionCheckpoint checkpoint, CancellationToken token)
        {
            Writes++;
            return inner.SaveCheckpointAsync(checkpoint, token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("tajstokens-token-revision-");
        public string Database => Path.Combine(Directory.FullName, "telemetry.db");
        public DateTimeOffset At { get; } = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        public SqliteTelemetryRepository Repository { get; }
        public SqliteCodexObservatoryStore Store { get; }
        public Fixture() { Repository = new(Database); Store = new(Database); }
        public async Task InitializeAsync() { await Repository.InitializeAsync(default); await Store.InitializeAsync(default); }
        public Task AddAsync(long total = 110) => Store.ApplyCumulativeTokenObservationAsync(
            new CodexTokenCountObservation("event", "rollout.jsonl", "session", null, At, "model", "high",
                total - 10, 20, 0, 10, 2, total), default);
        public void Dispose() { Store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(true); }
    }
}
