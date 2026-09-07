using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexNativeSourcesServiceTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000_000)]
    public void LogTimestamp_InvalidNanosecondsDoNotBecomeAnotherInstant(long nanoseconds)
    {
        var entry = new CodexLogEntry(1, 10, nanoseconds, "WARN", "test", null, null, null, null, null, false, null, null);
        Assert.Null(entry.TimestampUtc);
        Assert.Equal(nanoseconds, entry.TimestampNanoseconds);
    }

    [Fact]
    public async Task ReadAsync_ExposesAlternativesAndHonorsExplicitInstanceWithoutFallback()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-source-selection-");
        try
        {
            var older = Path.Combine(directory.FullName, "queue_1.sqlite");
            var newer = Path.Combine(directory.FullName, "queue_2.sqlite");
            await CreateDatabaseAsync(older, "CREATE TABLE queued_items (id TEXT, thread_id TEXT); INSERT INTO queued_items VALUES ('older', 't');");
            await CreateDatabaseAsync(newer, "CREATE TABLE queued_items (id TEXT, thread_id TEXT); INSERT INTO queued_items VALUES ('newer', 't');");
            var service = new CodexNativeSourcesService(directory.FullName);
            var automatic = await service.ReadAsync();
            Assert.Equal("newer", Assert.Single(automatic.Queue.Items).Id);
            var selection = Assert.Single(automatic.Selections, value => value.Kind == CodexNativeSourceKind.Queue);
            Assert.Equal(2, selection.Candidates.Count);
            Assert.Equal(newer, selection.SelectedPath);
            Assert.Contains("not semantic authority", selection.Rationale);

            var query = new CodexNativeSourcesQuery
            {
                SelectedPaths = new Dictionary<CodexNativeSourceKind, string> { [CodexNativeSourceKind.Queue] = older }
            };
            var chosen = await service.ReadAsync(query);
            Assert.Equal("older", Assert.Single(chosen.Queue.Items).Id);
            Assert.Equal(older, chosen.Queue.Source.DatabasePath);
            // A disappeared explicit selection must not turn into a successful read from queue_2.
            File.Delete(older);
            var disappeared = await service.ReadAsync(query);
            Assert.Equal(CodexNativeSourceAvailability.Unavailable, disappeared.Queue.Source.Availability);
            Assert.Empty(disappeared.Queue.Items);
            Assert.Equal(older, Assert.Single(disappeared.Selections, value => value.Kind == CodexNativeSourceKind.Queue).SelectedPath);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReadAsync_DoesNotHideBrokenPreferredSourceBehindReadableAlternative()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-source-error-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "queue_1.sqlite"), "CREATE TABLE queued_items (id TEXT, thread_id TEXT);");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "queue_2.sqlite"), "not a SQLite database");
            var result = await new CodexNativeSourcesService(directory.FullName).ReadAsync();
            Assert.Equal(CodexNativeSourceAvailability.Error, result.Queue.Source.Availability);
            Assert.Equal(2, Assert.Single(result.Selections, value => value.Kind == CodexNativeSourceKind.Queue).Candidates.Count);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReadAsync_PreservesZeroFalseAndUnknownWithoutOverflowOrInventedDeferrals()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-source-values-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "memories_1.sqlite"), """
                CREATE TABLE jobs (kind TEXT, job_key TEXT, status TEXT, retry_remaining INTEGER);
                INSERT INTO jobs VALUES ('stage1', 't', 'future_status', 9223372036854775807);
                CREATE TABLE stage1_outputs (thread_id TEXT, source_updated_at INTEGER, generated_at INTEGER, selected_for_phase2 INTEGER, raw_memory TEXT);
                INSERT INTO stage1_outputs VALUES ('zero', 0, 0, 0, NULL);
                INSERT INTO stage1_outputs VALUES ('unknown', NULL, 'not-an-integer', 9, NULL);
                """);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "goals_1.sqlite"), """
                CREATE TABLE thread_goals (thread_id TEXT, goal_id TEXT, status TEXT);
                INSERT INTO thread_goals VALUES ('t', 'g', 'future_status');
                CREATE TABLE thread_goal_continuation_deferrals (future_key TEXT);
                """);
            var result = await new CodexNativeSourcesService(directory.FullName).ReadAsync();
            Assert.Null(Assert.Single(result.Memory.Jobs).RetryRemaining);
            var zero = Assert.Single(result.Memory.Stage1Outputs, value => value.ThreadId == "zero");
            Assert.Equal(0L, zero.SourceUpdatedAt);
            Assert.False(zero.SelectedForPhase2);
            Assert.False(zero.HasRawMemory); // present column, NULL payload
            Assert.Null(zero.HasRolloutSummary); // missing column is different
            var unknown = Assert.Single(result.Memory.Stage1Outputs, value => value.ThreadId == "unknown");
            Assert.Null(unknown.SourceUpdatedAt);
            Assert.Null(unknown.GeneratedAt);
            Assert.Null(unknown.SelectedForPhase2);
            var goal = Assert.Single(result.Goals.Goals);
            Assert.Equal("future_status", goal.Status);
            Assert.Null(goal.TokensUsed);
            Assert.Null(goal.HasContinuationDeferral);
            Assert.Contains(result.Goals.Source.Warnings, value => value.Contains("thread_goal_continuation_deferrals"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReadAsync_ReportsRejectedRowsAndRetainsConflictingRevisionObservations()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-source-conflict-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "queue_1.sqlite"), """
                CREATE TABLE queued_items (id TEXT, thread_id TEXT);
                INSERT INTO queued_items VALUES ('item', 't');
                INSERT INTO queued_items VALUES (NULL, 't');
                CREATE TABLE queued_thread_revisions (thread_id TEXT, revision INTEGER);
                INSERT INTO queued_thread_revisions VALUES ('t', 1), ('t', 2);
                """);
            var result = await new CodexNativeSourcesService(directory.FullName).ReadAsync();
            Assert.Null(Assert.Single(result.Queue.Items).Revision);
            Assert.Equal(2, result.Queue.Revisions.Count);
            Assert.Contains(result.Queue.Source.Warnings, value => value.Contains("1 row(s) omitted"));
            Assert.Contains(result.Queue.Source.Warnings, value => value.Contains("conflicting revisions"));
            Assert.Equal(CodexNativeSourceAvailability.Available, result.Queue.Source.Availability);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReadLogsAsync_PinsReturnedQueryToActualSourceAndKeepsBodiesOptIn()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-log-selection-");
        try
        {
            var path = Path.Combine(directory.FullName, "logs_1.sqlite");
            await CreateDatabaseAsync(path, """
                CREATE TABLE logs (id INTEGER, ts INTEGER, ts_nanos INTEGER, level TEXT, target TEXT, feedback_log_body TEXT);
                INSERT INTO logs VALUES (1, 10, 0, 'WARN', 'test', 'private fixture body');
                """);
            var service = new CodexNativeSourcesService(directory.FullName);
            var first = await service.ReadLogsAsync();
            Assert.Equal(path, first.Query.DatabasePath);
            Assert.Null(Assert.Single(first.Entries).Message);
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "logs_2.sqlite"), "CREATE TABLE logs (id INTEGER, ts INTEGER, ts_nanos INTEGER, level TEXT, target TEXT);");
            var pinned = await service.ReadLogsAsync(first.Query with { IncludeMessages = true });
            Assert.Equal(path, pinned.Source.DatabasePath);
            Assert.Equal("private fixture body", Assert.Single(pinned.Entries).Message);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ReadAsync_RelatedNativeKeysAreNotCollapsedByCase()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-key-case-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "queue_1.sqlite"), """
                CREATE TABLE queued_items (id TEXT, thread_id TEXT);
                INSERT INTO queued_items VALUES ('one', 't'), ('two', 'T');
                CREATE TABLE queued_thread_revisions (thread_id TEXT, revision INTEGER);
                INSERT INTO queued_thread_revisions VALUES ('t', 1), ('T', 2);
                """);
            var result = await new CodexNativeSourcesService(directory.FullName).ReadAsync();
            Assert.Equal(1, Assert.Single(result.Queue.Items, value => value.ThreadId == "t").Revision);
            Assert.Equal(2, Assert.Single(result.Queue.Items, value => value.ThreadId == "T").Revision);
            Assert.Equal(2, result.Queue.Revisions.Count);
        }
        finally { directory.Delete(recursive: true); }
    }

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
            Assert.Null(item.QueueOrder);
            Assert.Null(item.HasPayload);
            Assert.Contains(snapshot.Queue.Source.Warnings, warning => warning.Contains("queue_order"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_NullOptionalBooleansRemainUnknown()
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

            Assert.Null(Assert.Single(snapshot.Memory.Stage1Outputs).SelectedForPhase2);
            var entry = Assert.Single(snapshot.DesktopCatalog.Entries);
            Assert.Null(entry.MissingCandidate);
            Assert.Null(entry.PendingObservedTitle);
        }
        finally
        {
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
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadLogsAsync_MapsCurrentSchemaAndKeepsBodiesOptIn()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-logs-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "logs_2.sqlite"), """
                CREATE TABLE logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts INTEGER NOT NULL,
                    ts_nanos INTEGER NOT NULL,
                    level TEXT NOT NULL,
                    target TEXT NOT NULL,
                    feedback_log_body TEXT,
                    module_path TEXT,
                    file TEXT,
                    line INTEGER,
                    thread_id TEXT,
                    process_uuid TEXT,
                    estimated_bytes INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO logs VALUES (1, 100, 20, 'INFO', 'codex::old', 'older body', 'module::old', 'old.rs', 10, NULL, 'process-1', 11);
                INSERT INTO logs VALUES (2, 200, 30, 'ERROR', 'codex::new', 'new body', 'module::new', 'new.rs', 20, 'thread-1', 'process-1', 9);
                """);

            var service = new CodexNativeSourcesService(directory.FullName);
            var withoutBodies = await service.ReadLogsAsync(new CodexLogsQuery { PageSize = 10 });

            Assert.Equal(CodexNativeSourceAvailability.Available, withoutBodies.Source.Availability);
            Assert.Equal("8e3b180d49", withoutBodies.Source.UpstreamCorroborationCommit);
            Assert.Equal("feedback_log_body", withoutBodies.Capabilities.MessageColumn);
            Assert.True(withoutBodies.Capabilities.HasThreadId);
            Assert.Equal(2, withoutBodies.TotalMatchingRows);
            Assert.Equal(2, withoutBodies.Entries.Count);
            var newest = withoutBodies.Entries[0];
            Assert.Equal(2, newest.Id);
            Assert.Equal(200, newest.TimestampUnixSeconds);
            Assert.Equal("ERROR", newest.Level);
            Assert.Equal("thread-1", newest.ThreadId);
            Assert.Equal(9, newest.EstimatedBytes);
            Assert.True(newest.HasMessage);
            Assert.Null(newest.Message);

            var withBodies = await service.ReadLogsAsync(new CodexLogsQuery
            {
                PageSize = 10,
                IncludeMessages = true
            });

            Assert.Equal("new body", withBodies.Entries[0].Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadLogsAsync_FiltersAndBoundsAtTheSourceBoundary()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-logs-bounds-");
        try
        {
            var rows = string.Join(Environment.NewLine, Enumerable.Range(1, 300).Select(index =>
                $"INSERT INTO logs VALUES ({index}, {1000 + index}, {index * 1000}, '{(index % 2 == 0 ? "ERROR" : "INFO")}', 'target-{index % 3}', 'body-{index}', 'module-{index % 2}', 'source-{index}.rs', {index}, '{(index % 2 == 0 ? "thread-1" : "")}', 'process-1', 1);"));
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "logs_2.sqlite"), $"""
                CREATE TABLE logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts INTEGER NOT NULL,
                    ts_nanos INTEGER NOT NULL,
                    level TEXT NOT NULL,
                    target TEXT NOT NULL,
                    feedback_log_body TEXT,
                    module_path TEXT,
                    file TEXT,
                    line INTEGER,
                    thread_id TEXT,
                    process_uuid TEXT,
                    estimated_bytes INTEGER NOT NULL DEFAULT 0
                );
                {rows}
                """);

            var service = new CodexNativeSourcesService(directory.FullName);
            var firstPage = await service.ReadLogsAsync(new CodexLogsQuery { PageSize = 25 });

            Assert.Equal(25, firstPage.Entries.Count);
            Assert.True(firstPage.HasMoreRows);
            Assert.Equal(300, firstPage.TotalMatchingRows);
            Assert.Equal(300, firstPage.Entries[0].Id);
            Assert.Equal(276, firstPage.Entries[^1].Id);

            var filtered = await service.ReadLogsAsync(new CodexLogsQuery
            {
                PageSize = 100,
                Levels = ["error"],
                TargetContains = "target-1",
                ModulePathContains = "module-0",
                FromUtc = DateTimeOffset.FromUnixTimeSeconds(1100),
                ToUtcExclusive = DateTimeOffset.FromUnixTimeSeconds(1300),
                IncludeThreadless = false
            });

            Assert.NotEmpty(filtered.Entries);
            Assert.All(filtered.Entries, entry =>
            {
                Assert.Equal("ERROR", entry.Level);
                Assert.Contains("target-1", entry.Target, StringComparison.Ordinal);
                Assert.Equal("module-0", entry.ModulePath);
                Assert.Equal("thread-1", entry.ThreadId);
                Assert.InRange(entry.TimestampUnixSeconds, 1100, 1299);
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadLogsAsync_DistinguishesEmptyUnsupportedAndUnavailableSchemas()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-logs-states-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(directory.FullName, "logs_2.sqlite"), """
                CREATE TABLE logs (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, ts_nanos INTEGER NOT NULL, level TEXT NOT NULL, target TEXT NOT NULL);
                """);

            var empty = await new CodexNativeSourcesService(directory.FullName).ReadLogsAsync();
            Assert.Equal(CodexNativeSourceAvailability.Empty, empty.Source.Availability);
            Assert.Empty(empty.Entries);
            Assert.Equal(0, empty.TotalMatchingRows);
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        var legacyDirectory = Directory.CreateTempSubdirectory("tajstokens-native-logs-legacy-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(legacyDirectory.FullName, "logs_1.sqlite"), """
                CREATE TABLE logs (id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, ts_nanos INTEGER NOT NULL, level TEXT NOT NULL, target TEXT NOT NULL, message TEXT);
                INSERT INTO logs VALUES (1, 100, 0, 'WARN', 'legacy-target', 'legacy body');
                """);

            var legacy = await new CodexNativeSourcesService(legacyDirectory.FullName).ReadLogsAsync(new CodexLogsQuery
            {
                IncludeMessages = true,
                ThreadId = "thread-that-cannot-exist"
            });
            Assert.Equal(CodexNativeSourceAvailability.Available, legacy.Source.Availability);
            Assert.Equal("message", legacy.Capabilities.MessageColumn);
            Assert.False(legacy.Capabilities.HasThreadId);
            Assert.Empty(legacy.Entries);
            Assert.Contains(legacy.Warnings, warning =>
                warning.Contains("thread_id filter is unavailable", StringComparison.OrdinalIgnoreCase));

            var legacyWithoutThreadFilter = await new CodexNativeSourcesService(legacyDirectory.FullName).ReadLogsAsync(new CodexLogsQuery
            {
                IncludeMessages = true
            });
            Assert.Equal("legacy body", Assert.Single(legacyWithoutThreadFilter.Entries).Message);
        }
        finally
        {
            legacyDirectory.Delete(recursive: true);
        }

        var unsupportedDirectory = Directory.CreateTempSubdirectory("tajstokens-native-logs-unsupported-");
        try
        {
            await CreateDatabaseAsync(Path.Combine(unsupportedDirectory.FullName, "logs_2.sqlite"), "CREATE TABLE logs (id INTEGER PRIMARY KEY, message TEXT);");

            var unsupported = await new CodexNativeSourcesService(unsupportedDirectory.FullName).ReadLogsAsync();
            Assert.Equal(CodexNativeSourceAvailability.Unsupported, unsupported.Source.Availability);
            Assert.Contains("required columns", unsupported.Warnings.Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            unsupportedDirectory.Delete(recursive: true);
        }

        var unavailableDirectory = Directory.CreateTempSubdirectory("tajstokens-native-logs-unavailable-");
        try
        {
            var unavailable = await new CodexNativeSourcesService(unavailableDirectory.FullName).ReadLogsAsync();
            Assert.Equal(CodexNativeSourceAvailability.Unavailable, unavailable.Source.Availability);
            Assert.Equal("8e3b180d49", unavailable.Source.UpstreamCorroborationCommit);
        }
        finally
        {
            unavailableDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadLogsAsync_ReportsInvalidSourceAsAnError()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-native-logs-error-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "logs_2.sqlite"), "not a sqlite database");

            var result = await new CodexNativeSourcesService(directory.FullName).ReadLogsAsync();

            Assert.Equal(CodexNativeSourceAvailability.Error, result.Source.Availability);
            Assert.Equal(directory.FullName + Path.DirectorySeparatorChar + "logs_2.sqlite", result.Source.DatabasePath);
            Assert.Equal("codex-home", result.Source.DiscoveryKind);
        }
        finally
        {
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
