using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class QuotaAccountScopeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory;
    private string Database => Path.Combine(_directory, "telemetry.db");

    public QuotaAccountScopeTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root is required for project-local test files.");
        _directory = Path.Combine(root.FullName, ".codex", "temp", "quota-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task UnknownSourceResetIdsRemainSeparateAndLegacyRetryIsIdempotent()
    {
        var a = Point(Now.AddHours(-1), 90, null);
        var b = a with { CapturedAtUtc = Now, UsedPercent = 5 };
        var events = new QuotaResetDetector().Detect([a, b,
            a with { Source = "other-source" }, b with { Source = "other-source" }]);
        Assert.Equal(2, events.Select(x => x.EventId).Distinct().Count());
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        foreach (var item in events) await repository.UpsertQuotaResetEventAsync(item, default);
        await ExecuteAsync("UPDATE quota_reset_events SET event_id='legacy-' || source; UPDATE intelligence_schema SET version=2 WHERE component='phase4-intelligence';");
        repository = new SqliteTelemetryRepository(Database);
        foreach (var item in events) await repository.UpsertQuotaResetEventAsync(item, default);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
    }

    [Fact]
    public async Task ResetIdentityMigrationRebuildsAllHistoryAtomicallyAndRetries()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeIntelligenceAsync(default);
        var first = Point(Now.AddDays(-10), 90, null) with { ResetsAtUtc = Now.AddDays(-9) };
        var second = first with { CapturedAtUtc = first.CapturedAtUtc.AddMinutes(1), UsedPercent = 5 };
        foreach (var plan in new[] { "a", "b" })
            foreach (var row in new[] { first, second })
                await repository.UpsertQuotaSnapshotAsync(row with { PlanType = plan }, default);
        for (var i = 0; i < 520; i++)
            await repository.UpsertQuotaSnapshotAsync(Point(Now.AddMinutes(-520 + i), 20, "recent") with
                { ResetsAtUtc = Now.AddHours(5).AddSeconds(i % 2) }, default);
        var legacy = Assert.Single(new QuotaResetDetector().Detect([first, second])) with { EventId = "legacy-event" };
        await repository.UpsertQuotaResetEventAsync(legacy, default);
        await ExecuteAsync("UPDATE intelligence_schema SET version=3; CREATE TRIGGER fail_reset_rebuild BEFORE INSERT ON quota_reset_events BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default));
        Assert.Equal(3L, await ScalarAsync("SELECT version FROM intelligence_schema"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events WHERE event_id='legacy-event'"));
        await ExecuteAsync("DROP TRIGGER fail_reset_rebuild;");
        await new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default);
        Assert.Equal(5L, await ScalarAsync("SELECT version FROM intelligence_schema"));
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events WHERE event_id NOT LIKE 'quota-reset-v2-%'"));
        Assert.Equal(524L, await ScalarAsync("SELECT COUNT(*) FROM quota_snapshots"));
        await new SqliteTelemetryRepository(Database).InitializeIntelligenceAsync(default);
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
    }

    [Fact]
    public async Task ResetCacheInvalidationRollsBackWithEvidenceAndRebuildFailureRemainsRetryable()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeIntelligenceAsync(default);
        var first = Point(Now.AddDays(-10), 90, "keep") with { ResetsAtUtc = Now.AddDays(-9) };
        await repository.UpsertQuotaSnapshotAsync(first, default);
        await repository.UpsertQuotaSnapshotAsync(first with { CapturedAtUtc = first.CapturedAtUtc.AddMinutes(1), UsedPercent = 5 }, default);
        await repository.UpsertQuotaSnapshotAsync(Point(Now, 10, "delete"), default);
        await repository.RefreshQuotaResetEventsAsync(default);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
        await ExecuteAsync("BEGIN; DELETE FROM quota_snapshots WHERE account_key='delete'; ROLLBACK;");
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
        Assert.Equal(0L, await ScalarAsync("SELECT needs_rebuild FROM quota_reset_cache_state"));
        // Push the surviving transition outside the bounded ordinary refresh.
        for (var i = 0; i < 520; i++)
            await repository.UpsertQuotaSnapshotAsync(Point(Now.AddMinutes(-520 + i), 20, "keep"), default);
        await ExecuteAsync("DELETE FROM quota_snapshots WHERE account_key='delete'; CREATE TRIGGER fail_refresh BEFORE INSERT ON quota_reset_events BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;");
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
        await Assert.ThrowsAsync<SqliteException>(() => repository.RefreshQuotaResetEventsAsync(default));
        Assert.Equal(1L, await ScalarAsync("SELECT needs_rebuild FROM quota_reset_cache_state"));
        await ExecuteAsync("DROP TRIGGER fail_refresh;");
        // A new repository simulates restarting after invalidation/failure.
        repository = new SqliteTelemetryRepository(Database);
        await repository.RefreshQuotaResetEventsAsync(default);
        // The old full reset plus the later window re-anchor both survive the full rebuild.
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events WHERE classification='FullReset'"));
        Assert.Equal(0L, await ScalarAsync("SELECT needs_rebuild FROM quota_reset_cache_state"));
        Assert.Equal(0, await repository.RefreshQuotaResetEventsAsync(default));
        Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM quota_reset_events"));
    }

    [Fact]
    public async Task ObservatoryFirstInitializationMigratesOldQuotaSchema()
    {
        await new SqliteTelemetryRepository(Database).InitializeAsync(default);
        await ExecuteAsync("DROP TABLE quota_snapshots; DROP TABLE forecast_snapshots; DROP TABLE ingestion_checkpoints; DROP TABLE codex_server_evidence;");
        await CreateVersionEightAsync(false);
        using var store = new SqliteCodexObservatoryStore(Database);
        await store.InitializeAsync(default);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name='account_key'"));
    }

    [Fact]
    public void ProviderKeepsOnlyStablePseudonymIncludingEmptyWindowResponses()
    {
        var first = Response("\"accountId\":\"sanitized-account-A\",", true);
        var empty = Response("\"accountId\":\"sanitized-account-A\",", false);
        var other = Response("\"accountId\":\"sanitized-account-B\",", true);
        Assert.StartsWith("codex-account-sha256/v1:", first.AccountKey);
        Assert.DoesNotContain("sanitized-account", first.AccountKey);
        Assert.Equal(first.AccountKey, empty.AccountKey);
        Assert.NotEqual(first.AccountKey, other.AccountKey);
        Assert.Equal(first.AccountKey, Assert.Single(first.Snapshots).AccountKey);
        Assert.Empty(empty.Snapshots);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"accountId\":null,")]
    [InlineData("\"accountId\":123,")]
    [InlineData("\"accountId\":\" \",")]
    [InlineData("\"accountId\":\"\",")]
    [InlineData("\"accountId\":\"A\",\"accountId\":\"B\",")]
    public void MissingMalformedOrAmbiguousAccountIsUnknown(string property)
    {
        var response = Response(property, true);
        Assert.Null(response.AccountKey);
        Assert.Null(Assert.Single(response.Snapshots).AccountKey);
    }

    [Fact]
    public async Task SameTimeAccountsAndUnknownCoexistAndFilterBeforeLimit()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        foreach (var account in new string?[] { null, "A", "B" })
            await repository.UpsertQuotaSnapshotAsync(Point(Now, 20, account), default);
        for (var index = 1; index <= 5; index++)
            await repository.UpsertQuotaSnapshotAsync(Point(Now.AddMinutes(index), 90, "B"), default);
        var a = Assert.Single(await repository.GetRecentQuotaSnapshotsAsync(
            QuotaWindowKind.FiveHour, "codex", "default", 1, default, accountKey: "A"));
        Assert.Equal(Now, a.CapturedAtUtc);
        Assert.Equal("A", a.AccountKey);
        Assert.Null(Assert.Single(await repository.GetRecentQuotaSnapshotsAsync(
            QuotaWindowKind.FiveHour, "codex", "default", 1, default)).AccountKey);
    }

    [Fact]
    public async Task CurrentForecastDoesNotBorrowOtherOrUnknownAccountAndForecastKeysCoexist()
    {
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        var service = new SqliteIntelligenceService(Database, repository);
        await repository.UpsertQuotaSnapshotAsync(Point(Now.AddHours(-1), 10, "A"), default);
        await repository.UpsertQuotaSnapshotAsync(Point(Now.AddMinutes(-1), 99, "B"), default);
        await repository.UpsertQuotaSnapshotAsync(Point(Now.AddMinutes(-2), 98, null), default);
        var current = Point(Now, 20, "A");
        var result = Assert.Single(await service.BuildAndPersistCurrentForecastsAsync([Lane(current)], Now, default));
        Assert.Equal(10, result.Forecast!.BurnRatePercentPerHour);
        var b = Assert.Single(await service.BuildAndPersistCurrentForecastsAsync([Lane(current with { AccountKey = "B" })], Now, default));
        Assert.NotNull(b.Forecast);
        var stored = await repository.GetRecentForecastSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default);
        Assert.Equal(new[] { "A", "B" }, stored.Select(x => x.AccountKey).Order().ToArray());
        var unknown = Assert.Single(await service.BuildAndPersistCurrentForecastsAsync([Lane(current with { AccountKey = null })], Now, default));
        Assert.Null(unknown.Forecast);
        Assert.Contains("scope", unknown.HistoryPolicy);
        Assert.Equal(2, (await repository.GetRecentForecastSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default)).Count);
    }

    [Fact]
    public void ResetsAndReplayNeverBridgeAccounts()
    {
        var first = Point(Now.AddHours(-1), 90, "A");
        var second = Point(Now, 5, "B");
        Assert.Empty(new QuotaResetDetector().Detect([first, second]));
        Assert.Throws<ArgumentException>(() => QuotaForecastBacktester.SplitEpochs([first, second]));
        var own = new[] { Point(Now.AddHours(-1), 10, "B"), Point(Now, 20, "B") };
        var forecast = new ForecastingService();
        Assert.Equal(forecast.BuildForecast(own, Now), forecast.BuildForecast([first, .. own], Now));
        var resets = new QuotaResetDetector().Detect([first, second, first with { CapturedAtUtc = Now, UsedPercent = 5 }]);
        Assert.Equal("A", Assert.Single(resets).AccountKey);
    }

    [Fact]
    public async Task VersionEightMigrationPreservesUnknownRowsAndIsIdempotent()
    {
        await CreateVersionEightAsync(false);
        var repository = new SqliteTelemetryRepository(Database);
        await repository.InitializeAsync(default);
        await new SqliteTelemetryRepository(Database).InitializeAsync(default);
        Assert.Null(Assert.Single(await repository.GetRecentQuotaSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default)).AccountKey);
        var forecast = Assert.Single(await repository.GetRecentForecastSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default));
        Assert.Null(forecast.AccountKey);
        Assert.Equal(2, forecast.Forecast.BurnRatePercentPerHour);
        Assert.Empty(await repository.GetRecentQuotaSnapshotsAsync(QuotaWindowKind.FiveHour, "codex", "default", 10, default, accountKey: "A"));
        Assert.Equal(14L, await ScalarAsync("PRAGMA user_version"));
    }

    [Fact]
    public void ScenarioNeverUsesOtherAccountToFillSparseHistory()
    {
        var samples = Enumerable.Range(1, 20).Select(i => new ScenarioHistorySample(
            QuotaWindowKind.FiveHour, Now.AddHours(-i - 1), Now.AddHours(-i), 5, 1, 0, null, null,
            Source: "codex-app-server:codex", AccountKey: "A")).ToArray();
        var planner = new ScenarioPlannerService();
        Assert.True(planner.Estimate(new(1, 1, 0, AccountKey: "A"), samples, Now).FiveHour.HasEnoughHistory);
        Assert.False(planner.Estimate(new(1, 1, 0, AccountKey: "B"), samples, Now).FiveHour.HasEnoughHistory);
    }

    [Fact]
    public void AccountSwitchRearmsLowAlertWithoutInventingReset()
    {
        TelemetrySnapshot Snapshot(double used, string account) => new(Now, RefreshTrigger.Interval,
            [], [], [Point(Now, used, account)], false, true, true, [], []);
        var engine = new QuotaAlertEngine([10]);
        Assert.Single(engine.Evaluate(Snapshot(95, "A")));
        Assert.Empty(engine.Evaluate(Snapshot(95, "A")));
        Assert.Single(engine.Evaluate(Snapshot(95, "B")));
        Assert.Empty(engine.Evaluate(Snapshot(5, "C")));
    }

    [Fact]
    public async Task MigrationFailureRollsBackQuotaRebuildAndCanRetry()
    {
        await CreateVersionEightAsync(true);
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteTelemetryRepository(Database).InitializeAsync(default));
        Assert.Equal(8L, await ScalarAsync("PRAGMA user_version"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM quota_snapshots"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name='account_key'"));
        await ExecuteAsync("ALTER TABLE forecast_snapshots ADD COLUMN evaluation_json TEXT;");
        await new SqliteTelemetryRepository(Database).InitializeAsync(default);
        Assert.Equal(14L, await ScalarAsync("PRAGMA user_version"));
    }

    private async Task CreateVersionEightAsync(bool malformed)
    {
        await ExecuteAsync($"""
            CREATE TABLE ingestion_checkpoints(file_path TEXT PRIMARY KEY, last_byte_offset INTEGER,
                updated_at_utc TEXT, last_session_id TEXT, parser_version TEXT, source_identity TEXT);
            CREATE TABLE quota_snapshots(provider TEXT,profile TEXT,kind TEXT,captured_at_utc TEXT,used_percent REAL,
                window_minutes INTEGER,resets_at_utc TEXT,source TEXT,PRIMARY KEY(provider,profile,kind,captured_at_utc,source));
            INSERT INTO quota_snapshots VALUES('codex','default','FiveHour','2026-09-08T12:00:00.0000000+00:00',20,300,
                '2026-09-08T17:00:00.0000000+00:00','codex-app-server:codex');
            CREATE TABLE forecast_snapshots(provider TEXT,profile TEXT,kind TEXT,generated_at_utc TEXT,
                burn_rate_percent_per_hour REAL,estimated_exhaustion_at_utc TEXT,survives_until_reset INTEGER,
                sustainable_percent_per_hour REAL,confidence REAL,state TEXT,burn_pressure REAL,
                projected_remaining_at_reset_percent REAL,trend TEXT,is_quantized_flat INTEGER,
                quota_source TEXT,quota_authority TEXT,quota_captured_at_utc TEXT,quota_window_minutes INTEGER,
                quota_resets_at_utc TEXT{(malformed ? "" : ",evaluation_json TEXT")});
            INSERT INTO forecast_snapshots(provider,profile,kind,generated_at_utc,burn_rate_percent_per_hour,
                confidence,state,is_quantized_flat,quota_authority)
                VALUES('codex','default','FiveHour','2026-09-08T12:00:00.0000000+00:00',2,0,'Learning',0,'Unknown');
            PRAGMA user_version=8;
            """);
    }

    [Fact]
    public async Task TokenForecastUsesRolloutsWithoutQuotaAccountOrQuotaRows()
    {
        var repository = new SqliteTelemetryRepository(Database);
        var intelligence = new SqliteIntelligenceService(Database, repository);
        await repository.InitializeAsync(default);
        using (var store = new SqliteCodexObservatoryStore(Database)) await store.InitializeAsync(default);
        using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO codex_native_token_events(source_event_id,source_file,session_id,observed_at_utc,
                    model,reasoning_effort,counter_epoch,uncached_input_tokens,cache_read_tokens,cache_write_tokens,
                    non_reasoning_output_tokens,reasoning_output_tokens,reported_total_tokens,captured_at_utc)
                VALUES($id,'fixture.jsonl','fixture-session',$at,'fixture-model','high',0,1000,0,0,0,0,1000,$captured);
                """;
            command.Parameters.AddWithValue("$id", "");
            command.Parameters.AddWithValue("$at", "");
            command.Parameters.AddWithValue("$captured", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            for (var i = 0; i < 80; i++)
            {
                command.Parameters["$id"].Value = $"fixture-{i}";
                command.Parameters["$at"].Value = Now.AddMinutes(-1200 + i * 15).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                await command.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
        var forecast = await intelligence.ForecastTokenWorkloadAsync(Now, default);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM quota_snapshots"));
        Assert.Equal(80, forecast.TokenEvents);
        Assert.Equal(2, forecast.Predictions.Count);
        Assert.True(forecast.Predictions[0].TrainingSamples >= 20);
        Assert.True(forecast.Predictions[0].ExpectedTokens > 0);
    }

    private async Task ExecuteAsync(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static CodexQuotaResponse Response(string accountProperty, bool window) =>
        CodexAppServerQuotaProvider.ParseQuotaResponse("{\"result\":{" + accountProperty + "\"rateLimits\":{" +
            (window ? "\"primary\":{\"usedPercent\":20,\"windowDurationMins\":300}" : "") + "}}}", Now);

    private static QuotaSnapshot Point(DateTimeOffset captured, double used, string? account) =>
        new(QuotaWindowKind.FiveHour, captured, used, 300, Now.AddHours(5), "codex", "default", "codex-app-server:codex", account) { HasSourceTimestamp = true };
    private static QuotaLaneState Lane(QuotaSnapshot snapshot) =>
        new(snapshot.Kind, snapshot.Provider, snapshot.Profile, snapshot, TelemetryHealthState.Live, snapshot.CapturedAtUtc);

    public void Dispose()
    {
        using var connection = new SqliteConnection($"Data Source={Database}");
        SqliteConnection.ClearPool(connection);
        Directory.Delete(_directory, recursive: true);
    }
}
