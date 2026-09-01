using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteTelemetryRepositoryTests
{
    private static readonly string[] FoundationTables =
    [
        "quota_snapshots",
        "token_usage",
        "sessions",
        "agents",
        "agent_relationships",
        "usage_events",
        "reset_events",
        "announcements",
        "ingestion_checkpoints",
        "repositories",
        "workspaces",
        "forecast_snapshots"
    ];

    [Fact]
    public async Task InitializeAsync_CreatesCompleteVersion5FoundationSchema()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "telemetry.db");

        try
        {
            var repository = new SqliteTelemetryRepository(path);
            await repository.InitializeAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();

                Assert.Equal(5, await ReadSchemaVersionAsync(connection));
                var tables = await ReadTableNamesAsync(connection);
                foreach (var expected in FoundationTables)
                {
                    Assert.Contains(expected, tables);
                }
            }
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesVersion2WithoutDestroyingExistingTelemetry()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "telemetry.db");

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE quota_snapshots (
                        provider TEXT NOT NULL,
                        profile TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        used_percent REAL,
                        window_minutes INTEGER,
                        resets_at_utc TEXT,
                        source TEXT NOT NULL,
                        PRIMARY KEY(provider, profile, kind, captured_at_utc)
                    );
                    INSERT INTO quota_snapshots(provider, profile, kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, source)
                    VALUES('codex', 'default', 'FiveHour', '2026-08-31T20:00:00.0000000+00:00', 42, 300, NULL, 'fixture');
                    PRAGMA user_version = 2;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteTelemetryRepository(path);
            await repository.InitializeAsync(CancellationToken.None);

            await using (var migrated = new SqliteConnection($"Data Source={path}"))
            {
                await migrated.OpenAsync();
                Assert.Equal(5, await ReadSchemaVersionAsync(migrated));
                Assert.Contains("repositories", await ReadTableNamesAsync(migrated));
                Assert.Contains("workspaces", await ReadTableNamesAsync(migrated));
                Assert.Contains("forecast_snapshots", await ReadTableNamesAsync(migrated));
                Assert.Contains("agent_relationships", await ReadTableNamesAsync(migrated));

                var countCommand = migrated.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM quota_snapshots;";
                Assert.Equal(1L, (long)(await countCommand.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesVersion3ForecastRowsWithSafeDefaults()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "telemetry.db");

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE forecast_snapshots (
                        provider TEXT NOT NULL,
                        profile TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        generated_at_utc TEXT NOT NULL,
                        burn_rate_percent_per_hour REAL,
                        estimated_exhaustion_at_utc TEXT,
                        survives_until_reset INTEGER,
                        sustainable_percent_per_hour REAL,
                        confidence REAL NOT NULL,
                        PRIMARY KEY(provider, profile, kind, generated_at_utc)
                    );
                    INSERT INTO forecast_snapshots(
                        provider, profile, kind, generated_at_utc, burn_rate_percent_per_hour,
                        estimated_exhaustion_at_utc, survives_until_reset, sustainable_percent_per_hour, confidence)
                    VALUES('codex', 'default', 'FiveHour', '2026-08-31T20:00:00.0000000+00:00', 12, NULL, 1, 20, 0.5);
                    PRAGMA user_version = 3;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteTelemetryRepository(path);
            await repository.InitializeAsync(CancellationToken.None);
            var forecasts = await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour, "codex", "default", 10, CancellationToken.None);

            var forecast = Assert.Single(forecasts).Forecast;
            Assert.Equal(ForecastState.Learning, forecast.State);
            Assert.Null(forecast.BurnPressure);
            Assert.Null(forecast.ProjectedRemainingAtResetPercent);
            Assert.Null(forecast.Trend);
            Assert.False(forecast.IsQuantizedFlat);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task FoundationIdentityAndForecastRecords_PreserveKnownMetadataAndStripRemoteCredentials()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "telemetry.db");
        var now = new DateTimeOffset(2026, 8, 31, 21, 0, 0, TimeSpan.Zero);

        try
        {
            var repository = new SqliteTelemetryRepository(path);
            await repository.InitializeAsync(CancellationToken.None);

            var repositoryIdentity = new RepositoryIdentity(
                "repo-1",
                "TajsTokens",
                @"C:\src\TajsTokens",
                "https://tajem:super-secret-token@example.invalid/tajemniktv/TajsTokens.git?access_token=also-secret#private-fragment",
                now,
                now);
            Assert.DoesNotContain("super-secret-token", repositoryIdentity.RemoteUrl!, StringComparison.Ordinal);
            Assert.DoesNotContain("access_token", repositoryIdentity.RemoteUrl!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-fragment", repositoryIdentity.RemoteUrl!, StringComparison.Ordinal);

            var scpIdentity = new RepositoryIdentity(
                "repo-scp",
                "TajsTokens",
                null,
                "oauth2:scp-secret@git.example.invalid:tajemniktv/TajsTokens.git?token=bad#fragment",
                now,
                now);
            Assert.Equal("git.example.invalid:tajemniktv/TajsTokens.git", scpIdentity.RemoteUrl);

            await repository.UpsertRepositoryAsync(repositoryIdentity, CancellationToken.None);
            await repository.UpsertWorkspaceAsync(
                new WorkspaceIdentity("workspace-1", @"C:\src\TajsTokens", "repo-1", now, now),
                CancellationToken.None);

            // A later partial observation must enrich timestamps/name/path without erasing identity
            // metadata that the provider simply did not repeat in this refresh.
            await repository.UpsertRepositoryAsync(
                new RepositoryIdentity("repo-1", "TajsTokens renamed", null, null, now.AddMinutes(1), now.AddMinutes(1)),
                CancellationToken.None);
            await repository.UpsertWorkspaceAsync(
                new WorkspaceIdentity("workspace-1", @"C:\src\TajsTokens-renamed", null, now.AddMinutes(1), now.AddMinutes(1)),
                CancellationToken.None);

            var snapshot = new ForecastSnapshot(
                "codex",
                "default",
                new Forecast(
                    QuotaWindowKind.FiveHour,
                    now,
                    12.5,
                    now.AddHours(3),
                    true,
                    8.25,
                    0.8,
                    ForecastState.NearSustainablePace,
                    1.17,
                    22.5,
                    "accelerating",
                    true));
            await repository.UpsertForecastSnapshotAsync(snapshot, CancellationToken.None);

            var forecasts = await repository.GetRecentForecastSnapshotsAsync(
                QuotaWindowKind.FiveHour,
                "codex",
                "default",
                10,
                CancellationToken.None);
            Assert.Equal(snapshot, Assert.Single(forecasts));

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                Assert.Equal(1L, await CountAsync(connection, "repositories"));
                Assert.Equal(1L, await CountAsync(connection, "workspaces"));

                var repositoryCommand = connection.CreateCommand();
                repositoryCommand.CommandText = "SELECT root_path, remote_url FROM repositories WHERE repository_id = 'repo-1';";
                await using (var reader = await repositoryCommand.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(@"C:\src\TajsTokens", reader.GetString(0));
                    var persistedRemote = reader.GetString(1);
                    Assert.Contains("example.invalid/tajemniktv/TajsTokens.git", persistedRemote, StringComparison.Ordinal);
                    Assert.DoesNotContain("tajem:", persistedRemote, StringComparison.Ordinal);
                    Assert.DoesNotContain("secret", persistedRemote, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("access_token", persistedRemote, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("private-fragment", persistedRemote, StringComparison.Ordinal);
                }

                var workspaceCommand = connection.CreateCommand();
                workspaceCommand.CommandText = "SELECT path, repository_id FROM workspaces WHERE workspace_id = 'workspace-1';";
                await using var workspaceReader = await workspaceCommand.ExecuteReaderAsync();
                Assert.True(await workspaceReader.ReadAsync());
                Assert.Equal(@"C:\src\TajsTokens-renamed", workspaceReader.GetString(0));
                Assert.Equal("repo-1", workspaceReader.GetString(1));
            }
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        // Microsoft.Data.Sqlite pools connections by default, so disposing a connection can still
        // leave a pooled handle to the temporary database on Windows. Drop all test pools before
        // deleting the per-test directory to keep cleanup deterministic on Windows CI.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var results = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        var command = connection.CreateCommand();
        command.CommandText = table switch
        {
            "repositories" => "SELECT COUNT(*) FROM repositories;",
            "workspaces" => "SELECT COUNT(*) FROM workspaces;",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
