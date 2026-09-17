using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaEvidencePersistenceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory;
    private string Database => Path.Combine(_directory, "telemetry.db");

    public QuotaEvidencePersistenceTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root is required.");
        _directory = Path.Combine(root.FullName, ".codex", "temp", "quota-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task AllWritersPreserveAlternativesProvenanceAndFirstCollectionOnRetry()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        using var store = new SqliteCodexObservatoryStore(Database);
        await store.InitializeAsync(default);
        var row = Point("r1", 12.375);
        await repository.UpsertQuotaSnapshotAsync(row, default);
        await store.UpsertQuotaSnapshotAsync(row with { CollectedAtUtc = Start.AddDays(2) }, default);
        var writer = new SqliteCodexIngestionBatchWriter(Database, store);
        var record = new ParsedRolloutRecord("r1", "token_count", 100, Start, "session",
            null, null, null, null, null, [row, Point("r2", 13)], null);
        await writer.WriteBatchAsync("generation", Path.Combine(_directory, "rollout.jsonl"), 100, [record], default);
        await writer.WriteBatchAsync("generation", Path.Combine(_directory, "rollout.jsonl"), 100, [record], default);
        var rows = await repository.GetRecentQuotaSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(row, rows.Single(x => x.ObservationId == "r1"));
        var data = await new SqliteForecastDatasetReader(Database).ReadAsync("codex", "default", Start.AddHours(-1), Start.AddHours(1), default);
        Assert.Equal(2, data.Quota.Count);
        Assert.All(QuotaHistoryPolicy.Describe(data.Quota, Start.AddHours(1)), x => Assert.Equal("same-time-conflict", x.Reason));
    }

    [Fact]
    public async Task CurrentSnapshotsAtSameTimePreserveConflictsInsteadOfOverwriting()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        var row = Point("ignored", 12) with { Source = "codex-app-server:codex", ObservationId = null,
            SourceIdentity = null, SessionId = null, AccountKey = "known" };
        await repository.UpsertQuotaSnapshotAsync(row, default);
        await repository.UpsertQuotaSnapshotAsync(row, default);
        await repository.UpsertQuotaSnapshotAsync(row with { UsedPercent = 14 }, default);
        var rows = await repository.GetRecentQuotaSnapshotsAsync(row.Kind, "codex", "default", 10, default, accountKey: "known");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 12d, 14d }, rows.Select(x => x.UsedPercent!.Value).Order());
    }

    [Fact]
    public async Task MigrationTenIsTransactionalAndDoesNotInventLegacyProvenance()
    {
        await Execute("""
            CREATE TABLE quota_snapshots(provider TEXT NOT NULL,profile TEXT NOT NULL,kind TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,used_percent REAL,window_minutes INTEGER,resets_at_utc TEXT,
                source TEXT NOT NULL,account_key TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(provider,profile,kind,captured_at_utc,source,account_key));
            INSERT INTO quota_snapshots VALUES('codex','default','FiveHour','2026-09-01T10:00:00.0000000+00:00',12.375,300,
                '2026-09-01T15:00:00.0000000+00:00','codex-rollout:primary','');
            CREATE TABLE quota_snapshots_v10(blocker INTEGER);
            PRAGMA user_version=9;
            """);
        var repository = new SqliteTelemetryRepository(Database);
        await Assert.ThrowsAsync<SqliteException>(() => repository.InitializeAsync(default));
        Assert.Equal(9L, await Scalar("PRAGMA user_version;"));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM quota_snapshots;"));
        await Execute("DROP TABLE quota_snapshots_v10;");
        await repository.InitializeAsync(default);
        await repository.InitializeAsync(default);
        Assert.Equal(10L, await Scalar("PRAGMA user_version;"));
        var row = Assert.Single(await repository.GetRecentQuotaSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default));
        Assert.Equal(12.375, row.UsedPercent);
        Assert.Null(row.CollectedAtUtc); Assert.Null(row.ObservationId); Assert.Null(row.LimitId);
        Assert.Equal("legacy-missing-provenance", Assert.Single(QuotaHistoryPolicy.Describe([row], Start)).Reason);
    }

    [Fact]
    public async Task ParserUpgradeInvalidatesOnlyOwnedAccelerationState()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        await repository.UpsertQuotaSnapshotAsync(Point("retained", 12), default);
        await new SqliteCodexStateIndexStore(Database).InitializeAsync(default);
        await Execute("""
            UPDATE codex_state_index_schema SET version=2;
            UPDATE codex_state_sync SET watermark_updated_at_ms=12345;
            INSERT INTO codex_state_thread_fingerprints VALUES('session',12345,100,NULL,NULL,0,'hash','2026-09-01');
            """);
        var index = new SqliteCodexStateIndexStore(Database);
        await index.InitializeAsync(default);
        Assert.Empty(await index.GetFingerprintsAsync(default));
        Assert.Equal(0, await index.GetWatermarkAsync(default));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM quota_snapshots;"));
        Assert.Equal(3L, await Scalar("SELECT version FROM codex_state_index_schema;"));
    }

    private async Task Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<object?> Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
    private static QuotaSnapshot Point(string id, double used) => new(QuotaWindowKind.FiveHour, Start,
        used, 300, Start.AddHours(5), "codex", "default", "codex-rollout:primary")
    {
        ObservationId = id, SourceIdentity = "generation", SessionId = "session", LimitId = "codex",
        PlanType = "pro", Lane = "primary", CollectedAtUtc = Start.AddDays(1)
    };
    public void Dispose()
    {
        using var connection = new SqliteConnection($"Data Source={Database}");
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_directory, recursive: true);
    }
}
