using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteReadGenerationMonitorTests
{
    [Fact]
    public async Task CommitsInvalidateReadCacheButRollbacksDoNotAndConnectionKeysAreNeverReused()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-read-generation-");
        string path = Path.Combine(directory.FullName, "fixture.db");
        try
        {
            await using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
            await writer.OpenAsync();
            using var command = writer.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE evidence(value INTEGER);";
            await command.ExecuteNonQueryAsync();
            using var monitor = new SqliteReadGenerationMonitor(path);
            SqliteReadGeneration? first = await monitor.ReadAsync(default);
            Assert.NotNull(first);
            Assert.Equal(first, await monitor.ReadAsync(default));
            command.CommandText = "BEGIN; INSERT INTO evidence VALUES(1); ROLLBACK;";
            await command.ExecuteNonQueryAsync();
            Assert.Equal(first, await monitor.ReadAsync(default));
            command.CommandText = "INSERT INTO evidence VALUES(2);";
            await command.ExecuteNonQueryAsync();
            SqliteReadGeneration? changed = await monitor.ReadAsync(default);
            Assert.NotNull(changed);
            Assert.Equal(first.DatabaseIdentity, changed.DatabaseIdentity);
            Assert.Equal(first.ConnectionIdentity, changed.ConnectionIdentity);
            Assert.NotEqual(first, changed);
            using var reopened = new SqliteReadGenerationMonitor(path);
            Assert.NotEqual(changed, await reopened.ReadAsync(default));
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(true); }
    }

    [Fact]
    public async Task MissingOrDisposedSourceDisablesReuseAndCancellationIsPreserved()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-read-generation-missing-");
        try
        {
            using var monitor = new SqliteReadGenerationMonitor(Path.Combine(directory.FullName, "missing.db"));
            Assert.Null(await monitor.ReadAsync(default));
            Assert.Empty(directory.GetFiles());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.ReadAsync(cancellation.Token));
            monitor.Dispose();
            Assert.Null(await monitor.ReadAsync(default));
        }
        finally { directory.Delete(true); }
    }
}
