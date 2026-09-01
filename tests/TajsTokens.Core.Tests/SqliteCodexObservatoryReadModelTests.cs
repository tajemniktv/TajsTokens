using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexObservatoryReadModelTests
{
    [Fact]
    public async Task Queries_FilterBeforeLimit_AndScopeTopologyAndStorageToSelectedSession()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-observatory-read-");
        var databasePath = Path.Combine(directory.FullName, "telemetry.db");
        var now = new DateTimeOffset(2026, 9, 1, 5, 0, 0, TimeSpan.Zero);

        try
        {
            var repository = new SqliteTelemetryRepository(databasePath);
            var store = new SqliteCodexObservatoryStore(databasePath);
            var readModel = new SqliteCodexObservatoryReadModel(databasePath);
            await repository.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);

            var root = new CodexSession("root", null, "needle-repository", now.AddHours(-3), now.AddHours(-2), "completed");
            var child = new CodexSession("child", null, "needle-repository", now.AddHours(-2), now.AddHours(-1), "completed");
            var unrelated = new CodexSession("unrelated", null, "other-repository", now.AddHours(-1), now, "running");
            await store.UpsertSessionAsync(root, CancellationToken.None);
            await store.UpsertSessionAsync(child, CancellationToken.None);
            await store.UpsertSessionAsync(unrelated, CancellationToken.None);

            await store.UpsertAgentAsync(new Agent("root", "root", "Root agent", AgentRuntimeState.Completed, now.AddHours(-2), "model-root"), CancellationToken.None);
            await store.UpsertAgentAsync(new Agent("child", "child", "Needle worker", AgentRuntimeState.Completed, now.AddHours(-1), "model-child"), CancellationToken.None);
            await store.UpsertAgentAsync(new Agent("unrelated", "unrelated", "Other agent", AgentRuntimeState.Running, now, "model-other"), CancellationToken.None);
            await store.UpsertAgentRelationshipAsync(new AgentRelationship("root", "child", now.AddHours(-2)), CancellationToken.None);

            await store.UpsertRolloutFileAsync("root-source", @"C:\rollouts\root.jsonl", "root", 100, now, CancellationToken.None);
            await store.UpsertRolloutFileAsync("child-source", @"C:\rollouts\child.jsonl", "child", 10, now, CancellationToken.None);
            await store.UpsertRolloutFileAsync("other-source", @"C:\rollouts\other.jsonl", "unrelated", 10_000, now, CancellationToken.None);

            // The newest global row is unrelated, but search is applied in SQLite before LIMIT.
            var search = await readModel.SearchSessionsAsync("Needle worker", 1, CancellationToken.None);
            Assert.Equal("child", Assert.Single(search).SessionId);

            var topology = await readModel.GetAgentTopologyAsync("child", CancellationToken.None);
            Assert.Equal(["root", "child"], topology.Agents.Select(agent => agent.AgentId).ToArray());
            Assert.Single(topology.Relationships);
            Assert.DoesNotContain(topology.Agents, agent => agent.AgentId == "unrelated");

            // The unrelated file is globally much larger, but selected-session scoping happens in SQL
            // before LIMIT so a small selected rollout cannot disappear below a global top-N cutoff.
            var storage = await readModel.GetSessionStorageAsync("child", 1, CancellationToken.None);
            var selectedStorage = Assert.Single(storage);
            Assert.Equal("child", selectedStorage.SessionId);
            Assert.Equal(10, selectedStorage.SizeBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }
}
