using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexNativeSourcesServiceTests
{
    [Fact]
    public async Task ReadAsync_MapsNativeSourcesAndPreservesContentBoundaries()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-sources-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "memories_1.sqlite"), """
                CREATE TABLE _sqlx_migrations (version INTEGER);
                CREATE TABLE jobs (kind TEXT, job_key TEXT, status TEXT, worker_id TEXT, started_at INTEGER, finished_at INTEGER, lease_until INTEGER, retry_at INTEGER, retry_remaining INTEGER, last_error TEXT, input_watermark INTEGER, last_success_watermark INTEGER);
                CREATE TABLE stage1_outputs (thread_id TEXT, source_updated_at INTEGER, raw_memory TEXT, rollout_summary TEXT, rollout_slug TEXT, generated_at INTEGER, usage_count INTEGER, last_usage INTEGER, selected_for_phase2 INTEGER, selected_for_phase2_source_updated_at INTEGER);
                INSERT INTO _sqlx_migrations VALUES (4);
                INSERT INTO jobs VALUES ('memory_stage1', 'thread-1', 'done', 'worker-a', 10, 20, NULL, NULL, 0, NULL, 3, 3);
                INSERT INTO stage1_outputs VALUES ('thread-1', 4, 'private memory', 'private summary', 'rollout-1', 5, 2, 1, 1, 4);
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "goals_1.sqlite"), """
                CREATE TABLE thread_goals (thread_id TEXT, goal_id TEXT, objective TEXT, status TEXT, token_budget INTEGER, tokens_used INTEGER, time_used_seconds INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER);
                CREATE TABLE thread_goal_continuation_deferrals (thread_id TEXT);
                INSERT INTO thread_goals VALUES ('thread-1', 'goal-1', 'private objective', 'paused', 100, 4, 2, 10, 20);
                INSERT INTO thread_goal_continuation_deferrals VALUES ('thread-1');
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "queue_1.sqlite"), """
                CREATE TABLE queued_items (id TEXT, thread_id TEXT, payload_json TEXT, queue_order INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER);
                CREATE TABLE queued_thread_revisions (revision INTEGER, thread_id TEXT);
                INSERT INTO queued_items VALUES ('item-1', 'thread-1', '{"secret":true}', 2, 10, 11);
                INSERT INTO queued_thread_revisions VALUES (7, 'thread-1');
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "state_5.sqlite"), """
                CREATE TABLE thread_artifacts (id TEXT, thread_id TEXT, artifact_type TEXT, identity_key TEXT, payload TEXT, created_at INTEGER);
                INSERT INTO thread_artifacts VALUES ('artifact-1', 'thread-1', 'file', 'key-1', 'private payload', 12);
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "codex-dev.db"), """
                CREATE TABLE local_thread_catalog (host_id TEXT, thread_id TEXT, display_title TEXT, source_created_at REAL, source_updated_at REAL, cwd TEXT, source_kind TEXT, source_detail TEXT, model_provider TEXT, git_branch TEXT, observation_sequence INTEGER, missing_candidate INTEGER, thread_source TEXT, source_recency_at REAL, pending_observed_title INTEGER, project_id TEXT, conversation_origin TEXT);
                INSERT INTO local_thread_catalog VALUES ('host-1', 'thread-1', 'Local title', 1, 2, 'C:/private', 'local', NULL, 'provider', 'main', 3, 0, 'desktop', 2, 0, NULL, 'user');
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "codex-thread-summaries-dev.db"), """
                CREATE TABLE thread_turn_summaries (principal_key TEXT, host_key TEXT, thread_id TEXT, summary TEXT, compact_summary TEXT, compact_summary_turn_key TEXT, revision INTEGER, updated_at INTEGER);
                INSERT INTO thread_turn_summaries VALUES ('principal', 'host-1', 'thread-1', 'private summary', 'compact', 'turn-1', 2, 3);
                """);

            var snapshot = await new CodexNativeSourcesService(directory.FullName).ReadAsync();

            Assert.Equal(CodexNativeSourceAvailability.Available, snapshot.Memory.Source.Availability);
            Assert.Equal("4", snapshot.Memory.Source.SourceVersion);
            Assert.Single(snapshot.Memory.Jobs);
            var memory = Assert.Single(snapshot.Memory.Stage1Outputs);
            Assert.True(memory.HasRawMemory);
            Assert.True(memory.HasRolloutSummary);
            Assert.Equal("private objective", Assert.Single(snapshot.Goals.Goals).Objective);
            Assert.True(Assert.Single(snapshot.Goals.Goals).HasContinuationDeferral);
            Assert.Equal(7, Assert.Single(snapshot.Queue.Items).Revision);
            Assert.True(Assert.Single(snapshot.Queue.Items).HasPayload);
            Assert.True(Assert.Single(snapshot.Artifacts.Artifacts).HasPayload);
            Assert.Equal("Local title", Assert.Single(snapshot.DesktopCatalog.Entries).DisplayTitle);
            Assert.Equal("private summary", Assert.Single(snapshot.ThreadSummaries.Summaries).Summary);
            Assert.All(snapshot.Sources, source => Assert.Equal("8e3b180d49", source.UpstreamCorroborationCommit));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DistinguishesUnavailableEmptyAndUnsupportedSources()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-source-states-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "memories_1.sqlite"), "CREATE TABLE jobs (kind TEXT, job_key TEXT, status TEXT);");
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "goals_1.sqlite"), "CREATE TABLE thread_goals (thread_id TEXT);");

            var snapshot = await new CodexNativeSourcesService(directory.FullName).ReadAsync();

            Assert.Equal(CodexNativeSourceAvailability.Empty, snapshot.Memory.Source.Availability);
            Assert.Equal(CodexNativeSourceAvailability.Empty, snapshot.Goals.Source.Availability);
            Assert.Equal(CodexNativeSourceAvailability.Unavailable, snapshot.Queue.Source.Availability);
            Assert.Equal(CodexNativeSourceAvailability.Unavailable, snapshot.Artifacts.Source.Availability);
            Assert.Equal(CodexNativeSourceAvailability.Unavailable, snapshot.DesktopCatalog.Source.Availability);
            Assert.Equal(CodexNativeSourceAvailability.Unavailable, snapshot.ThreadSummaries.Source.Availability);
            Assert.Empty(snapshot.Goals.Goals);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_SchemaVariantWithMissingOptionalColumnsDoesNotCrash()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-source-variant-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "queue_1.sqlite"), """
                CREATE TABLE queued_items (id TEXT, thread_id TEXT);
                INSERT INTO queued_items VALUES ('item-1', 'thread-1');
                """);

            var snapshot = await new CodexNativeSourcesService(directory.FullName).ReadAsync();

            Assert.Equal(CodexNativeSourceAvailability.Available, snapshot.Queue.Source.Availability);
            var item = Assert.Single(snapshot.Queue.Items);
            Assert.Equal("item-1", item.Id);
            Assert.Equal(0, item.QueueOrder);
            Assert.False(item.HasPayload);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_NullOptionalBooleansRemainFalse()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-source-null-flags-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "memories_1.sqlite"), """
                CREATE TABLE stage1_outputs (thread_id TEXT, source_updated_at INTEGER, selected_for_phase2 INTEGER, generated_at INTEGER);
                INSERT INTO stage1_outputs VALUES ('thread-1', 10, NULL, 11);
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "codex-dev.db"), """
                CREATE TABLE local_thread_catalog (host_id TEXT, thread_id TEXT, display_title TEXT, source_kind TEXT, missing_candidate INTEGER, pending_observed_title INTEGER);
                INSERT INTO local_thread_catalog VALUES ('host-1', 'thread-1', 'Title', 'local', NULL, NULL);
                """);

            var snapshot = await new CodexNativeSourcesService(directory.FullName).ReadAsync();

            Assert.False(Assert.Single(snapshot.Memory.Stage1Outputs).SelectedForPhase2);
            var entry = Assert.Single(snapshot.DesktopCatalog.Entries);
            Assert.False(entry.MissingCandidate);
            Assert.False(entry.PendingObservedTitle);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_OrdersBeforeBoundingAndJoinsRelationsBySelectedIds()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-source-bounds-");
        try
        {
            var migrations = string.Join(Environment.NewLine, Enumerable.Range(1, 300).Select(version => $"INSERT INTO _sqlx_migrations VALUES ({version});"));
            var jobs = string.Join(Environment.NewLine, Enumerable.Range(1, 300).Select(index => $"INSERT INTO jobs VALUES ('memory', 'job-{index}', 'done', {index});"));
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "memories_1.sqlite"), $"""
                CREATE TABLE _sqlx_migrations (version INTEGER);
                CREATE TABLE jobs (kind TEXT, job_key TEXT, status TEXT, started_at INTEGER);
                {migrations}
                {jobs}
                """);

            var goals = string.Join(Environment.NewLine, Enumerable.Range(1, 300).Select(index => $"INSERT INTO thread_goals VALUES ('thread-{index}', 'goal-{index}', 'objective', 'active', {index}, 1, 1, {index}, {index});"));
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "goals_1.sqlite"), $"""
                CREATE TABLE thread_goals (thread_id TEXT, goal_id TEXT, objective TEXT, status TEXT, token_budget INTEGER, tokens_used INTEGER, time_used_seconds INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER);
                CREATE TABLE thread_goal_continuation_deferrals (thread_id TEXT);
                {goals}
                {string.Join(Environment.NewLine, Enumerable.Range(1, 299).Select(index => $"INSERT INTO thread_goal_continuation_deferrals VALUES ('other-{index}');"))}
                INSERT INTO thread_goal_continuation_deferrals VALUES ('thread-300');
                """);

            var queue = string.Join(Environment.NewLine, Enumerable.Range(1, 300).Select(index => $"INSERT INTO queued_items VALUES ('item-{index}', 'thread-{index}', NULL, {index}, {index}, {index});"));
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "queue_1.sqlite"), $"""
                CREATE TABLE queued_items (id TEXT, thread_id TEXT, payload_json TEXT, queue_order INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER);
                CREATE TABLE queued_thread_revisions (revision INTEGER, thread_id TEXT);
                {queue}
                {string.Join(Environment.NewLine, Enumerable.Range(1, 299).Select(index => $"INSERT INTO queued_thread_revisions VALUES ({index}, 'other-{index}');"))}
                INSERT INTO queued_thread_revisions VALUES (77, 'thread-1');
                """);

            var snapshot = await new CodexNativeSourcesService(directory.FullName).ReadAsync();

            Assert.Equal("300", snapshot.Memory.Source.SourceVersion);
            Assert.Equal("job-300", snapshot.Memory.Jobs[0].JobKey);
            Assert.Equal(250, snapshot.Memory.Jobs.Count);
            Assert.True(snapshot.Memory.Source.HasMoreRows);
            var goal = snapshot.Goals.Goals[0];
            Assert.Equal("thread-300", goal.ThreadId);
            Assert.True(goal.HasContinuationDeferral);
            Assert.Equal(250, snapshot.Goals.Goals.Count);
            Assert.True(snapshot.Goals.Source.HasMoreRows);
            var item = snapshot.Queue.Items[0];
            Assert.Equal("thread-1", item.ThreadId);
            Assert.Equal(77, item.Revision);
            Assert.Equal(250, snapshot.Queue.Items.Count);
            Assert.True(snapshot.Queue.Source.HasMoreRows);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DesktopSourcesReportIncompatibleCapabilities()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-desktop-variant-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "codex-dev.db"), "CREATE TABLE catalog_v2 (id TEXT);");
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "codex-thread-summaries-dev.db"), "CREATE TABLE summaries_v2 (id TEXT);");

            var snapshot = await new CodexNativeSourcesService(directory.FullName).ReadAsync();

            Assert.Equal(CodexNativeSourceAvailability.Unsupported, snapshot.DesktopCatalog.Source.Availability);
            Assert.Equal(CodexNativeSourceAvailability.Unsupported, snapshot.ThreadSummaries.Source.Availability);
            Assert.Empty(snapshot.DesktopCatalog.Entries);
            Assert.Empty(snapshot.ThreadSummaries.Summaries);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_PrefersInstalledSourceOverSnapshotWhenBothExist()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-source-precedence-");
        var snapshotDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "private"));
        var codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, "codex"));
        try
        {
            await CreateDatabaseAsync(Path.Combine(codexHome.FullName, "memories_1.sqlite"), "CREATE TABLE jobs (kind TEXT, job_key TEXT, status TEXT); INSERT INTO jobs VALUES ('memory', 'installed', 'done');");
            await CreateDatabaseAsync(Path.Combine(snapshotDirectory.FullName, "memories_1_snapshot.sqlite"), "CREATE TABLE jobs (kind TEXT, job_key TEXT, status TEXT); INSERT INTO jobs VALUES ('memory', 'snapshot', 'done');");

            var source = await new CodexNativeSourcesService(codexHome.FullName, snapshotDirectory.FullName).ReadMemoryAsync();

            Assert.Equal("installed", Assert.Single(source.Jobs).JobKey);
            Assert.Equal("codex-home", source.Source.DiscoveryKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_SourceErrorsPreserveDiscoveryKind()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-source-error-provenance-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "memories_1.sqlite"), "not a sqlite database");

            var source = await new CodexNativeSourcesService(directory.FullName).ReadMemoryAsync();

            Assert.Equal(CodexNativeSourceAvailability.Error, source.Source.Availability);
            Assert.Equal("codex-home", source.Source.DiscoveryKind);
            Assert.Equal(Path.Combine(directory.FullName, "memories_1.sqlite"), source.Source.DatabasePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_CheckedInCodexSnapshotsRemainInspectable()
    {
        var snapshotDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "private"));
        if (!Directory.Exists(snapshotDirectory))
        {
            return;
        }

        var snapshot = await new CodexNativeSourcesService(snapshotDirectory).ReadAsync();

        Assert.All(snapshot.Sources, source => Assert.NotEqual(CodexNativeSourceAvailability.Error, source.Availability));
        Assert.Equal(CodexNativeSourceAvailability.Empty, snapshot.Artifacts.Source.Availability);
        Assert.True(snapshot.Memory.Jobs.Count >= 0);
    }

    private static async Task CreateDatabaseAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
