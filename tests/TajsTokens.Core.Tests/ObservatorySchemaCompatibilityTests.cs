using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class ObservatorySchemaCompatibilityTests
{
    private static readonly DateTimeOffset EventAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

    [Fact]
    public async Task Schema8UpgradeAndExistingCurrentSchemaPlannerPathsPreserveEvidence()
    {
        string directory = TemporaryDirectory();
        string database = Path.Combine(directory, "telemetry.db");
        try
        {
            await SeedAsync(database);
            // Recreate the old v8 shape only in this disposable fixture, never in user data.
            await ExecuteAsync(database, LegacyObservatoryFixture.RemoveProjectionCache + """
                DROP INDEX idx_native_token_source_time;
                DROP INDEX idx_native_token_session_source_time;
                DROP INDEX idx_context_source_time;
                ALTER TABLE codex_native_token_events DROP COLUMN source_event_at_utc;
                ALTER TABLE codex_native_token_events DROP COLUMN source_timestamp_quality;
                ALTER TABLE codex_counter_state DROP COLUMN last_source_event_at_utc;
                ALTER TABLE context_observations DROP COLUMN source_event_at_utc;
                ALTER TABLE context_observations DROP COLUMN source_timestamp_quality;
                UPDATE observatory_schema SET version = 8 WHERE component = 'codex-observatory';
                """);
            Evidence before = await ReadEvidenceAsync(database);
            using (var upgrading = new SqliteCodexObservatoryStore(database)) await upgrading.InitializeAsync(default);
            Assert.Equal(before, await ReadEvidenceAsync(database));
            Assert.Equal(11L, await ScalarAsync(database, "SELECT version FROM observatory_schema WHERE component='codex-observatory'"));
            Assert.Equal("LegacyUnverified", await ScalarAsync(database, "SELECT source_timestamp_quality FROM codex_native_token_events"));
            Assert.Null(await ScalarAsync(database, "SELECT source_event_at_utc FROM codex_native_token_events"));

            // These are the two failing UI paths: both reopen an already-upgraded database.
            var repository = new SqliteTelemetryRepository(database);
            var service = new SqliteIntelligenceService(database, repository);
            RecentScenarioPattern recent = await service.GetRecentScenarioPatternAsync(EventAt.AddMinutes(30), default);
            ScenarioEstimate scenario = await service.EstimateScenarioAsync(new ScenarioRequest(1, 1, 0, AccountKey: "fixture-account"),
                EventAt.AddDays(-1), default);
            Assert.False(recent.Available); // Legacy time cannot become verified dated activity.
            Assert.False(scenario.FiveHour.HasEnoughHistory);
            Assert.False(scenario.Weekly.HasEnoughHistory);
            Assert.Equal(before, await ReadEvidenceAsync(database));
            Assert.Equal(11L, await ScalarAsync(database, "SELECT version FROM observatory_schema WHERE component='codex-observatory'"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FutureComponentIsStillRejectedWithoutDowngradingOrChangingEvidence()
    {
        string directory = TemporaryDirectory();
        string database = Path.Combine(directory, "telemetry.db");
        try
        {
            await SeedAsync(database);
            Evidence before = await ReadEvidenceAsync(database);
            await ExecuteAsync(database, "UPDATE observatory_schema SET version=12 WHERE component='codex-observatory'");
            using var reader = new SqliteCodexObservatoryStore(database);
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.InitializeAsync(default));
            Assert.Contains("schema 12 is newer than supported version 11", error.Message);
            Assert.Equal(12L, await ScalarAsync(database, "SELECT version FROM observatory_schema WHERE component='codex-observatory'"));
            Assert.Equal(before, await ReadEvidenceAsync(database));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static async Task SeedAsync(string database)
    {
        var repository = new SqliteTelemetryRepository(database);
        await repository.InitializeAsync(default);
        using var store = new SqliteCodexObservatoryStore(database);
        await store.InitializeAsync(default);
        await store.ApplyCumulativeTokenObservationAsync(new CodexCumulativeTokenObservation(
            "fixture-event", "fixture.jsonl", "fixture-session", null, EventAt, "fixture-model", "high",
            1000, 500, 0, 100, 50, 1100) { CapturedAtUtc = EventAt.AddDays(1) }, default);
    }

    private sealed record Evidence(long Rows, long Tokens, string Observed, string Captured);
    private static async Task<Evidence> ReadEvidenceAsync(string database)
    {
        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(reported_total_tokens), MIN(observed_at_utc), MIN(captured_at_utc) FROM codex_native_token_events";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3));
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + database);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string TemporaryDirectory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Directory.CreateDirectory(Path.Combine(root.FullName, ".codex", "temp", "schema-compat-" + Guid.NewGuid().ToString("N"))).FullName;
    }
}
