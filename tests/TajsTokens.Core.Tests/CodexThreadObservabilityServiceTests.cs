using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexThreadObservabilityServiceTests
{
    [Fact]
    public async Task ReadThread_ProjectsNativeMetadataRelationshipsAndHistoryWithoutWritingSources()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-view-");
        try
        {
            var statePath = Path.Combine(directory.FullName, "state_5.sqlite");
            var historyPath = Path.Combine(directory.FullName, "thread_history_1.sqlite");
            await CreateStateAsync(statePath);
            await CreateHistoryAsync(historyPath);
            var stateBefore = File.GetLastWriteTimeUtc(statePath);
            var historyBefore = File.GetLastWriteTimeUtc(historyPath);

            var service = new CodexThreadObservabilityService(directory.FullName);
            var result = await service.ReadThreadAsync("thread-1", CancellationToken.None);

            Assert.NotNull(result.Thread);
            Assert.Equal("paginated", result.Thread!.HistoryMode);
            Assert.Equal("feature/demo", result.Thread.GitBranch);
            Assert.Equal("project-1", result.Thread.ProjectId);
            Assert.Equal("Project One", result.Project!.Name);
            Assert.True(result.ProjectCapabilityAvailable);
            Assert.True(result.ProjectRootsCapabilityAvailable);
            Assert.True(result.Project.RootsCapabilityAvailable);
            Assert.Equal(new[] { "C:/repo", "C:/repo/docs" }, result.Project.OrderedRoots);
            Assert.Equal("section-1", result.Section!.SectionId);
            Assert.Equal("Tools", result.Section.Name);
            var tool = Assert.Single(result.DynamicTools);
            Assert.Equal("search", tool.Name);
            Assert.True(tool.DeferLoading);
            Assert.True(result.DynamicToolsCapabilityAvailable);
            Assert.True(result.SpawnEdgesCapabilityAvailable);
            Assert.Equal(2, result.SpawnEdges.Count);
            Assert.Contains(result.SpawnEdges, edge => edge.ParentThreadId == "thread-1" && edge.ChildThreadId == "thread-2" && edge.Status == "open");
            Assert.Equal(2, result.Turns.Count);
            Assert.True(result.TurnsCapabilityAvailable);
            Assert.Equal("completed", result.Turns[0].Status);
            Assert.Equal(2, result.Items.Count);
            Assert.True(result.ItemsCapabilityAvailable);
            Assert.Equal("userMessage", result.Items[0].ItemType);
            Assert.Single(result.RealtimeItems);
            Assert.True(result.RealtimeCapabilityAvailable);
            Assert.Equal("realtime_session_started", result.RealtimeItems[0].ItemType);
            Assert.Equal(Path.GetFullPath(statePath), result.StateSourcePath);
            Assert.Equal(Path.GetFullPath(historyPath), result.HistorySourcePath);
            Assert.Equal(stateBefore, File.GetLastWriteTimeUtc(statePath));
            Assert.Equal(historyBefore, File.GetLastWriteTimeUtc(historyPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SearchThreads_PreservesMissingFieldsAndFiltersSourceRows()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-search-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, title TEXT, created_at INTEGER, updated_at INTEGER, archived INTEGER); INSERT INTO threads VALUES ('one', 'Alpha', 1700000000, 1700000001, 0); INSERT INTO threads VALUES ('two', NULL, 1700000000, 1700000002, 1);";
                await command.ExecuteNonQueryAsync();
            }

            var service = new CodexThreadObservabilityService(directory.FullName);
            var all = await service.SearchThreadsAsync(null, 10, CancellationToken.None);
            var alpha = Assert.Single(all, entry => entry.ThreadId == "one");
            Assert.Equal("Alpha", alpha.DisplayName);
            Assert.Null(alpha.Model);
            Assert.True(all.Single(entry => entry.ThreadId == "two").Archived!.Value);

            var filtered = await service.SearchThreadsAsync("alpha", 10, CancellationToken.None);
            Assert.Equal("one", Assert.Single(filtered).ThreadId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SearchThreads_RetainsAllSourceObservationsAndReportsExplicitReconciliation()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-search-sources-");
        try
        {
            var olderPath = Path.Combine(directory.FullName, "state_5.sqlite");
            var newerPath = Path.Combine(directory.FullName, "state_6.sqlite");
            await CreateStateAsync(olderPath);
            await CreateStateAsync(newerPath);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .SearchThreadsAsync(null, 10, CancellationToken.None);

            var entry = Assert.Single(result.Entries);
            Assert.Equal(2, result.SourceObservations.Count);
            Assert.Equal(2, entry.Observations.Count);
            Assert.Equal(Path.GetFullPath(newerPath), entry.Preferred.SourcePath);
            Assert.Contains("explicit catalog policy", entry.SelectionRationale, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source generation", result.ReconciliationPolicy, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadThread_MergesEveryHistorySourceAndAccumulatesCapabilities()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-history-sources-");
        try
        {
            var firstPath = Path.Combine(directory.FullName, "thread_history_1.sqlite");
            var secondPath = Path.Combine(directory.FullName, "thread_history_2.sqlite");
            await CreateHistoryAsync(firstPath);
            await CreateHistoryAsync(secondPath);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .ReadThreadAsync("thread-1", CancellationToken.None);

            Assert.Equal(2, result.HistorySources.Count);
            Assert.Equal(Path.GetFullPath(secondPath), result.HistorySourcePath);
            Assert.Equal(4, result.Turns.Count);
            Assert.Equal(4, result.Items.Count);
            Assert.Equal(2, result.RealtimeItems.Count);
            Assert.Equal(2, result.HistorySources.Select(source => source.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.True(result.TurnsCapabilityAvailable);
            Assert.True(result.ItemsCapabilityAvailable);
            Assert.True(result.RealtimeCapabilityAvailable);
            Assert.Contains("source-qualified", result.HistoryReconciliationPolicy, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("all 2 readable history source", result.HistorySourceSelectionRationale, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadThread_DoesNotLoseHistoryCapabilitiesAfterLaterNonHistorySource()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-history-capabilities-");
        try
        {
            var historyPath = Path.Combine(directory.FullName, "a.sqlite");
            var statePath = Path.Combine(directory.FullName, "z.sqlite");
            await CreateHistoryAsync(historyPath);
            await CreateStateAsync(statePath);
            File.SetLastWriteTimeUtc(historyPath, DateTime.UtcNow.AddMinutes(1));

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .ReadThreadAsync("thread-1", CancellationToken.None);

            Assert.True(result.TurnsCapabilityAvailable);
            Assert.True(result.ItemsCapabilityAvailable);
            Assert.True(result.RealtimeCapabilityAvailable);
            Assert.Single(result.HistorySources);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadThread_ExposesSourceTruncationForEveryHistoryLane()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-history-truncation-");
        try
        {
            var path = Path.Combine(directory.FullName, "thread_history_1.sqlite");
            await CreateLargeHistoryAsync(path);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .ReadThreadAsync("thread-1", CancellationToken.None);

            Assert.Equal(5_000, result.Turns.Count);
            Assert.Equal(5_000, result.Items.Count);
            Assert.Equal(5_000, result.RealtimeItems.Count);
            Assert.True(result.TurnsTruncated);
            Assert.True(result.ItemsTruncated);
            Assert.True(result.RealtimeItemsTruncated);
            Assert.Equal(3, result.CoverageWarnings.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SearchThreads_ExposesCatalogTruncation()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-catalog-truncation-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateLargeCatalogAsync(path);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .SearchThreadsAsync(null, 1, CancellationToken.None);

            Assert.True(result.SourceRowsTruncated);
            Assert.Contains(result.CoverageWarnings, warning => warning.Contains("threads search was truncated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadThread_ExposesSourceTruncationForStateCollections()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-state-truncation-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateLargeStateAsync(path);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .ReadThreadAsync("thread-1", CancellationToken.None);

            Assert.True(result.ProjectRootsTruncated);
            Assert.True(result.DynamicToolsTruncated);
            Assert.True(result.SpawnEdgesTruncated);
            Assert.Equal(5_000, result.Project!.OrderedRoots.Count);
            Assert.Equal(5_000, result.DynamicTools.Count);
            Assert.Equal(5_000, result.SpawnEdges.Count);
            Assert.Equal(3, result.CoverageWarnings.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CatalogEntry_DisplayNameKeepsLegacyAndPaginatedSemanticsDistinct()
    {
        var legacy = new TajsTokens.Core.Models.CodexThreadCatalogEntry(
            "legacy", "state.sqlite", null, null, null, "Legacy title", "Paginated name", null, null, null,
            null, null, null, null, null, null, null, "legacy", null, null, null, null, null, null, null, null, null, null, null, null);
        var paginated = legacy with { HistoryMode = "paginated" };

        Assert.Equal("Legacy title", legacy.DisplayName);
        Assert.Equal("Paginated name", paginated.DisplayName);
    }

    [Fact]
    public async Task ReadThread_ReportsAbsentOptionalStateCapabilitiesSeparatelyFromEmptyRows()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-thread-capabilities-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_5.sqlite");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, title TEXT); INSERT INTO threads VALUES ('thread-1', 'Only state');";
                await command.ExecuteNonQueryAsync();
            }

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .ReadThreadAsync("thread-1", CancellationToken.None);

            Assert.Equal(false, result.ProjectCapabilityAvailable);
            Assert.Equal(false, result.DynamicToolsCapabilityAvailable);
            Assert.Equal(false, result.SpawnEdgesCapabilityAvailable);
            Assert.Null(result.Project);
            Assert.Empty(result.DynamicTools);
            Assert.Empty(result.SpawnEdges);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static async Task CreateStateAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads(
                id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL, created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL, source TEXT NOT NULL, model_provider TEXT NOT NULL,
                cwd TEXT NOT NULL, title TEXT NOT NULL, sandbox_policy TEXT NOT NULL,
                approval_mode TEXT NOT NULL, tokens_used INTEGER NOT NULL, archived INTEGER NOT NULL,
                git_sha TEXT, git_branch TEXT, git_origin_url TEXT, model TEXT,
                reasoning_effort TEXT, agent_nickname TEXT, agent_role TEXT, agent_path TEXT,
                memory_mode TEXT, history_mode TEXT, name TEXT, preview TEXT,
                first_user_message TEXT, thread_source TEXT, is_pinned INTEGER,
                thread_section_id TEXT, section_position INTEGER, project_id TEXT,
                created_at_ms INTEGER, updated_at_ms INTEGER, recency_at_ms INTEGER);
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT NOT NULL, metadata TEXT, position INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER);
            CREATE TABLE project_roots(project_id TEXT NOT NULL, position INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY(project_id, position));
            CREATE TABLE thread_sections(id TEXT PRIMARY KEY, name TEXT NOT NULL, appearance TEXT);
            CREATE TABLE thread_dynamic_tools(thread_id TEXT NOT NULL, position INTEGER NOT NULL, name TEXT NOT NULL, description TEXT NOT NULL, input_schema TEXT NOT NULL, defer_loading INTEGER, namespace TEXT, PRIMARY KEY(thread_id, position));
            CREATE TABLE thread_spawn_edges(parent_thread_id TEXT NOT NULL, child_thread_id TEXT PRIMARY KEY, status TEXT NOT NULL);
            INSERT INTO threads VALUES ('thread-1','rollout.jsonl',1700000000,1700000100,'cli','openai','C:/repo','Title','{"type":"workspace"}','on-request',42,0,'abc','feature/demo','https://github.com/example/repo','gpt-5.6-luna','xhigh','Ada','root','C:/agent','enabled','paginated','Friendly name','Preview text','First request','cli',1,'section-1',3,'project-1',1700000000000,1700000100000,1700000100000);
            INSERT INTO projects VALUES ('project-1','Project One','{"kind":"demo"}',1,1700000000000,1700000100000);
            INSERT INTO project_roots VALUES ('project-1',0,'C:/repo'), ('project-1',1,'C:/repo/docs');
            INSERT INTO thread_sections VALUES ('section-1','Tools','{"accent":"blue"}');
            INSERT INTO thread_dynamic_tools VALUES ('thread-1',0,'search','Search files','{"type":"object"}',1,'fs');
            INSERT INTO thread_spawn_edges VALUES ('thread-1','thread-2','open'), ('thread-2','thread-3','closed');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateHistoryAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE thread_turns(thread_id TEXT, turn_id TEXT, rollout_ordinal INTEGER, status TEXT, error_json TEXT, started_at INTEGER, completed_at INTEGER, duration_ms INTEGER, first_user_item_id TEXT, final_agent_item_id TEXT, rollout_byte_offset INTEGER, rollout_end_ordinal INTEGER, rollout_end_byte_offset INTEGER, PRIMARY KEY(thread_id, turn_id));
            CREATE TABLE thread_items(thread_id TEXT, turn_id TEXT, item_id TEXT, rollout_ordinal INTEGER, created_at_ms INTEGER, item_json TEXT, item_type TEXT, updated_at_ordinal INTEGER, PRIMARY KEY(thread_id, turn_id, item_id));
            CREATE TABLE thread_realtime_items(thread_id TEXT, item_id TEXT, rollout_ordinal INTEGER, created_at_ms INTEGER, item_type TEXT, item_json TEXT, PRIMARY KEY(thread_id, item_id));
            INSERT INTO thread_turns VALUES ('thread-1','turn-1',1,'completed',NULL,1700000000000,1700000001000,1000,'item-1','item-2',0,2,500);
            INSERT INTO thread_turns VALUES ('thread-1','turn-2',3,'inProgress',NULL,1700000002000,NULL,NULL,NULL,NULL,501,NULL,NULL);
            INSERT INTO thread_items VALUES ('thread-1','turn-1','item-1',1,1700000000000,'{"role":"user","text":"hello"}','userMessage',1);
            INSERT INTO thread_items VALUES ('thread-1','turn-1','item-2',2,1700000001000,'{"role":"assistant","text":"hi"}','agentMessage',2);
            INSERT INTO thread_realtime_items VALUES ('thread-1','rt-1',4,1700000003000,'realtime_session_started','{"kind":"realtime"}');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLargeHistoryAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE thread_turns(thread_id TEXT, turn_id TEXT, rollout_ordinal INTEGER, status TEXT, error_json TEXT, started_at INTEGER, completed_at INTEGER, duration_ms INTEGER, first_user_item_id TEXT, final_agent_item_id TEXT, rollout_byte_offset INTEGER, rollout_end_ordinal INTEGER, rollout_end_byte_offset INTEGER, PRIMARY KEY(thread_id, turn_id));
            CREATE TABLE thread_items(thread_id TEXT, turn_id TEXT, item_id TEXT, rollout_ordinal INTEGER, created_at_ms INTEGER, item_json TEXT, item_type TEXT, updated_at_ordinal INTEGER, PRIMARY KEY(thread_id, turn_id, item_id));
            CREATE TABLE thread_realtime_items(thread_id TEXT, item_id TEXT, rollout_ordinal INTEGER, created_at_ms INTEGER, item_type TEXT, item_json TEXT, PRIMARY KEY(thread_id, item_id));
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5001)
            INSERT INTO thread_turns(thread_id, turn_id, rollout_ordinal, status) SELECT 'thread-1', 'turn-' || n, n, 'completed' FROM seq;
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5001)
            INSERT INTO thread_items(thread_id, turn_id, item_id, rollout_ordinal, item_type, item_json) SELECT 'thread-1', 'turn-' || n, 'item-' || n, n, 'agentMessage', '{}' FROM seq;
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5001)
            INSERT INTO thread_realtime_items(thread_id, item_id, rollout_ordinal, item_type, item_json) SELECT 'thread-1', 'realtime-' || n, n, 'realtime', '{}' FROM seq;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLargeCatalogAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads(id TEXT PRIMARY KEY, title TEXT, created_at INTEGER, updated_at INTEGER, archived INTEGER);
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 10001)
            INSERT INTO threads(id, title, created_at, updated_at, archived) SELECT 'thread-' || n, 'Thread ' || n, 1700000000 + n, 1700000000 + n, 0 FROM seq;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLargeStateAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE threads(id TEXT PRIMARY KEY, project_id TEXT);
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT);
            CREATE TABLE project_roots(project_id TEXT, position INTEGER, path TEXT, PRIMARY KEY(project_id, position));
            CREATE TABLE thread_dynamic_tools(thread_id TEXT, position INTEGER, name TEXT, description TEXT, input_schema TEXT, defer_loading INTEGER, namespace TEXT, PRIMARY KEY(thread_id, position));
            CREATE TABLE thread_spawn_edges(parent_thread_id TEXT, child_thread_id TEXT PRIMARY KEY, status TEXT);
            INSERT INTO threads VALUES ('thread-1', 'project-1');
            INSERT INTO projects VALUES ('project-1', 'Project One');
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5001)
            INSERT INTO project_roots SELECT 'project-1', n, 'C:/repo/' || n FROM seq;
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5001)
            INSERT INTO thread_dynamic_tools SELECT 'thread-1', n, 'tool-' || n, '', '{}', 0, 'test' FROM seq;
            WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 5001)
            INSERT INTO thread_spawn_edges SELECT 'thread-1', 'child-' || n, 'open' FROM seq;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
