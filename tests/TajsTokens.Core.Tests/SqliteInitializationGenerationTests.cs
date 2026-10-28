using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteInitializationGenerationTests
{
    [Fact]
    public async Task InitializedOwnersRecheckReplacementAndRefuseNewerSchema()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("tajstokens-initialization-");
        try
        {
            string database = Path.Combine(folder.FullName, "data.db");
            var repository = new SqliteTelemetryRepository(database);
            using var store = new SqliteCodexObservatoryStore(database);
            var index = new SqliteCodexStateIndexStore(database);
            await repository.InitializeIntelligenceAsync(default);
            await store.InitializeAsync(default);
            await index.InitializeAsync(default);
            string newer = Path.Combine(folder.FullName, "newer.db");
            await Execute(newer, "PRAGMA user_version=999;");
            SqliteConnection.ClearAllPools();
            File.Move(newer, database, true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.InitializeAsync(default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
            Assert.Equal(999L, await Scalar(database, "PRAGMA user_version;"));

            string empty = Path.Combine(folder.FullName, "empty.db");
            await Execute(empty, "PRAGMA user_version=0;");
            SqliteConnection.ClearAllPools();
            File.Move(empty, database, true);
            await repository.InitializeIntelligenceAsync(default);
            await store.InitializeAsync(default);
            await index.InitializeAsync(default);
            Assert.Equal(16L, await Scalar(database, "PRAGMA user_version;"));
            Assert.Equal(11L, await Scalar(database, "SELECT version FROM observatory_schema WHERE component='codex-observatory';"));
            Assert.Empty(await index.GetFingerprintsAsync(default));
            Assert.Equal(0, await repository.RefreshQuotaResetEventsAsync(default));
        }
        finally { SqliteConnection.ClearAllPools(); folder.Delete(true); }
    }

    [Fact]
    public async Task LegacyPrivacyMigrationIsAtomicVersionedAndRetryable()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("tajstokens-privacy-migration-");
        try
        {
            string database = Path.Combine(folder.FullName, "data.db");
            using (var seed = new SqliteCodexObservatoryStore(database)) await seed.InitializeAsync(default);
            await Execute(database, """
                DELETE FROM observatory_schema WHERE component='legacy-privacy-scrub';
                INSERT INTO rollout_files VALUES('legacy','C:/private/rollout.jsonl',NULL,42,'2026-10-01T00:00:00Z');
                CREATE TRIGGER reject_test_scrub BEFORE UPDATE ON rollout_files
                BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;
                """);
            using (var failing = new SqliteCodexObservatoryStore(database))
                await Assert.ThrowsAsync<SqliteException>(() => failing.InitializeAsync(default));
            Assert.Equal(0L, await Scalar(database, "SELECT COUNT(*) FROM observatory_schema WHERE component='legacy-privacy-scrub';"));
            Assert.Equal(1L, await Scalar(database, "SELECT COUNT(*) FROM rollout_files WHERE file_path='C:/private/rollout.jsonl';"));
            await Execute(database, "DROP TRIGGER reject_test_scrub;");
            using (var retry = new SqliteCodexObservatoryStore(database)) await retry.InitializeAsync(default);
            Assert.Equal(1L, await Scalar(database, "SELECT version FROM observatory_schema WHERE component='legacy-privacy-scrub';"));
            Assert.Equal(1L, await Scalar(database, "SELECT COUNT(*) FROM rollout_files WHERE file_path='[redacted]' AND size_bytes=42 AND last_seen_at_utc='2026-10-01T00:00:00Z';"));
            await Execute(database, "UPDATE observatory_schema SET version=2 WHERE component='legacy-privacy-scrub';");
            using var future = new SqliteCodexObservatoryStore(database);
            await Assert.ThrowsAsync<InvalidOperationException>(() => future.InitializeAsync(default));
            Assert.Equal(2L, await Scalar(database, "SELECT version FROM observatory_schema WHERE component='legacy-privacy-scrub';"));
        }
        finally { SqliteConnection.ClearAllPools(); folder.Delete(true); }
    }

    private static async Task Execute(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText=sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> Scalar(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText=sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
