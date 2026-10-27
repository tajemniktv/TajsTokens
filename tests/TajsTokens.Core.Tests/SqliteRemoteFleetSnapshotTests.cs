// Taj's Tokens | SqliteRemoteFleetSnapshotTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteRemoteFleetSnapshotTests
{
    [Fact]
    public async Task RetainsDistinctChecksByHostAndRecoversTheLatestWithoutMerging()
    {
        await WithRepositoryAsync(async (repository, _) =>
        {
            DateTimeOffset captured = DateTimeOffset.Parse("2026-10-13T10:00:00Z");
            CodexRemoteFleetHostResult first = new("laptop", captured,
                [new("laptop", "thread-1", "inProgress", captured.AddMinutes(-1), captured)],
                null, false, 0, true) { CollectedAtUtc = captured.AddSeconds(5) };
            Assert.True(await repository.SaveRemoteFleetSnapshotAsync(first, default));
            Assert.False(await repository.SaveRemoteFleetSnapshotAsync(first, default));
            CodexRemoteFleetHostResult later = first with
            {
                Threads = [new("laptop", "thread-1", "completed", captured.AddMinutes(-1), captured.AddMinutes(1))],
                CollectedAtUtc = captured.AddMinutes(2),
            };
            Assert.True(await repository.SaveRemoteFleetSnapshotAsync(later, default));
            Assert.True(await repository.SaveRemoteFleetSnapshotAsync(new("vps", captured, [], null, false, 0, false)
                { CollectedAtUtc = captured.AddMinutes(3) }, default));

            CodexRemoteFleetHostResult recovered = Assert.IsType<CodexRemoteFleetHostResult>(
                await repository.ReadLatestRemoteFleetSnapshotAsync("LAPTOP", default));
            Assert.Equal("laptop", recovered.Host);
            Assert.Equal(captured.AddMinutes(2), recovered.CollectedAtUtc);
            Assert.Equal("completed", Assert.Single(recovered.Threads).NativeStatus);
            Assert.Equal(2, (await repository.ReadRemoteFleetHistoryAsync("laptop", 10, default)).Count);
            Assert.Null(await repository.ReadLatestRemoteFleetSnapshotAsync("unknown-host", default));
            Assert.Equal(0, Assert.Single(await repository.ReadRemoteFleetHistoryAsync("vps", 10, default)).ThreadCount);
        });
    }

    [Fact]
    public async Task RejectsUnnormalizedRemoteTextAndMigratesV14WithoutDeletingQuota()
    {
        await WithRepositoryAsync(async (repository, path) =>
        {
            DateTimeOffset now = DateTimeOffset.Parse("2026-10-13T10:00:00Z");
            CodexRemoteFleetHostResult bad = new("laptop", now,
                [new("laptop", "prompt with spaces", "inProgress", null, now)],
                null, false) { CollectedAtUtc = now };
            await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveRemoteFleetSnapshotAsync(bad, default));
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO quota_snapshots(provider,profile,kind,captured_at_utc,source,account_key)
                    VALUES('codex','default','Weekly','2026-10-13T10:00:00.0000000+00:00','fixture','account-a');
                    DROP TABLE remote_fleet_threads;
                    DROP TABLE remote_fleet_snapshots;
                    PRAGMA user_version = 14;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await new SqliteTelemetryRepository(path).InitializeAsync(default);
            await using var check = new SqliteConnection($"Data Source={path}");
            await check.OpenAsync();
            await using SqliteCommand query = check.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM quota_snapshots;";
            Assert.Equal(1L, await query.ExecuteScalarAsync());
            query.CommandText = "PRAGMA user_version;";
            Assert.Equal(15L, await query.ExecuteScalarAsync());
        });
    }

    private static async Task WithRepositoryAsync(Func<SqliteTelemetryRepository, string, Task> action)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string tempRoot = Path.Combine(root.FullName, ".codex", "temp");
        Directory.CreateDirectory(tempRoot);
        string directory = Path.Combine(tempRoot, "remote-fleet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "telemetry.db");
            var repository = new SqliteTelemetryRepository(path);
            await repository.InitializeAsync(default);
            await action(repository, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
