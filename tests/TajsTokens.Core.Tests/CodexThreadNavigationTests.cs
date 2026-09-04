using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexThreadNavigationTests
{
    [Fact]
    public async Task BrowseThreads_GroupsRootsAndRetainsRecursiveDescendantsUnderRootFamily()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-navigation-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_1.sqlite");
            await CreateNavigationDbAsync(path, """
                INSERT INTO threads(id, title, cwd, project_id, updated_at, archived, is_pinned) VALUES
                    ('root', 'Root', 'C:/repo', 'project-1', 1700000000, 0, 1),
                    ('child', 'Child', 'C:/other', 'project-2', 1700000001, 0, 0),
                    ('grandchild', 'Grandchild', 'C:/third', NULL, 1700000002, 0, 0);
                INSERT INTO projects(id, name) VALUES ('project-1', 'Project One');
                INSERT INTO thread_spawn_edges(parent_thread_id, child_thread_id, status) VALUES
                    ('root', 'child', 'open'), ('child', 'grandchild', 'open');
                """);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .BrowseThreadsAsync(new CodexThreadNavigationQuery(), CancellationToken.None);

            var group = Assert.Single(result.Groups);
            Assert.Equal(CodexThreadNavigationGroupKind.Project, group.Kind);
            Assert.Equal("Project · Project One", group.DisplayName);
            Assert.Equal(3, group.ThreadCount);
            var root = Assert.Single(group.RootThreads);
            Assert.Equal("root", root.ThreadId);
            Assert.Equal("child", Assert.Single(root.Children).ThreadId);
            Assert.Equal("grandchild", Assert.Single(root.Children[0].Children).ThreadId);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task BrowseThreads_SearchRetainsAncestorsAndMarksMissingParents()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-navigation-search-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_1.sqlite");
            await CreateNavigationDbAsync(path, """
                INSERT INTO threads(id, title, cwd, updated_at, archived) VALUES
                    ('root', 'Root', 'C:/repo', 1700000000, 0),
                    ('child', 'Needle', 'C:/repo', 1700000001, 0);
                INSERT INTO thread_spawn_edges(parent_thread_id, child_thread_id, status) VALUES
                    ('missing-parent', 'child', 'open');
                """);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .BrowseThreadsAsync(new CodexThreadNavigationQuery("needle"), CancellationToken.None);

            var group = Assert.Single(result.Groups);
            var node = Assert.Single(group.RootThreads);
            Assert.Equal("child", node.ThreadId);
            Assert.True(node.MissingParent);
            Assert.Contains(result.Warnings, warning => warning.Contains("missing parent", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task BrowseThreads_PureCycleRemainsVisibleWithCycleWarning()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-navigation-cycle-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_1.sqlite");
            await CreateNavigationDbAsync(path, """
                INSERT INTO threads(id, title, cwd, updated_at, archived) VALUES
                    ('a', 'A', 'C:/repo', 1700000000, 0),
                    ('b', 'B', 'C:/repo', 1700000001, 0),
                    ('c', 'C', 'C:/repo', 1700000002, 0);
                INSERT INTO thread_spawn_edges(parent_thread_id, child_thread_id, status) VALUES
                    ('a', 'b', 'open'), ('b', 'c', 'open'), ('c', 'a', 'open');
                """);

            var result = await new CodexThreadObservabilityService(directory.FullName)
                .BrowseThreadsAsync(new CodexThreadNavigationQuery(), CancellationToken.None);

            var group = Assert.Single(result.Groups);
            Assert.Equal(3, group.ThreadCount);
            Assert.Single(group.RootThreads);
            Assert.Contains(Flatten(group.RootThreads), node => node.CycleDetected);
            Assert.Contains(result.Warnings, warning => warning.Contains("cycle", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task BrowseThreads_ExcludesArchivedByDefaultAndCanIncludeThem()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-navigation-archived-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_1.sqlite");
            await CreateNavigationDbAsync(path, """
                INSERT INTO threads(id, title, cwd, updated_at, archived, is_pinned) VALUES
                    ('active', 'Active', 'C:/repo', 1700000000, 0, 0),
                    ('archived', 'Archived', 'C:/repo', 1700000001, 1, 1);
                """);

            var service = new CodexThreadObservabilityService(directory.FullName);
            var current = await service.BrowseThreadsAsync(new CodexThreadNavigationQuery(), CancellationToken.None);
            var all = await service.BrowseThreadsAsync(new CodexThreadNavigationQuery(IncludeArchived: true), CancellationToken.None);

            Assert.Equal(1, current.TotalThreadCount);
            Assert.Equal(2, all.TotalThreadCount);
            Assert.Equal("archived", all.Groups[0].RootThreads[0].ThreadId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task BrowseThreads_UsesWorkspaceAndUnassignedFallbackAndSearchesAgentIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-navigation-groups-");
        try
        {
            var path = Path.Combine(directory.FullName, "state_1.sqlite");
            await CreateNavigationDbAsync(path, """
                INSERT INTO threads(id, title, cwd, agent_nickname, agent_role, updated_at, archived, is_pinned) VALUES
                    ('workspace', 'Workspace thread', 'C:/repo', 'Ada', 'reviewer', 1700000000, 0, 1),
                    ('unassigned', 'Unassigned thread', NULL, NULL, NULL, 1700000001, 0, 0);
                """);

            var service = new CodexThreadObservabilityService(directory.FullName);
            var all = await service.BrowseThreadsAsync(new CodexThreadNavigationQuery(), CancellationToken.None);
            var searched = await service.BrowseThreadsAsync(new CodexThreadNavigationQuery("reviewer"), CancellationToken.None);

            Assert.Equal(2, all.Groups.Count);
            Assert.Equal(CodexThreadNavigationGroupKind.Workspace, all.Groups[0].Kind);
            Assert.Equal("Workspace · repo", all.Groups[0].DisplayName);
            Assert.Equal(CodexThreadNavigationGroupKind.Unassigned, all.Groups[1].Kind);
            Assert.Equal("Unassigned", all.Groups[1].DisplayName);
            Assert.Equal("workspace", Assert.Single(searched.Groups).RootThreads[0].ThreadId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    private static IEnumerable<CodexThreadNavigationNode> Flatten(IEnumerable<CodexThreadNavigationNode> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    private static async Task CreateNavigationDbAsync(string path, string inserts)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE threads(id TEXT PRIMARY KEY, title TEXT, cwd TEXT, project_id TEXT, agent_nickname TEXT, agent_role TEXT, updated_at INTEGER, archived INTEGER, is_pinned INTEGER);
            CREATE TABLE projects(id TEXT PRIMARY KEY, name TEXT);
            CREATE TABLE thread_spawn_edges(parent_thread_id TEXT, child_thread_id TEXT PRIMARY KEY, status TEXT);
            {inserts}
            """;
        await command.ExecuteNonQueryAsync();
    }
}
