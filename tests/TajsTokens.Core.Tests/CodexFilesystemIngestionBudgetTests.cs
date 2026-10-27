using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexFilesystemIngestionBudgetTests
{
    [Fact]
    public async Task FallbackBoundsWorkCatchesUpAndChecksDurableCheckpointBeforeWarmSkip()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-fallback-budget-");
        try
        {
            string sessions = Directory.CreateDirectory(Path.Combine(directory.FullName, "sessions")).FullName;
            string[] files = Enumerable.Range(0, 40).Select(i => Path.Combine(sessions, $"t{i:D2}.jsonl")).ToArray();
            foreach (string file in files) await File.WriteAllTextAsync(file, "{}\n");
            string database = Path.Combine(directory.FullName, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            using var store = new SqliteCodexObservatoryStore(database);
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), repository, store);
            var collector = new CodexObservatoryService(ingestion, store, [sessions]);
            int scanned = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                var result = await collector.RefreshAsync(default);
                Assert.InRange(result.FilesScanned, 1, 16);
                scanned += result.FilesScanned;
                Assert.Equal(0, result.Errors);
            }
            Assert.Equal(40, scanned);
            var warm = await collector.RefreshAsync(default);
            Assert.Equal(0, warm.FilesScanned);
            Assert.Equal(0, warm.DeferredFiles);

            await File.AppendAllTextAsync(files[0], "{}\n");
            var append = await collector.RefreshAsync(default);
            Assert.Equal(1, append.FilesScanned);
            // These deliberately untyped records have no semantic resume state, so the one
            // changed file safely replays its prefix. Unchanged files remain unopened.
            Assert.Equal(2, append.RecordsScanned);

            // The in-memory verified prefix is insufficient after app-owned checkpoint loss.
            await using var connection = new SqliteConnection($"Data Source={database}");
            await connection.OpenAsync();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM ingestion_checkpoints WHERE file_path = $path;";
            delete.Parameters.AddWithValue("$path", files[1]);
            await delete.ExecuteNonQueryAsync();
            Assert.False(await ingestion.IsUnchangedAsync(files[1], default));
            var recovered = await collector.RefreshAsync(default);
            Assert.Equal(1, recovered.FilesScanned);
            Assert.Equal(1, recovered.RecordsScanned);
            Assert.NotNull(await repository.GetCheckpointAsync(files[1], default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }
}
