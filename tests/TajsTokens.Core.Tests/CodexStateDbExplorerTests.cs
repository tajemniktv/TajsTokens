using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexStateDbExplorerTests
{
    [Fact]
    public async Task DiscoverAndInspect_ReportsRawSchemaWithoutUsingProductModels()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-db-explorer-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);

            var candidates = explorer.DiscoverCandidates();
            var candidate = Assert.Single(candidates);
            Assert.Equal(path, candidate.Path);
            Assert.Equal(5, candidate.Generation);

            var result = await explorer.InspectAsync(path);
            Assert.Equal(path, result.Database.Path);
            var threads = Assert.Single(result.Tables, table => table.Name == "threads");
            Assert.Equal("table", threads.ObjectType);
            Assert.Equal(2, threads.RowCount);
            Assert.Contains(threads.Columns, column => column.Name == "prompt");
            Assert.Contains(threads.Indexes, index => index.Name == "idx_threads_model");
            Assert.Contains(result.FocusedTables, table => table.Name == "threads");
            Assert.True(CodexStateDbExplorerService.IsInterestingTable("logs"));
            Assert.True(CodexStateDbExplorerService.IsInterestingTable("thread_turn_summaries"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverCandidates_IncludesSnapshotFilesAndCodexSqliteFolderDbs()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-db-discovery-");
        try
        {
            var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
            var sqliteDirectory = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sqlite"));
            var snapshots = Directory.CreateDirectory(Path.Combine(directory.FullName, "private"));
            foreach (var path in new[]
                     {
                         Path.Combine(codexHome.FullName, "state_5.sqlite"),
                         Path.Combine(sqliteDirectory.FullName, "codex-dev.db"),
                         Path.Combine(sqliteDirectory.FullName, "codex-thread-summaries-dev.db"),
                         Path.Combine(snapshots.FullName, "state_5_snapshot.sqlite"),
                         Path.Combine(snapshots.FullName, "logs_2_snapshot.sqlite")
                     })
            {
                await File.WriteAllBytesAsync(path, []);
            }

            var explorer = new CodexStateDbExplorerService(codexHome.FullName, snapshots.FullName);
            var candidates = explorer.DiscoverCandidates();

            Assert.Equal(5, candidates.Count);
            Assert.Contains(candidates, candidate => candidate.FileName == "codex-dev.db" && candidate.SourceDescription == "Codex sqlite folder");
            Assert.Contains(candidates, candidate => candidate.FileName == "codex-thread-summaries-dev.db" && candidate.SourceDescription == "Codex sqlite folder");
            Assert.Contains(candidates, candidate => candidate.FileName == "logs_2_snapshot.sqlite" && candidate.SourceDescription == "Snapshot folder");
            Assert.Equal(5, Assert.Single(candidates, candidate => candidate.FileName == "state_5_snapshot.sqlite").Generation);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadPage_UsesReadOnlyQueryOnlyAndReturnsRawValues()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-db-page-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);

            var page = await explorer.ReadPageAsync(path, "threads", pageIndex: 0, pageSize: 1);
            Assert.Equal(2, page.TotalRows);
            Assert.Single(page.Rows);
            Assert.Equal(["id", "model", "prompt", "rollout_path", "payload"], page.Columns);
            Assert.Contains("thread-1", page.Rows[0].DisplayText);
            Assert.Contains("prompt one", page.Rows[0].DisplayText);

            await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "PRAGMA query_only = ON; INSERT INTO threads(id) VALUES('blocked');";
                await command.ExecuteNonQueryAsync();
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Compare_DetectsChangedRowsWithAnUnchangedRowCount()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-db-diff-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);
            var baseline = (await explorer.InspectAsync(path)).Snapshot;

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                var update = connection.CreateCommand();
                update.CommandText = "UPDATE threads SET model = 'gpt-new' WHERE id = 'thread-2';";
                await update.ExecuteNonQueryAsync();
            }

            var current = (await explorer.InspectAsync(path)).Snapshot;
            var diff = CodexStateDbExplorerService.Compare(baseline, current);
            var changed = Assert.Single(diff.Tables, table => table.Name == "threads");
            Assert.True(changed.RowsChanged);
            Assert.False(changed.SchemaChanged);
            Assert.Equal(2, changed.PreviousRowCount);
            Assert.Equal(2, changed.CurrentRowCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SanitizedExport_RedactsContentAndReducesPathsAndBlobs()
    {
        var page = new CodexStateRawPage(
            "C:\\Users\\tester\\.codex\\state_5.sqlite",
            "threads",
            0,
            50,
            1,
            ["id", "prompt", "rollout_path", "blob_value"],
            [new CodexStateRawRow(["id-1", "private prompt", "C:\\Users\\tester\\rollout.jsonl", new byte[] { 1, 2 }], "")]);

        var export = CodexStateDbExplorerService.BuildSanitizedExport(page);
        Assert.Contains("id-1", export);
        Assert.Contains("[redacted]", export);
        Assert.Contains("<path:rollout.jsonl>", export);
        Assert.Contains("<blob 2 bytes>", export);
        Assert.DoesNotContain("private prompt", export);
    }

    [Fact]
    public async Task CombinedSchemaExport_LabelsEachDatabaseWithoutExportingRows()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-db-schema-");
        try
        {
            var firstPath = Path.Combine(directory.FullName, "state_5_snapshot.sqlite");
            var secondPath = Path.Combine(directory.FullName, "codex-dev.db");
            await CreateDatabaseAsync(firstPath);
            File.Copy(firstPath, secondPath);

            var explorer = new CodexStateDbExplorerService(directory.FullName, directory.FullName);
            var first = await explorer.InspectAsync(firstPath, includeRowFingerprints: false);
            var second = await explorer.InspectAsync(secondPath, includeRowFingerprints: false);
            var export = CodexStateDbExplorerService.BuildCombinedSchemaExport([first, second]);

            Assert.Contains($"-- SOURCE: {Path.GetFullPath(firstPath)}", export);
            Assert.Contains($"-- SOURCE: {Path.GetFullPath(secondPath)}", export);
            Assert.Contains("CREATE TABLE threads", export);
            Assert.Contains("-- COLUMN 0: id TEXT", export);
            Assert.DoesNotContain("prompt one", export);
            Assert.DoesNotContain("thread-1", export);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task CreateDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE threads(id TEXT PRIMARY KEY, model TEXT, prompt TEXT, rollout_path TEXT, payload BLOB);
            CREATE INDEX idx_threads_model ON threads(model);
            INSERT INTO threads(id, model, prompt, rollout_path, payload) VALUES
                ('thread-1', 'gpt-a', 'prompt one', 'C:/rollout-1.jsonl', X'0102'),
                ('thread-2', 'gpt-b', 'prompt two', 'C:/rollout-2.jsonl', X'0304');
            CREATE TABLE thread_spawn_edges(parent_thread_id TEXT, child_thread_id TEXT, status TEXT);
            """;
        await schema.ExecuteNonQueryAsync();
    }
}
