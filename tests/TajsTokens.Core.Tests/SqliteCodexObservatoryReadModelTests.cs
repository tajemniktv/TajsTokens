using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexObservatoryReadModelTests
{
    [Fact]
    public async Task Queries_FilterBeforeLimit_AndPreserveSessionAggregatesTopologyAndStorage()
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

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None);
                var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO codex_native_token_events(
                        source_event_id, source_file, session_id, agent_id, observed_at_utc, model, reasoning_effort,
                        counter_epoch, uncached_input_tokens, cache_read_tokens, cache_write_tokens,
                        non_reasoning_output_tokens, reasoning_output_tokens, reported_total_tokens)
                    VALUES
                        ('tok-1', 'child.jsonl', 'child', 'child', '2026-09-01T04:00:00.0000000+00:00', 'model-child', 'high', 0, 10, 20, 3, 4, 5, 42),
                        ('tok-2', 'child.jsonl', 'child', 'child', '2026-09-01T04:30:00.0000000+00:00', 'model-child', 'high', 0, 1, 2, 0, 6, 7, 58);

                    INSERT INTO context_observations(
                        event_id, session_id, agent_id, observed_at_utc, model, input_tokens,
                        context_window_tokens, is_compaction, record_bytes)
                    VALUES
                        ('ctx-1', 'child', 'child', '2026-09-01T04:10:00.0000000+00:00', 'model-child', 75, 100, 1, 20),
                        ('ctx-2', 'child', 'child', '2026-09-01T04:20:00.0000000+00:00', 'model-child', 90, 100, 0, 20);

                    INSERT INTO rollout_records(
                        source_record_id, source_identity, file_path, session_id, event_class, record_bytes, observed_at_utc)
                    VALUES('record-1', 'child-source', 'child.jsonl', 'child', 'token_count', 123, '2026-09-01T04:30:00.0000000+00:00');

                    INSERT INTO usage_events(event_id, session_id, timestamp_utc, event_type, summary, token_delta)
                    VALUES
                        ('usage-1', 'child', '2026-09-01T04:00:00.0000000+00:00', 'token_count', 'fixture', NULL),
                        ('usage-2', 'child', '2026-09-01T04:30:00.0000000+00:00', 'task_complete', 'fixture', NULL);
                    """;
                await seed.ExecuteNonQueryAsync(CancellationToken.None);
            }

            // The newest global row is unrelated, but search is applied in SQLite before LIMIT.
            var search = await readModel.SearchSessionsAsync("Needle worker", 1, CancellationToken.None);
            var selected = Assert.Single(search);
            Assert.Equal("child", selected.SessionId);
            Assert.Equal("root", selected.ParentSessionId);
            Assert.Equal("model-child", selected.Model);
            Assert.Equal(11, selected.NativeTokens.UncachedInput);
            Assert.Equal(22, selected.NativeTokens.CacheRead);
            Assert.Equal(3, selected.NativeTokens.CacheWrite);
            Assert.Equal(10, selected.NativeTokens.NonReasoningOutput);
            Assert.Equal(12, selected.NativeTokens.ReasoningOutput);
            Assert.Equal(100, selected.NativeTokens.ReportedTotal);
            Assert.Equal(1, selected.CompactionCount);
            Assert.Equal(90, selected.PeakContextPercent);
            Assert.Equal(123, selected.RolloutBytes);
            Assert.Equal(2, selected.TimelineEventCount);

            var topology = await readModel.GetAgentTopologyAsync("child", CancellationToken.None);
            Assert.Equal(new[] { "root", "child" }, topology.Agents.Select(agent => agent.AgentId).ToArray());
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

    [Fact]
    public async Task Initialize_UpgradesV2WithObservedTimeIndex()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-observatory-v3-");
        var databasePath = Path.Combine(directory.FullName, "telemetry.db");

        try
        {
            var repository = new SqliteTelemetryRepository(databasePath);
            var seedStore = new SqliteCodexObservatoryStore(databasePath);
            await repository.InitializeAsync(CancellationToken.None);
            await seedStore.InitializeAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                await connection.OpenAsync(CancellationToken.None);
                var downgrade = connection.CreateCommand();
                downgrade.CommandText = """
                    DROP INDEX IF EXISTS idx_native_tokens_observed_time;
                    UPDATE observatory_schema
                    SET version = 2
                    WHERE component = 'codex-observatory';
                    """;
                await downgrade.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var upgradedStore = new SqliteCodexObservatoryStore(databasePath);
            await upgradedStore.InitializeAsync(CancellationToken.None);

            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await verify.OpenAsync(CancellationToken.None);

            var versionCommand = verify.CreateCommand();
            versionCommand.CommandText = "SELECT version FROM observatory_schema WHERE component = 'codex-observatory';";
            Assert.Equal(3L, (long)(await versionCommand.ExecuteScalarAsync(CancellationToken.None))!);

            var indexCommand = verify.CreateCommand();
            indexCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'idx_native_tokens_observed_time';";
            Assert.Equal(1L, (long)(await indexCommand.ExecuteScalarAsync(CancellationToken.None))!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }
}
