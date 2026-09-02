using System.Globalization;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Read-only, bounded projections for Codex SQLite sources that are separate from core thread
/// state. The service deliberately keeps provider-native rows and does not write to Codex or the
/// TajsTokens evidence database.
/// </summary>
public sealed class CodexNativeSourcesService
{
    private const int MaxRowsPerTable = 250;
    private const string UpstreamCorroborationCommit = "8e3b180d49";

    private readonly CodexStateDbExplorerService _explorer;

    public CodexNativeSourcesService(
        string? codexHome = null,
        string? snapshotDirectory = null,
        CodexStateDbExplorerService? explorer = null)
    {
        _explorer = explorer ?? new CodexStateDbExplorerService(codexHome, snapshotDirectory);
    }

    public CodexStateDbExplorerService Explorer => _explorer;

    public async Task<CodexNativeSourcesSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var candidates = _explorer.DiscoverCandidates();

        var memoryTask = ReadMemorySafelyAsync(SelectCandidate(candidates, "memories_"), capturedAtUtc, cancellationToken);
        var goalsTask = ReadGoalsSafelyAsync(SelectCandidate(candidates, "goals_"), capturedAtUtc, cancellationToken);
        var queueTask = ReadQueueSafelyAsync(SelectCandidate(candidates, "queue_"), capturedAtUtc, cancellationToken);
        var artifactsTask = ReadArtifactsSafelyAsync(SelectCandidate(candidates, "state_"), capturedAtUtc, cancellationToken);
        var catalogTask = ReadDesktopCatalogSafelyAsync(SelectCandidate(candidates, "codex-dev"), capturedAtUtc, cancellationToken);
        var summariesTask = ReadThreadSummariesSafelyAsync(SelectCandidate(candidates, "codex-thread-summaries"), capturedAtUtc, cancellationToken);

        await Task.WhenAll(memoryTask, goalsTask, queueTask, artifactsTask, catalogTask, summariesTask);
        return new CodexNativeSourcesSnapshot(
            capturedAtUtc,
            await memoryTask,
            await goalsTask,
            await queueTask,
            await artifactsTask,
            await catalogTask,
            await summariesTask);
    }

    public Task<CodexNativeSourcesSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(cancellationToken);

    /// <summary>Reads only the memory source while retaining the same availability contract.</summary>
    public Task<CodexMemorySource> ReadMemoryAsync(CancellationToken cancellationToken = default) =>
        ReadMemorySafelyAsync(
            SelectCandidate(_explorer.DiscoverCandidates(), "memories_"),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>Reads only the goals source while retaining native status and revision fields.</summary>
    public Task<CodexGoalsSource> ReadGoalsAsync(CancellationToken cancellationToken = default) =>
        ReadGoalsSafelyAsync(
            SelectCandidate(_explorer.DiscoverCandidates(), "goals_"),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>Reads only the queue source; no queue mutation or polling is performed.</summary>
    public Task<CodexQueueSource> ReadQueueAsync(CancellationToken cancellationToken = default) =>
        ReadQueueSafelyAsync(
            SelectCandidate(_explorer.DiscoverCandidates(), "queue_"),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>Reads only core-state thread artifacts, including an explicit empty state.</summary>
    public Task<CodexArtifactsSource> ReadArtifactsAsync(CancellationToken cancellationToken = default) =>
        ReadArtifactsSafelyAsync(
            SelectCandidate(_explorer.DiscoverCandidates(), "state_"),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>Reads the Desktop catalog without reconciling it with core thread state.</summary>
    public Task<CodexDesktopCatalogSource> ReadDesktopCatalogAsync(CancellationToken cancellationToken = default) =>
        ReadDesktopCatalogSafelyAsync(
            SelectCandidate(_explorer.DiscoverCandidates(), "codex-dev"),
            DateTimeOffset.UtcNow,
            cancellationToken);

    /// <summary>Reads the Desktop thread-summary source as observations only.</summary>
    public Task<CodexThreadSummariesSource> ReadThreadSummariesAsync(CancellationToken cancellationToken = default) =>
        ReadThreadSummariesSafelyAsync(
            SelectCandidate(_explorer.DiscoverCandidates(), "codex-thread-summaries"),
            DateTimeOffset.UtcNow,
            cancellationToken);

    private async Task<CodexMemorySource> ReadMemorySafelyAsync(CodexStateDatabaseCandidate? candidate, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try { return await ReadMemoryAsync(candidate, capturedAtUtc, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception)) { return new CodexMemorySource(ErrorInfo(CodexNativeSourceKind.Memory, "Codex memory", candidate, capturedAtUtc, exception), [], []); }
    }

    private async Task<CodexGoalsSource> ReadGoalsSafelyAsync(CodexStateDatabaseCandidate? candidate, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try { return await ReadGoalsAsync(candidate, capturedAtUtc, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception)) { return new CodexGoalsSource(ErrorInfo(CodexNativeSourceKind.Goals, "Codex goals", candidate, capturedAtUtc, exception), [], []); }
    }

    private async Task<CodexQueueSource> ReadQueueSafelyAsync(CodexStateDatabaseCandidate? candidate, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try { return await ReadQueueAsync(candidate, capturedAtUtc, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception)) { return new CodexQueueSource(ErrorInfo(CodexNativeSourceKind.Queue, "Codex queue", candidate, capturedAtUtc, exception), [], []); }
    }

    private async Task<CodexArtifactsSource> ReadArtifactsSafelyAsync(CodexStateDatabaseCandidate? candidate, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try { return await ReadArtifactsAsync(candidate, capturedAtUtc, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception)) { return new CodexArtifactsSource(ErrorInfo(CodexNativeSourceKind.Artifacts, "Codex thread artifacts", candidate, capturedAtUtc, exception), []); }
    }

    private async Task<CodexDesktopCatalogSource> ReadDesktopCatalogSafelyAsync(CodexStateDatabaseCandidate? candidate, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try { return await ReadDesktopCatalogAsync(candidate, capturedAtUtc, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception)) { return new CodexDesktopCatalogSource(ErrorInfo(CodexNativeSourceKind.DesktopCatalog, "Codex Desktop thread catalog", candidate, capturedAtUtc, exception), []); }
    }

    private async Task<CodexThreadSummariesSource> ReadThreadSummariesSafelyAsync(CodexStateDatabaseCandidate? candidate, DateTimeOffset capturedAtUtc, CancellationToken cancellationToken)
    {
        try { return await ReadThreadSummariesAsync(candidate, capturedAtUtc, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsSourceFailure(exception)) { return new CodexThreadSummariesSource(ErrorInfo(CodexNativeSourceKind.ThreadSummaries, "Codex Desktop thread summaries", candidate, capturedAtUtc, exception), []); }
    }

    private static bool IsSourceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException or FormatException or OverflowException or ArgumentException or NotSupportedException;

    private static CodexNativeSourceInfo ErrorInfo(
        CodexNativeSourceKind kind,
        string displayName,
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        Exception exception) => new(
            kind,
            CodexNativeSourceAvailability.Error,
            displayName,
            candidate?.Path,
            capturedAtUtc,
            null,
            [],
            exception.Message)
    {
        UpstreamCorroborationCommit = UpstreamCorroborationCommit
    };

    private async Task<CodexMemorySource> ReadMemoryAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        var context = await PrepareAsync(
            CodexNativeSourceKind.Memory,
            "Codex memory",
            candidate,
            ["jobs", "stage1_outputs"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexMemorySource(context.Info, [], []);
        }

        var jobs = context.Tables.Contains("jobs", StringComparer.OrdinalIgnoreCase)
            ? await ReadRowsAsync(candidate!.Path, "jobs", cancellationToken)
            : [];
        var outputs = context.Tables.Contains("stage1_outputs", StringComparer.OrdinalIgnoreCase)
            ? await ReadRowsAsync(candidate!.Path, "stage1_outputs", cancellationToken)
            : [];

        return new CodexMemorySource(
            context.Info,
            jobs.Select(MapMemoryJob).Where(job => job is not null).Select(job => job!)
                .OrderByDescending(job => job.StartedAt ?? long.MinValue).ThenBy(job => job.Kind, StringComparer.Ordinal)
                .ThenBy(job => job.JobKey, StringComparer.Ordinal).ToArray(),
            outputs.Select(MapMemoryOutput).Where(output => output is not null).Select(output => output!)
                .OrderByDescending(output => output.SourceUpdatedAt).ThenBy(output => output.ThreadId, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task<CodexGoalsSource> ReadGoalsAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        var context = await PrepareAsync(
            CodexNativeSourceKind.Goals,
            "Codex goals",
            candidate,
            ["thread_goals", "thread_goal_continuation_deferrals"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexGoalsSource(context.Info, [], []);
        }

        var goals = context.Tables.Contains("thread_goals", StringComparer.OrdinalIgnoreCase)
            ? await ReadRowsAsync(candidate!.Path, "thread_goals", cancellationToken)
            : [];
        var deferrals = context.Tables.Contains("thread_goal_continuation_deferrals", StringComparer.OrdinalIgnoreCase)
            ? await ReadRowsAsync(candidate!.Path, "thread_goal_continuation_deferrals", cancellationToken)
            : [];
        var deferralIds = deferrals
            .Select(row => Text(row.Page, row.Row, "thread_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deferralSet = new HashSet<string>(deferralIds, StringComparer.OrdinalIgnoreCase);
        return new CodexGoalsSource(
            context.Info,
            goals.Select(row => MapGoal(row, deferralSet)).Where(goal => goal is not null).Select(goal => goal!)
                .OrderByDescending(goal => goal.UpdatedAtMs).ThenBy(goal => goal.ThreadId, StringComparer.Ordinal).ToArray(),
            deferralIds);
    }

    private async Task<CodexQueueSource> ReadQueueAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        var context = await PrepareAsync(
            CodexNativeSourceKind.Queue,
            "Codex queue",
            candidate,
            ["queued_items", "queued_thread_revisions"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexQueueSource(context.Info, [], []);
        }

        var revisions = context.Tables.Contains("queued_thread_revisions", StringComparer.OrdinalIgnoreCase)
            ? await ReadRowsAsync(candidate!.Path, "queued_thread_revisions", cancellationToken)
            : [];
        var revisionByThread = revisions
            .Select(row => (ThreadId: Text(row.Page, row.Row, "thread_id"), Revision: Int64(row.Page, row.Row, "revision")))
            .Where(value => !string.IsNullOrWhiteSpace(value.ThreadId) && value.Revision is not null)
            .GroupBy(value => value.ThreadId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(value => value.Revision!.Value), StringComparer.OrdinalIgnoreCase);

        var items = context.Tables.Contains("queued_items", StringComparer.OrdinalIgnoreCase)
            ? await ReadRowsAsync(candidate!.Path, "queued_items", cancellationToken)
            : [];
        return new CodexQueueSource(
            context.Info,
            items.Select(row => MapQueueItem(row, revisionByThread)).Where(item => item is not null).Select(item => item!)
                .OrderBy(item => item.ThreadId, StringComparer.Ordinal).ThenBy(item => item.QueueOrder).ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray(),
            revisionByThread.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new CodexQueueRevision(pair.Key, pair.Value)).ToArray());
    }

    private async Task<CodexArtifactsSource> ReadArtifactsAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        var context = await PrepareAsync(
            CodexNativeSourceKind.Artifacts,
            "Codex thread artifacts",
            candidate,
            ["thread_artifacts"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexArtifactsSource(context.Info, []);
        }

        var rows = await ReadRowsAsync(candidate!.Path, "thread_artifacts", cancellationToken);
        return new CodexArtifactsSource(
            context.Info,
            rows.Select(MapArtifact).Where(artifact => artifact is not null).Select(artifact => artifact!)
                .OrderBy(artifact => artifact.ThreadId, StringComparer.Ordinal).ThenBy(artifact => artifact.CreatedAt)
                .ThenBy(artifact => artifact.Id, StringComparer.Ordinal).ToArray());
    }

    private async Task<CodexDesktopCatalogSource> ReadDesktopCatalogAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        var context = await PrepareAsync(
            CodexNativeSourceKind.DesktopCatalog,
            "Codex Desktop thread catalog",
            candidate,
            ["local_thread_catalog"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexDesktopCatalogSource(context.Info, []);
        }

        var rows = await ReadRowsAsync(candidate!.Path, "local_thread_catalog", cancellationToken);
        return new CodexDesktopCatalogSource(
            context.Info,
            rows.Select(MapCatalogEntry).Where(entry => entry is not null).Select(entry => entry!)
                .OrderByDescending(entry => entry.SourceRecencyAt).ThenByDescending(entry => entry.SourceCreatedAt)
                .ThenBy(entry => entry.HostId, StringComparer.Ordinal).ThenBy(entry => entry.ThreadId, StringComparer.Ordinal).ToArray());
    }

    private async Task<CodexThreadSummariesSource> ReadThreadSummariesAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        var context = await PrepareAsync(
            CodexNativeSourceKind.ThreadSummaries,
            "Codex Desktop thread summaries",
            candidate,
            ["thread_turn_summaries"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexThreadSummariesSource(context.Info, []);
        }

        var rows = await ReadRowsAsync(candidate!.Path, "thread_turn_summaries", cancellationToken);
        return new CodexThreadSummariesSource(
            context.Info,
            rows.Select(MapSummary).Where(summary => summary is not null).Select(summary => summary!)
                .OrderByDescending(summary => summary.UpdatedAt).ThenBy(summary => summary.ThreadId, StringComparer.Ordinal).ToArray());
    }

    private async Task<SourceContext> PrepareAsync(
        CodexNativeSourceKind kind,
        string displayName,
        CodexStateDatabaseCandidate? candidate,
        IReadOnlyList<string> expectedTables,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
        {
            return new SourceContext(
                new CodexNativeSourceInfo(kind, CodexNativeSourceAvailability.Unavailable, displayName, null, capturedAtUtc, null, [], null),
                [], null);
        }

        try
        {
            var inspection = await _explorer.InspectAsync(candidate.Path, includeRowFingerprints: false, cancellationToken);
            var tables = inspection.Tables
                .Where(table => expectedTables.Contains(table.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var supportedNames = tables.Select(table => table.Name).ToArray();
            var availability = supportedNames.Length == 0
                ? CodexNativeSourceAvailability.Unsupported
                : tables.Any(table => table.RowCount != 0)
                    ? CodexNativeSourceAvailability.Available
                    : CodexNativeSourceAvailability.Empty;
            var info = new CodexNativeSourceInfo(
                kind,
                availability,
                displayName,
                candidate.Path,
                capturedAtUtc,
                inspection.SchemaFingerprint,
                supportedNames,
                null)
            {
                UpstreamCorroborationCommit = UpstreamCorroborationCommit,
                SourceVersion = await ReadSourceVersionAsync(candidate.Path, inspection.Tables, cancellationToken),
                DiscoveryKind = candidate.DiscoveryKind
            };
            return new SourceContext(info, supportedNames, inspection);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        {
            var info = new CodexNativeSourceInfo(
                kind,
                CodexNativeSourceAvailability.Error,
                displayName,
                candidate.Path,
                capturedAtUtc,
                null,
                [],
                exception.Message)
            {
            UpstreamCorroborationCommit = UpstreamCorroborationCommit,
            DiscoveryKind = candidate.DiscoveryKind
        };
            return new SourceContext(info, [], null);
        }
    }

    private async Task<IReadOnlyList<RawRow>> ReadRowsAsync(
        string databasePath,
        string tableName,
        CancellationToken cancellationToken)
    {
        var page = await _explorer.ReadPageAsync(databasePath, tableName, 0, MaxRowsPerTable, cancellationToken);
        return page.Rows.Select(row => new RawRow(page, row)).ToArray();
    }

    private async Task<string?> ReadSourceVersionAsync(
        string databasePath,
        IReadOnlyList<CodexStateTableInfo> tables,
        CancellationToken cancellationToken)
    {
        if (!tables.Any(table => string.Equals(table.Name, "_sqlx_migrations", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var rows = await ReadRowsAsync(databasePath, "_sqlx_migrations", cancellationToken);
        var versions = rows
            .Select(row => Int64(row.Page, row.Row, "version"))
            .Where(version => version is not null)
            .Select(version => version!.Value)
            .ToArray();
        return versions.Length == 0 ? null : versions.Max().ToString(CultureInfo.InvariantCulture);
    }

    private static CodexStateDatabaseCandidate? SelectCandidate(
        IReadOnlyList<CodexStateDatabaseCandidate> candidates,
        string prefix) => candidates
        .Where(candidate => candidate.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .OrderBy(candidate => DiscoveryPriority(candidate.DiscoveryKind))
        .ThenByDescending(candidate => candidate.Generation ?? -1)
        .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
        .FirstOrDefault();

    private static int DiscoveryPriority(string discoveryKind) => discoveryKind switch
    {
        "codex-home" => 0,
        "codex-sqlite-folder" => 1,
        "snapshot-folder" => 2,
        _ => 3
    };

    private static CodexMemoryJob? MapMemoryJob(RawRow row) =>
        RequiredText(row, "kind") is { } kind &&
        RequiredText(row, "job_key") is { } key &&
        RequiredText(row, "status") is { } status
            ? new CodexMemoryJob(
                kind,
                key,
                status,
                Text(row.Page, row.Row, "worker_id"),
                Int64(row.Page, row.Row, "started_at"),
                Int64(row.Page, row.Row, "finished_at"),
                Int64(row.Page, row.Row, "lease_until"),
                Int64(row.Page, row.Row, "retry_at"),
                (int)(Int64(row.Page, row.Row, "retry_remaining") ?? 0),
                Text(row.Page, row.Row, "last_error"),
                Int64(row.Page, row.Row, "input_watermark"),
                Int64(row.Page, row.Row, "last_success_watermark"))
            : null;

    private static CodexMemoryStage1Output? MapMemoryOutput(RawRow row) =>
        RequiredText(row, "thread_id") is { } threadId
            ? new CodexMemoryStage1Output(
                threadId,
                Int64(row.Page, row.Row, "source_updated_at") ?? 0,
                Text(row.Page, row.Row, "rollout_slug"),
                Int64(row.Page, row.Row, "generated_at") ?? 0,
                NullableInt(row.Page, row.Row, "usage_count"),
                NullableInt(row.Page, row.Row, "last_usage"),
                Bool(row.Page, row.Row, "selected_for_phase2"),
                Int64(row.Page, row.Row, "selected_for_phase2_source_updated_at"),
                HasValue(row.Page, row.Row, "raw_memory"),
                HasValue(row.Page, row.Row, "rollout_summary"))
            : null;

    private static CodexGoal? MapGoal(RawRow row, ISet<string> deferrals) =>
        RequiredText(row, "thread_id") is { } threadId &&
        RequiredText(row, "goal_id") is { } goalId &&
        RequiredText(row, "status") is { } status
            ? new CodexGoal(
                threadId,
                goalId,
                Text(row.Page, row.Row, "objective"),
                status,
                Int64(row.Page, row.Row, "token_budget"),
                Int64(row.Page, row.Row, "tokens_used") ?? 0,
                Int64(row.Page, row.Row, "time_used_seconds") ?? 0,
                Int64(row.Page, row.Row, "created_at_ms") ?? 0,
                Int64(row.Page, row.Row, "updated_at_ms") ?? 0,
                deferrals.Contains(threadId))
            : null;

    private static CodexQueueItem? MapQueueItem(RawRow row, IReadOnlyDictionary<string, long> revisions) =>
        RequiredText(row, "id") is { } id &&
        RequiredText(row, "thread_id") is { } threadId
            ? new CodexQueueItem(
                id,
                threadId,
                Int64(row.Page, row.Row, "queue_order") ?? 0,
                Int64(row.Page, row.Row, "created_at_ms") ?? 0,
                Int64(row.Page, row.Row, "updated_at_ms") ?? 0,
                revisions.TryGetValue(threadId, out var revision) ? revision : null,
                HasValue(row.Page, row.Row, "payload_json"))
            : null;

    private static CodexThreadArtifact? MapArtifact(RawRow row) =>
        RequiredText(row, "id") is { } id &&
        RequiredText(row, "thread_id") is { } threadId &&
        RequiredText(row, "artifact_type") is { } artifactType &&
        RequiredText(row, "identity_key") is { } identityKey
            ? new CodexThreadArtifact(
                id,
                threadId,
                artifactType,
                identityKey,
                Int64(row.Page, row.Row, "created_at") ?? 0,
                HasValue(row.Page, row.Row, "payload"))
            : null;

    private static CodexDesktopCatalogEntry? MapCatalogEntry(RawRow row) =>
        RequiredText(row, "host_id") is { } hostId &&
        RequiredText(row, "thread_id") is { } threadId &&
        RequiredText(row, "display_title") is { } title &&
        RequiredText(row, "source_kind") is { } sourceKind
            ? new CodexDesktopCatalogEntry(
                hostId,
                threadId,
                title,
                Double(row.Page, row.Row, "source_created_at"),
                Double(row.Page, row.Row, "source_updated_at"),
                Text(row.Page, row.Row, "cwd"),
                sourceKind,
                Text(row.Page, row.Row, "source_detail"),
                Text(row.Page, row.Row, "model_provider"),
                Text(row.Page, row.Row, "git_branch"),
                Int64(row.Page, row.Row, "observation_sequence") ?? 0,
                Bool(row.Page, row.Row, "missing_candidate"),
                Text(row.Page, row.Row, "thread_source"),
                Double(row.Page, row.Row, "source_recency_at"),
                Bool(row.Page, row.Row, "pending_observed_title"),
                Text(row.Page, row.Row, "project_id"),
                Text(row.Page, row.Row, "conversation_origin"))
            : null;

    private static CodexThreadSummary? MapSummary(RawRow row) =>
        RequiredText(row, "principal_key") is { } principal &&
        RequiredText(row, "host_key") is { } host &&
        RequiredText(row, "thread_id") is { } threadId &&
        RequiredText(row, "summary") is { } summary
            ? new CodexThreadSummary(
                principal,
                host,
                threadId,
                summary,
                Text(row.Page, row.Row, "compact_summary"),
                Text(row.Page, row.Row, "compact_summary_turn_key"),
                Int64(row.Page, row.Row, "revision") ?? 0,
                Int64(row.Page, row.Row, "updated_at") ?? 0)
            : null;

    private static string? RequiredText(RawRow row, string column) =>
        Text(row.Page, row.Row, column) is { Length: > 0 } value ? value : null;

    private static string? Text(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        var index = -1;
        for (var i = 0; i < page.Columns.Count; i++)
        {
            if (string.Equals(page.Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        return index >= 0 && index < row.Values.Count && row.Values[index] is not null
            ? Convert.ToString(row.Values[index], CultureInfo.InvariantCulture)
            : null;
    }

    private static long? Int64(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        var text = Text(page, row, column);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static int? NullableInt(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        var value = Int64(page, row, column);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    private static bool Bool(CodexStateRawPage page, CodexStateRawRow row, string column) =>
        Int64(page, row, column) is not 0;

    private static double Double(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        var text = Text(page, row, column);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static bool HasValue(CodexStateRawPage page, CodexStateRawRow row, string column) =>
        Text(page, row, column) is not null;

    private sealed record RawRow(CodexStateRawPage Page, CodexStateRawRow Row);

    private sealed record SourceContext(
        CodexNativeSourceInfo Info,
        IReadOnlyList<string> Tables,
        CodexStateInspectionResult? Inspection);
}
