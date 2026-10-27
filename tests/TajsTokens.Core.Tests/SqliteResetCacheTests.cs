using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteResetCacheTests
{
    [Fact]
    public async Task IncrementalTailsMatchReplayAndUnchangedRefreshWritesNothingAfterRestart()
    {
        using var f = new Fixture();
        await f.Repository.InitializeIntelligenceAsync(default);
        var rows = new List<QuotaSnapshot>();
        for (int i = 0; i < 30; i++)
        {
            QuotaSnapshot row = f.Quota(f.At.AddMinutes(i), i % 8 * 10) with
            {
                LimitId = i % 2 == 0 ? null : "codex",
                PlanType = i % 3 == 0 ? "pro" : null,
                SessionId = i % 2 == 0 ? "ignored-app-server-session" : null,
            };
            rows.Add(row);
            await f.Repository.UpsertQuotaSnapshotAsync(row, default);
            await f.Repository.RefreshQuotaResetEventsAsync(default);
            await AssertReplayAsync(f, rows);
        }
        await f.Execute("""
            CREATE TRIGGER reject_noop_reset_write BEFORE UPDATE ON quota_reset_cache_state
            BEGIN SELECT RAISE(ABORT,'unexpected unchanged cache write'); END;
            """);
        var restarted = new SqliteTelemetryRepository(f.Database, f.Clock);
        Assert.Equal(0, await restarted.RefreshQuotaResetEventsAsync(default));

        // The bounded reader must seek the app-owned expression index, not rank all history.
        string plan = await f.Text("""
            EXPLAIN QUERY PLAN SELECT captured_at_utc FROM quota_snapshots
            WHERE json_array(provider,profile,kind,source,account_key,limit_id,plan_type,session_id,window_minutes)='[]'
            ORDER BY captured_at_utc DESC LIMIT 512;
            """, 3);
        Assert.StartsWith("SEARCH quota_snapshots USING", plan);
        Assert.Contains("idx_quota_snapshots_reset_tail", plan);
        Assert.DoesNotContain("TEMP B-TREE", plan);
    }

    [Fact]
    public async Task BackfillConflictUpdateAndDeletionRebuildRatherThanRetainObsoletePairs()
    {
        using var f = new Fixture();
        await f.Repository.InitializeIntelligenceAsync(default);
        QuotaSnapshot first = f.Quota(f.At, 80), second = f.Quota(f.At.AddMinutes(1), 10);
        await f.Repository.UpsertQuotaSnapshotAsync(first, default);
        await f.Repository.UpsertQuotaSnapshotAsync(second, default);
        Assert.Equal(1, await f.Repository.RefreshQuotaResetEventsAsync(default));
        QuotaSnapshot conflict = second with { UsedPercent = 20, ObservationId = "alternative" };
        await f.Repository.UpsertQuotaSnapshotAsync(conflict, default);
        Assert.Equal("0", await f.Text("SELECT COUNT(*) FROM quota_reset_events;"));
        await f.Repository.RefreshQuotaResetEventsAsync(default);
        await AssertReplayAsync(f, [first, second, conflict]);
        await f.Execute("DELETE FROM quota_snapshots WHERE observation_id='alternative';");
        await f.Repository.RefreshQuotaResetEventsAsync(default);
        await AssertReplayAsync(f, [first, second]);
        await f.Execute("UPDATE quota_snapshots SET used_percent=85 WHERE used_percent=10;");
        await f.Repository.RefreshQuotaResetEventsAsync(default);
        await AssertReplayAsync(f, [first, second with { UsedPercent = 85 }]);
    }

    [Fact]
    public async Task FutureObservationsMatureWithoutNewWritesAndClockRollbackRequalifies()
    {
        using var f = new Fixture();
        f.Clock.Now = f.At;
        await f.Repository.InitializeIntelligenceAsync(default);
        QuotaSnapshot first = f.Quota(f.At, 80), second = f.Quota(f.At.AddMinutes(1), 10);
        await f.Repository.UpsertQuotaSnapshotAsync(first, default);
        await f.Repository.UpsertQuotaSnapshotAsync(second, default);
        Assert.Equal(0, await f.Repository.RefreshQuotaResetEventsAsync(default));
        f.Clock.Now = f.At.AddMinutes(2);
        Assert.Equal(1, await f.Repository.RefreshQuotaResetEventsAsync(default));
        await AssertReplayAsync(f, [first, second]);
        f.Clock.Now = f.At;
        await f.Repository.RefreshQuotaResetEventsAsync(default);
        await AssertReplayAsync(f, [first, second]);
    }

    [Fact]
    public async Task MoreThanOneTailOfPendingRowsRebuildsAndRetainsOlderResets()
    {
        using var f = new Fixture();
        await f.Repository.InitializeIntelligenceAsync(default);
        var rows = new List<QuotaSnapshot>();
        for (int i = 0; i < 600; i++)
        {
            var row = f.Quota(f.At.AddSeconds(i), i < 10 ? 80 : 10);
            rows.Add(row);
            await f.Repository.UpsertQuotaSnapshotAsync(row, default);
        }
        await f.Repository.RefreshQuotaResetEventsAsync(default);
        await AssertReplayAsync(f, rows);
        Assert.Equal("1", await f.Text("SELECT COUNT(*) FROM quota_reset_events;"));
        var append = f.Quota(f.At.AddSeconds(601), 12);
        rows.Add(append);
        await f.Repository.UpsertQuotaSnapshotAsync(append, default);
        await f.Repository.RefreshQuotaResetEventsAsync(default);
        await AssertReplayAsync(f, rows);
    }

    [Fact]
    public async Task FailedDerivedWriteRollsBackWatermarkAndRestartReplays()
    {
        using var f = new Fixture();
        await f.Repository.InitializeIntelligenceAsync(default);
        QuotaSnapshot first = f.Quota(f.At, 80), second = f.Quota(f.At.AddMinutes(1), 10);
        await f.Repository.UpsertQuotaSnapshotAsync(first, default);
        await f.Repository.UpsertQuotaSnapshotAsync(second, default);
        await f.Execute("""
            CREATE TRIGGER reject_reset_event BEFORE INSERT ON quota_reset_events
            BEGIN SELECT RAISE(ABORT,'injected derived-write failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => f.Repository.RefreshQuotaResetEventsAsync(default));
        Assert.Equal("2", await f.Text("SELECT pending_rows FROM quota_reset_cache_state;"));
        await f.Execute("DROP TRIGGER reject_reset_event;");
        var restarted = new SqliteTelemetryRepository(f.Database, f.Clock);
        Assert.Equal(1, await restarted.RefreshQuotaResetEventsAsync(default));
        await AssertReplayAsync(f, [first, second]);
    }

    private static async Task AssertReplayAsync(Fixture f, IReadOnlyList<QuotaSnapshot> rows)
    {
        var expected = new QuotaResetDetector().Detect(QuotaHistoryPolicy.ReplayRows(
            QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(rows, f.Clock.Now)).SelectMany(x => x)))
            .Select(item => item.EventId).Order().ToArray();
        string actual = await f.Text("SELECT event_id FROM quota_reset_events ORDER BY event_id;");
        Assert.Equal(string.Join("\n", expected), actual);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tajstokens-reset-cache-");
        public DateTimeOffset At { get; } = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        public Clock Clock { get; }
        public string Database { get; }
        public SqliteTelemetryRepository Repository { get; }
        public Fixture()
        {
            Database = Path.Combine(_directory.FullName, "data.db");
            Clock = new Clock { Now = At.AddDays(1) };
            Repository = new SqliteTelemetryRepository(Database, Clock);
        }
        public QuotaSnapshot Quota(DateTimeOffset at, double used) =>
            new(QuotaWindowKind.FiveHour, at, used, 300, At.AddHours(5), "codex", "default", "codex-app-server:codex", "account")
            { HasSourceTimestamp = true, CollectedAtUtc = At.AddDays(1) };
        public async Task Execute(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        public async Task<string> Text(string sql, int column = 0)
        {
            await using var connection = new SqliteConnection($"Data Source={Database}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await reader.ReadAsync()) values.Add(Convert.ToString(reader.GetValue(column))!);
            return string.Join("\n", values);
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); _directory.Delete(true); }
    }
}
