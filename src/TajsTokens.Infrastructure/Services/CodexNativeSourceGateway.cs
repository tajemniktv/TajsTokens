// Taj's Tokens | CodexNativeSourceGateway.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Services;

/// <summary>
///     Source-specific gateway for Codex SQLite sources that are separate from core thread state.
///     This boundary owns source acquisition and parsing; the public read-model service delegates to
///     it without interpreting generic explorer rows.
/// </summary>
public sealed class CodexNativeSourceGateway
{
    private const int MaxRowsPerTable = 250;
    private const int MaxLogPageSize = 250;
    private const string UpstreamCorroborationCommit = "8e3b180d49";

    private readonly CodexLogReadSnapshots _logSnapshots = new();

    public CodexNativeSourceGateway(
        string? codexHome = null,
        string? snapshotDirectory = null,
        CodexStateDbExplorerService? explorer = null)
    {
        Explorer = explorer ?? new CodexStateDbExplorerService(codexHome, snapshotDirectory);
    }

    public CodexStateDbExplorerService Explorer { get; }

    public Task ReleaseLogsSnapshotAsync(string id)
    {
        return _logSnapshots.ReleaseAsync(id);
    }

    public Task<CodexNativeSourcesSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync(new CodexNativeSourcesQuery(), cancellationToken);
    }

    public async Task<CodexNativeSourcesSnapshot> ReadAsync(
        CodexNativeSourcesQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        query = query with
        {
            Auxiliary = new CodexAuxiliaryQuery(Math.Max(0, query.Auxiliary.PageIndex), TrimToNull(query.Auxiliary.ThreadId)),
        };
        DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<CodexStateDatabaseCandidate> candidates = Explorer.DiscoverCandidates();
        CodexNativeSourceSelection[] selections = Enum.GetValues<CodexNativeSourceKind>()
            .Select(kind => SelectSource(
                candidates,
                kind,
                query.SelectedPaths.GetValueOrDefault(kind) ??
                (kind == CodexNativeSourceKind.Logs ? query.Logs.DatabasePath : null)))
            .ToArray();

        CodexStateDatabaseCandidate? Selected(CodexNativeSourceKind kind)
        {
            return ResolveCandidate(candidates, kind, selections.Single(selection => selection.Kind == kind).SelectedPath);
        }

        Task<CodexLogsSource> logsTask = ReadLogsSafelyAsync(
            Selected(CodexNativeSourceKind.Logs),
            query.Logs with { DatabasePath = selections.Single(selection => selection.Kind == CodexNativeSourceKind.Logs).SelectedPath },
            capturedAtUtc,
            cancellationToken);
        Task<CodexMemorySource> memoryTask = ReadMemorySafelyAsync(
            Selected(CodexNativeSourceKind.Memory),
            capturedAtUtc,
            cancellationToken,
            query.Auxiliary);
        Task<CodexGoalsSource> goalsTask = ReadGoalsSafelyAsync(
            Selected(CodexNativeSourceKind.Goals),
            capturedAtUtc,
            cancellationToken,
            query.Auxiliary);
        Task<CodexQueueSource> queueTask = ReadQueueSafelyAsync(
            Selected(CodexNativeSourceKind.Queue),
            capturedAtUtc,
            cancellationToken,
            query.Auxiliary);
        Task<CodexArtifactsSource> artifactsTask = ReadArtifactsSafelyAsync(
            Selected(CodexNativeSourceKind.Artifacts),
            capturedAtUtc,
            cancellationToken,
            query.Auxiliary);
        Task<CodexDesktopCatalogSource> catalogTask = ReadDesktopCatalogSafelyAsync(
            Selected(CodexNativeSourceKind.DesktopCatalog),
            capturedAtUtc,
            cancellationToken,
            query.Auxiliary);
        Task<CodexThreadSummariesSource> summariesTask = ReadThreadSummariesSafelyAsync(
            Selected(CodexNativeSourceKind.ThreadSummaries),
            capturedAtUtc,
            cancellationToken,
            query.Auxiliary);

        await Task.WhenAll(logsTask, memoryTask, goalsTask, queueTask, artifactsTask, catalogTask, summariesTask);
        return new CodexNativeSourcesSnapshot(
            capturedAtUtc,
            await logsTask,
            await memoryTask,
            await goalsTask,
            await queueTask,
            await artifactsTask,
            await catalogTask,
            await summariesTask) { Selections = selections, Auxiliary = query.Auxiliary };
    }

    public Task<CodexNativeSourcesSnapshot> InspectAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync(cancellationToken);
    }

    /// <summary>Reads only the memory source while retaining the same availability contract.</summary>
    public Task<CodexMemorySource> ReadMemoryAsync(CancellationToken cancellationToken = default)
    {
        return ReadMemorySafelyAsync(
            SelectCandidate(Explorer.DiscoverCandidates(), "memories_"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Reads a bounded page of source-native Codex logs without writing to either database.</summary>
    public Task<CodexLogsSource> ReadLogsAsync(
        CodexLogsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        return ReadLogsSafelyAsync(
            ResolveCandidate(Explorer.DiscoverCandidates(), CodexNativeSourceKind.Logs, query?.DatabasePath),
            query ?? new CodexLogsQuery(),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Reads only the goals source while retaining native status and revision fields.</summary>
    public Task<CodexGoalsSource> ReadGoalsAsync(CancellationToken cancellationToken = default)
    {
        return ReadGoalsSafelyAsync(
            SelectCandidate(Explorer.DiscoverCandidates(), "goals_"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Reads only the queue source; no queue mutation or polling is performed.</summary>
    public Task<CodexQueueSource> ReadQueueAsync(CancellationToken cancellationToken = default)
    {
        return ReadQueueSafelyAsync(
            SelectCandidate(Explorer.DiscoverCandidates(), "queue_"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Reads only core-state thread artifacts, including an explicit empty state.</summary>
    public Task<CodexArtifactsSource> ReadArtifactsAsync(CancellationToken cancellationToken = default)
    {
        return ReadArtifactsSafelyAsync(
            SelectCandidate(Explorer.DiscoverCandidates(), "state_"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Reads the Desktop catalog without reconciling it with core thread state.</summary>
    public Task<CodexDesktopCatalogSource> ReadDesktopCatalogAsync(CancellationToken cancellationToken = default)
    {
        return ReadDesktopCatalogSafelyAsync(
            SelectCandidate(Explorer.DiscoverCandidates(), "codex-dev"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>Reads the Desktop thread-summary source as observations only.</summary>
    public Task<CodexThreadSummariesSource> ReadThreadSummariesAsync(CancellationToken cancellationToken = default)
    {
        return ReadThreadSummariesSafelyAsync(
            SelectCandidate(Explorer.DiscoverCandidates(), "codex-thread-summaries"),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private async Task<CodexMemorySource> ReadMemorySafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        try
        {
            return await ReadMemoryAsync(candidate, capturedAtUtc, cancellationToken, auxiliary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexMemorySource(
                ErrorInfo(CodexNativeSourceKind.Memory, "Codex memory", candidate, capturedAtUtc, exception),
                [],
                []);
        }
    }

    private async Task<CodexLogsSource> ReadLogsSafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        CodexLogsQuery query,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadLogsAsync(candidate, query, capturedAtUtc, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexLogsSource(
                ErrorInfo(CodexNativeSourceKind.Logs, "Codex logs", candidate, capturedAtUtc, exception),
                [],
                NormalizeLogsQuery(query),
                CodexLogsCapabilities.None,
                null,
                false,
                []);
        }
    }

    private async Task<CodexGoalsSource> ReadGoalsSafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        try
        {
            return await ReadGoalsAsync(candidate, capturedAtUtc, cancellationToken, auxiliary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexGoalsSource(ErrorInfo(CodexNativeSourceKind.Goals, "Codex goals", candidate, capturedAtUtc, exception), [], []);
        }
    }

    private async Task<CodexQueueSource> ReadQueueSafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        try
        {
            return await ReadQueueAsync(candidate, capturedAtUtc, cancellationToken, auxiliary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexQueueSource(ErrorInfo(CodexNativeSourceKind.Queue, "Codex queue", candidate, capturedAtUtc, exception), [], []);
        }
    }

    private async Task<CodexArtifactsSource> ReadArtifactsSafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        try
        {
            return await ReadArtifactsAsync(candidate, capturedAtUtc, cancellationToken, auxiliary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexArtifactsSource(
                ErrorInfo(CodexNativeSourceKind.Artifacts, "Codex thread artifacts", candidate, capturedAtUtc, exception),
                []);
        }
    }

    private async Task<CodexDesktopCatalogSource> ReadDesktopCatalogSafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        try
        {
            return await ReadDesktopCatalogAsync(candidate, capturedAtUtc, cancellationToken, auxiliary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexDesktopCatalogSource(
                ErrorInfo(CodexNativeSourceKind.DesktopCatalog, "Codex Desktop thread catalog", candidate, capturedAtUtc, exception),
                []);
        }
    }

    private async Task<CodexThreadSummariesSource> ReadThreadSummariesSafelyAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        try
        {
            return await ReadThreadSummariesAsync(candidate, capturedAtUtc, cancellationToken, auxiliary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsSourceFailure(exception))
        {
            return new CodexThreadSummariesSource(
                ErrorInfo(CodexNativeSourceKind.ThreadSummaries, "Codex Desktop thread summaries", candidate, capturedAtUtc, exception),
                []);
        }
    }

    private static bool IsSourceFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
            or KeyNotFoundException or SqliteException or FormatException or OverflowException or ArgumentException
            or NotSupportedException;
    }

    private static CodexNativeSourceInfo ErrorInfo(
        CodexNativeSourceKind kind,
        string displayName,
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        Exception exception)
    {
        return new CodexNativeSourceInfo(
            kind,
            CodexNativeSourceAvailability.Error,
            displayName,
            candidate?.Path,
            capturedAtUtc,
            null,
            [],
            exception.Message) { UpstreamCorroborationCommit = UpstreamCorroborationCommit, DiscoveryKind = candidate?.DiscoveryKind };
    }

    private async Task<CodexMemorySource> ReadMemoryAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        SourceContext context = await PrepareAsync(
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

        ReadRowsResult jobs = context.Tables.Contains("jobs", StringComparer.OrdinalIgnoreCase)
            ? await ReadOrderedRowsAsync(candidate!.Path, CodexOrderedSourceTable.Jobs, cancellationToken, auxiliary)
            : ReadRowsResult.Empty;
        ReadRowsResult outputs = context.Tables.Contains("stage1_outputs", StringComparer.OrdinalIgnoreCase)
            ? await ReadOrderedRowsAsync(candidate!.Path, CodexOrderedSourceTable.Stage1Outputs, cancellationToken, auxiliary)
            : ReadRowsResult.Empty;

        CodexMemoryJob[] mappedJobs = jobs.Rows.Select(MapMemoryJob).OfType<CodexMemoryJob>().ToArray();
        CodexMemoryStage1Output[] mappedOutputs = outputs.Rows.Select(MapMemoryOutput).OfType<CodexMemoryStage1Output>().ToArray();
        if (!string.IsNullOrWhiteSpace(auxiliary?.ThreadId))
            context = context with
            {
                Info = context.Info with
                {
                    Warnings =
                    [
                        .. context.Info.Warnings, "jobs: omitted in thread scope; no native thread_id relationship is established.",
                    ],
                },
            };
        return new CodexMemorySource(
            WithCoverage(context.Info, ("jobs", jobs, mappedJobs.Length), ("stage1_outputs", outputs, mappedOutputs.Length)),
            mappedJobs,
            mappedOutputs);
    }

    private async Task<CodexLogsSource> ReadLogsAsync(
        CodexStateDatabaseCandidate? candidate,
        CodexLogsQuery query,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        CodexLogsQuery normalizedQuery = NormalizeLogsQuery(query) with { DatabasePath = candidate?.Path ?? query.DatabasePath };
        SourceContext context = await PrepareAsync(
            CodexNativeSourceKind.Logs,
            "Codex logs",
            candidate,
            ["logs"],
            capturedAtUtc,
            cancellationToken);
        if (!context.Info.IsInspectable)
        {
            return new CodexLogsSource(
                context.Info with { UpstreamCorroborationCommit = UpstreamCorroborationCommit },
                [],
                normalizedQuery,
                CodexLogsCapabilities.None,
                null,
                false,
                []);
        }

        CodexLogsCapabilities capabilities = GetLogsCapabilities(context.Inspection);
        if (!capabilities.HasRequiredSchema)
        {
            CodexNativeSourceInfo unsupportedInfo = context.Info with
            {
                Availability = CodexNativeSourceAvailability.Unsupported, SupportedTables = [],
            };
            return new CodexLogsSource(
                unsupportedInfo,
                [],
                normalizedQuery,
                capabilities,
                null,
                false,
                ["The logs table is present but is missing one or more required columns: id, ts, ts_nanos, level or target."]);
        }

        IReadOnlyList<string> warnings = GetLogQueryWarnings(normalizedQuery, capabilities);
        (CodexStateRawPage Page, string? Id, DateTimeOffset? Expires) snapshot = await _logSnapshots.ReadAsync(
            Explorer,
            candidate!.Path,
            normalizedQuery,
            capabilities,
            cancellationToken);
        CodexStateRawPage page = snapshot.Page;
        normalizedQuery = normalizedQuery with { SnapshotId = snapshot.Id };
        warnings =
        [
            .. warnings, snapshot.Expires is { } expires
                ? $"Read-only log snapshot expires at {expires:O}; Apply refreshes it."
                : "Live page only; navigation can shift as the source changes. WAL snapshot unavailable or not requested.",
        ];
        CodexLogEntry[] entries = page.Rows
            .Take(normalizedQuery.PageSize)
            .Select(row => MapLog(new RawRow(page, row)))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();
        long offset = checked((long)normalizedQuery.PageIndex * normalizedQuery.PageSize);
        bool hasMoreRows = page.TotalRows > offset + normalizedQuery.PageSize;

        int skipped = Math.Min(page.Rows.Count, normalizedQuery.PageSize) - entries.Length;
        return new CodexLogsSource(
            context.Info with
            {
                CapturedAtUtc = snapshot.Expires?.AddMinutes(-2) ?? context.Info.CapturedAtUtc,
                HasMoreRows = hasMoreRows,
                Warnings = skipped == 0
                    ? context.Info.Warnings
                    :
                    [
                        .. context.Info.Warnings,
                        $"logs: {skipped} row(s) omitted because required identity/value fields could not be decoded.",
                    ],
            },
            entries,
            normalizedQuery,
            capabilities,
            page.TotalRows,
            hasMoreRows,
            warnings) { SnapshotExpiresAtUtc = snapshot.Expires };
    }

    private static CodexLogsQuery NormalizeLogsQuery(CodexLogsQuery query)
    {
        return query with
        {
            PageIndex = Math.Max(0, query.PageIndex),
            PageSize = Math.Clamp(query.PageSize, 1, MaxLogPageSize),
            Levels = (query.Levels ?? Array.Empty<string>())
            .Where(level => !string.IsNullOrWhiteSpace(level))
            .Select(level => level.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray(),
            TargetContains = TrimToNull(query.TargetContains),
            ModulePathContains = TrimToNull(query.ModulePathContains),
            FileContains = TrimToNull(query.FileContains),
            ThreadId = TrimToNull(query.ThreadId),
            ProcessUuid = TrimToNull(query.ProcessUuid),
        };
    }

    private static CodexLogsCapabilities GetLogsCapabilities(CodexStateInspectionResult? inspection)
    {
        CodexStateTableInfo? table = inspection?.Tables.FirstOrDefault(table =>
            string.Equals(table.Name, "logs", StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            return CodexLogsCapabilities.None;
        }

        HashSet<string> columns = table.Columns.Select(column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? messageColumn = columns.Contains("feedback_log_body")
            ? "feedback_log_body"
            : columns.Contains("message")
                ? "message"
                : null;
        return new CodexLogsCapabilities(
            messageColumn,
            columns.Contains("module_path"),
            columns.Contains("file"),
            columns.Contains("line"),
            columns.Contains("thread_id"),
            columns.Contains("process_uuid"),
            columns.Contains("estimated_bytes"))
        {
            HasRequiredSchema = new[] { "id", "ts", "ts_nanos", "level", "target" }.All(columns.Contains),
        };
    }

    private static IReadOnlyList<string> GetLogQueryWarnings(
        CodexLogsQuery query,
        CodexLogsCapabilities capabilities)
    {
        var warnings = new List<string>();
        if (query.FromUtc is { } fromUtc && query.ToUtcExclusive is { } toUtc && fromUtc >= toUtc)
        {
            warnings.Add("The time range is empty because the end must be later than the start.");
        }

        AddUnavailableFilterWarning(warnings, query.ModulePathContains, capabilities.HasModulePath, "module_path");
        AddUnavailableFilterWarning(warnings, query.FileContains, capabilities.HasFile, "file");
        AddUnavailableFilterWarning(warnings, query.ThreadId, capabilities.HasThreadId, "thread_id");
        AddUnavailableFilterWarning(warnings, query.ProcessUuid, capabilities.HasProcessUuid, "process_uuid");
        if (!query.IncludeThreadless && !capabilities.HasThreadId)
        {
            warnings.Add("The source has no thread_id column, so thread-associated rows cannot be selected.");
        }

        return warnings;
    }

    private static void AddUnavailableFilterWarning(
        ICollection<string> warnings,
        string? value,
        bool available,
        string columnName)
    {
        if (!string.IsNullOrWhiteSpace(value) && !available)
        {
            warnings.Add($"The {columnName} filter is unavailable in this logs schema.");
        }
    }

    private static CodexLogEntry? MapLog(RawRow row)
    {
        return Int64(row.Page, row.Row, "id") is long id &&
               Int64(row.Page, row.Row, "ts") is long timestamp &&
               Int64(row.Page, row.Row, "ts_nanos") is long timestampNanoseconds &&
               RequiredText(row, "level") is { } level &&
               RequiredText(row, "target") is { } target
            ? new CodexLogEntry(
                id,
                timestamp,
                timestampNanoseconds,
                level,
                target,
                Text(row.Page, row.Row, "module_path"),
                Text(row.Page, row.Row, "file"),
                Int64(row.Page, row.Row, "line"),
                Text(row.Page, row.Row, "thread_id"),
                Text(row.Page, row.Row, "process_uuid"),
                Bool(row.Page, row.Row, "has_message") == true,
                Text(row.Page, row.Row, "message"),
                Int64(row.Page, row.Row, "estimated_bytes"))
            : null;
    }

    private async Task<CodexGoalsSource> ReadGoalsAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        SourceContext context = await PrepareAsync(
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

        ReadRowsResult goals = context.Tables.Contains("thread_goals", StringComparer.OrdinalIgnoreCase)
            ? await ReadOrderedRowsAsync(candidate!.Path, CodexOrderedSourceTable.ThreadGoals, cancellationToken, auxiliary)
            : ReadRowsResult.Empty;
        string[] goalThreadIds = goals.Rows
            .Select(row => Text(row.Page, row.Row, "thread_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        ReadRowsResult deferrals = context.Tables.Contains("thread_goal_continuation_deferrals", StringComparer.OrdinalIgnoreCase) &&
                                   goalThreadIds.Length > 0
            ? await ReadRowsByTextValuesAsync(
                candidate!.Path,
                CodexRelationSourceTable.ThreadGoalContinuationDeferrals,
                goalThreadIds,
                cancellationToken)
            : ReadRowsResult.Empty;
        string[] deferralIds = deferrals.Rows
            .Select(row => Text(row.Page, row.Row, "thread_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var deferralSet = new HashSet<string>(deferralIds, StringComparer.Ordinal);
        bool deferralsComplete = context.Inspection?.Tables.Any(table =>
                                     string.Equals(table.Name, "thread_goal_continuation_deferrals", StringComparison.OrdinalIgnoreCase) &&
                                     table.Columns.Any(column => string.Equals(
                                         column.Name,
                                         "thread_id",
                                         StringComparison.OrdinalIgnoreCase))) == true &&
                                 !deferrals.IsTruncated;
        CodexGoal[] mappedGoals = goals.Rows.Select(row => MapGoal(row, deferralSet, deferralsComplete)).OfType<CodexGoal>().ToArray();
        return new CodexGoalsSource(
            WithCoverage(
                context.Info,
                ("thread_goals", goals, mappedGoals.Length),
                ("thread_goal_continuation_deferrals", deferrals, deferralIds.Length)),
            mappedGoals,
            deferralIds);
    }

    private async Task<CodexQueueSource> ReadQueueAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        SourceContext context = await PrepareAsync(
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

        ReadRowsResult items = context.Tables.Contains("queued_items", StringComparer.OrdinalIgnoreCase)
            ? await ReadOrderedRowsAsync(candidate!.Path, CodexOrderedSourceTable.QueuedItems, cancellationToken, auxiliary)
            : ReadRowsResult.Empty;
        string[] itemThreadIds = items.Rows
            .Select(row => Text(row.Page, row.Row, "thread_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        ReadRowsResult revisions =
            context.Tables.Contains("queued_thread_revisions", StringComparer.OrdinalIgnoreCase) && itemThreadIds.Length > 0
                ? await ReadRowsByTextValuesAsync(
                    candidate!.Path,
                    CodexRelationSourceTable.QueuedThreadRevisions,
                    itemThreadIds,
                    cancellationToken)
                : ReadRowsResult.Empty;
        CodexQueueRevision[] mappedRevisions = revisions.Rows
            .Select(row => (ThreadId: Text(row.Page, row.Row, "thread_id"), Revision: Int64(row.Page, row.Row, "revision")))
            .Where(value => !string.IsNullOrWhiteSpace(value.ThreadId) && value.Revision is not null)
            .Select(value => new CodexQueueRevision(value.ThreadId!, value.Revision!.Value)).ToArray();
        IGrouping<string, CodexQueueRevision>[] groups = mappedRevisions.GroupBy(value => value.ThreadId, StringComparer.Ordinal).ToArray();
        Dictionary<string, long> revisionByThread = groups
            .Where(group => !revisions.IsTruncated && group.Select(value => value.Revision).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Revision, StringComparer.Ordinal);
        CodexNativeSourceInfo info = WithCoverage(context.Info, ("queued_thread_revisions", revisions, mappedRevisions.Length));
        if (groups.Any(group => group.Select(value => value.Revision).Distinct().Count() > 1))
            info = info with
            {
                Warnings =
                [
                    .. info.Warnings,
                    "queued_thread_revisions: conflicting revisions retained; item revision is unknown for ambiguous threads.",
                ],
            };

        CodexQueueItem[] mappedItems = items.Rows.Select(row => MapQueueItem(row, revisionByThread)).OfType<CodexQueueItem>().ToArray();
        return new CodexQueueSource(
            WithCoverage(info, ("queued_items", items, mappedItems.Length)),
            mappedItems,
            mappedRevisions);
    }

    private async Task<CodexArtifactsSource> ReadArtifactsAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        SourceContext context = await PrepareAsync(
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

        ReadRowsResult rows = await ReadOrderedRowsAsync(
            candidate!.Path,
            CodexOrderedSourceTable.ThreadArtifacts,
            cancellationToken,
            auxiliary);
        CodexThreadArtifact[] mapped = rows.Rows.Select(MapArtifact).OfType<CodexThreadArtifact>().ToArray();
        return new CodexArtifactsSource(
            WithCoverage(context.Info, ("thread_artifacts", rows, mapped.Length)),
            mapped);
    }

    private async Task<CodexDesktopCatalogSource> ReadDesktopCatalogAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        SourceContext context = await PrepareAsync(
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

        ReadRowsResult rows = await ReadOrderedRowsAsync(
            candidate!.Path,
            CodexOrderedSourceTable.LocalThreadCatalog,
            cancellationToken,
            auxiliary);
        CodexDesktopCatalogEntry[] mapped = rows.Rows.Select(MapCatalogEntry).OfType<CodexDesktopCatalogEntry>().ToArray();
        return new CodexDesktopCatalogSource(
            WithCoverage(context.Info, ("local_thread_catalog", rows, mapped.Length)),
            mapped);
    }

    private async Task<CodexThreadSummariesSource> ReadThreadSummariesAsync(
        CodexStateDatabaseCandidate? candidate,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        SourceContext context = await PrepareAsync(
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

        ReadRowsResult rows = await ReadOrderedRowsAsync(
            candidate!.Path,
            CodexOrderedSourceTable.ThreadTurnSummaries,
            cancellationToken,
            auxiliary);
        CodexThreadSummary[] mapped = rows.Rows.Select(MapSummary).OfType<CodexThreadSummary>().ToArray();
        return new CodexThreadSummariesSource(
            WithCoverage(context.Info, ("thread_turn_summaries", rows, mapped.Length)),
            mapped);
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
                new CodexNativeSourceInfo(
                    kind,
                    CodexNativeSourceAvailability.Unavailable,
                    displayName,
                    null,
                    capturedAtUtc,
                    null,
                    [],
                    null),
                [],
                null);
        }

        try
        {
            CodexStateInspectionResult inspection = await Explorer.InspectAsync(candidate.Path, false, cancellationToken);
            CodexStateTableInfo[] tables = inspection.Tables
                .Where(table => expectedTables.Contains(table.Name, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            string[] supportedNames = tables.Select(table => table.Name).ToArray();
            CodexNativeSourceAvailability availability = supportedNames.Length == 0
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
                DiscoveryKind = candidate.DiscoveryKind,
                Warnings = expectedTables.Except(supportedNames, StringComparer.OrdinalIgnoreCase)
                    .Select(table => $"{table}: capability unavailable (table not observed).")
                    .Concat(
                        tables.SelectMany(table => ExpectedColumns(table.Name)
                            .Except(table.Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase)
                            .Select(column => $"{table.Name}.{column}: unavailable (column not observed)."))).ToArray(),
            };
            return new SourceContext(info, supportedNames, inspection);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or InvalidDataException or KeyNotFoundException or SqliteException)
        {
            var info = new CodexNativeSourceInfo(
                kind,
                CodexNativeSourceAvailability.Error,
                displayName,
                candidate.Path,
                capturedAtUtc,
                null,
                [],
                exception.Message) { UpstreamCorroborationCommit = UpstreamCorroborationCommit, DiscoveryKind = candidate.DiscoveryKind };
            return new SourceContext(info, [], null);
        }
    }

    private async Task<ReadRowsResult> ReadOrderedRowsAsync(
        string databasePath,
        CodexOrderedSourceTable sourceTable,
        CancellationToken cancellationToken,
        CodexAuxiliaryQuery? auxiliary = null)
    {
        auxiliary ??= new CodexAuxiliaryQuery();
        // Jobs have no native thread_id; do not infer thread ownership from job keys.
        if (sourceTable == CodexOrderedSourceTable.Jobs && !string.IsNullOrWhiteSpace(auxiliary.ThreadId)) return ReadRowsResult.Empty;
        int pageIndex = Math.Max(0, auxiliary.PageIndex);
        CodexStateRawPage page = await Explorer.ReadOrderedPageAsync(
            databasePath,
            sourceTable,
            pageIndex,
            MaxRowsPerTable,
            cancellationToken,
            string.IsNullOrWhiteSpace(auxiliary.ThreadId) ? null : auxiliary.ThreadId.Trim());
        return new ReadRowsResult(
            page.Rows.Take(MaxRowsPerTable).Select(row => new RawRow(page, row)).ToArray(),
            page.TotalRows > ((long)pageIndex + 1) * MaxRowsPerTable);
    }

    private async Task<ReadRowsResult> ReadRowsByTextValuesAsync(
        string databasePath,
        CodexRelationSourceTable sourceTable,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        CodexStateRawPage page = await Explorer.ReadRowsByTextValuesAsync(
            databasePath,
            sourceTable,
            values,
            cancellationToken);
        return new ReadRowsResult(
            page.Rows.Select(row => new RawRow(page, row)).ToArray(),
            page.TotalRows > page.PageSize || page.Rows.Count > page.PageSize);
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

        long? version = await Explorer.ReadMaxInt64Async(databasePath, "_sqlx_migrations", "version", cancellationToken);
        return version?.ToString(CultureInfo.InvariantCulture);
    }

    private static CodexStateDatabaseCandidate? SelectCandidate(
        IReadOnlyList<CodexStateDatabaseCandidate> candidates,
        string prefix)
    {
        return candidates
            .Where(candidate => candidate.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => DiscoveryPriority(candidate.DiscoveryKind))
            .ThenByDescending(candidate => candidate.Generation ?? -1)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string Prefix(CodexNativeSourceKind kind)
    {
        return kind switch
        {
            CodexNativeSourceKind.Logs => "logs_",
            CodexNativeSourceKind.Memory => "memories_",
            CodexNativeSourceKind.Goals => "goals_",
            CodexNativeSourceKind.Queue => "queue_",
            CodexNativeSourceKind.Artifacts => "state_",
            CodexNativeSourceKind.DesktopCatalog => "codex-dev",
            CodexNativeSourceKind.ThreadSummaries => "codex-thread-summaries",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static CodexNativeSourceSelection SelectSource(
        IReadOnlyList<CodexStateDatabaseCandidate> candidates,
        CodexNativeSourceKind kind,
        string? requestedPath)
    {
        CodexStateDatabaseCandidate[] matching = candidates
            .Where(candidate => candidate.FileName.StartsWith(Prefix(kind), StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => DiscoveryPriority(candidate.DiscoveryKind))
            .ThenByDescending(candidate => candidate.Generation ?? -1)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ToArray();
        string? selected = requestedPath ?? matching.FirstOrDefault()?.Path;
        return new CodexNativeSourceSelection(
            kind,
            selected,
            matching.Select(candidate => new CodexNativeSourceInstance(
                kind,
                candidate.Path,
                candidate.DiscoveryKind,
                candidate.Generation,
                candidate.LastWriteTimeUtc)).ToArray(),
            "codex-native-source-selection/v1",
            requestedPath is not null
                ? "Explicit source choice; never falls back if the chosen instance disappears or fails."
                : "Discovery preference: Codex home, sqlite folder, then snapshots; generation descending, write time descending, path ordinal. This is not semantic authority. Other instances are not merged.");
    }

    private static CodexNativeSourceInfo WithCoverage(
        CodexNativeSourceInfo info,
        params (string Table, ReadRowsResult Read, int MappedCount)[] results)
    {
        return info with
        {
            HasMoreRows = info.HasMoreRows || results.Any(result => result.Read.IsTruncated),
            Warnings =
            [
                .. info.Warnings, .. results.SelectMany(result =>
                {
                    var warnings = new List<string>();
                    if (result.Read.IsTruncated) warnings.Add($"{result.Table}: bounded result; more source rows exist.");
                    int skipped = result.Read.Rows.Count - result.MappedCount;
                    if (skipped > 0)
                        warnings.Add(
                            $"{result.Table}: {skipped} row(s) omitted or collapsed because identity/value fields were missing, invalid or duplicated. Inspect the source for details.");
                    return warnings;
                }),
            ],
        };
    }

    private static string[] ExpectedColumns(string table)
    {
        return table.ToLowerInvariant() switch
        {
            "jobs" =>
            [
                "kind", "job_key", "status", "worker_id", "started_at", "finished_at", "lease_until", "retry_at", "retry_remaining",
                "last_error", "input_watermark", "last_success_watermark",
            ],
            "stage1_outputs" =>
            [
                "thread_id", "source_updated_at", "generated_at", "usage_count", "last_usage", "selected_for_phase2",
                "selected_for_phase2_source_updated_at", "raw_memory", "rollout_summary",
            ],
            "thread_goals" =>
            [
                "thread_id", "goal_id", "objective", "status", "token_budget", "tokens_used", "time_used_seconds", "created_at_ms",
                "updated_at_ms",
            ],
            "thread_goal_continuation_deferrals" => ["thread_id"],
            "queued_items" => ["id", "thread_id", "queue_order", "created_at_ms", "updated_at_ms", "payload_json"],
            "queued_thread_revisions" => ["thread_id", "revision"],
            "thread_artifacts" => ["id", "thread_id", "artifact_type", "identity_key", "created_at", "payload"],
            "local_thread_catalog" =>
            [
                "host_id", "thread_id", "display_title", "source_kind", "source_created_at", "source_updated_at", "observation_sequence",
                "missing_candidate", "source_recency_at", "pending_observed_title",
            ],
            "thread_turn_summaries" => ["principal_key", "host_key", "thread_id", "summary", "revision", "updated_at"],
            _ => [],
        };
    }

    private static CodexStateDatabaseCandidate? ResolveCandidate(
        IReadOnlyList<CodexStateDatabaseCandidate> candidates,
        CodexNativeSourceKind kind,
        string? requestedPath)
    {
        if (requestedPath is null) return SelectCandidate(candidates, Prefix(kind));
        // Only discovered files in this family can be selected. A vanished selection is unavailable,
        // never permission to silently read another store or an arbitrary file.
        return candidates.FirstOrDefault(candidate =>
            candidate.FileName.StartsWith(Prefix(kind), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Path, requestedPath, StringComparison.OrdinalIgnoreCase));
    }

    private static int DiscoveryPriority(string discoveryKind)
    {
        return discoveryKind switch
        {
            "codex-home" => 0,
            "codex-sqlite-folder" => 1,
            "snapshot-folder" => 2,
            _ => 3,
        };
    }

    private static CodexMemoryJob? MapMemoryJob(RawRow row)
    {
        return RequiredText(row, "kind") is { } kind &&
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
                NullableInt(row.Page, row.Row, "retry_remaining"),
                Text(row.Page, row.Row, "last_error"),
                Int64(row.Page, row.Row, "input_watermark"),
                Int64(row.Page, row.Row, "last_success_watermark"))
            : null;
    }

    private static CodexMemoryStage1Output? MapMemoryOutput(RawRow row)
    {
        return RequiredText(row, "thread_id") is { } threadId
            ? new CodexMemoryStage1Output(
                threadId,
                Int64(row.Page, row.Row, "source_updated_at"),
                Text(row.Page, row.Row, "rollout_slug"),
                Int64(row.Page, row.Row, "generated_at"),
                NullableInt(row.Page, row.Row, "usage_count"),
                NullableInt(row.Page, row.Row, "last_usage"),
                Bool(row.Page, row.Row, "selected_for_phase2"),
                Int64(row.Page, row.Row, "selected_for_phase2_source_updated_at"),
                HasValue(row.Page, row.Row, "raw_memory"),
                HasValue(row.Page, row.Row, "rollout_summary"))
            : null;
    }

    private static CodexGoal? MapGoal(RawRow row, ISet<string> deferrals, bool deferralsComplete)
    {
        return RequiredText(row, "thread_id") is { } threadId &&
               RequiredText(row, "goal_id") is { } goalId &&
               RequiredText(row, "status") is { } status
            ? new CodexGoal(
                threadId,
                goalId,
                Text(row.Page, row.Row, "objective"),
                status,
                Int64(row.Page, row.Row, "token_budget"),
                Int64(row.Page, row.Row, "tokens_used"),
                Int64(row.Page, row.Row, "time_used_seconds"),
                Int64(row.Page, row.Row, "created_at_ms"),
                Int64(row.Page, row.Row, "updated_at_ms"),
                deferrals.Contains(threadId) ? true : deferralsComplete ? false : null)
            : null;
    }

    private static CodexQueueItem? MapQueueItem(RawRow row, IReadOnlyDictionary<string, long> revisions)
    {
        return RequiredText(row, "id") is { } id &&
               RequiredText(row, "thread_id") is { } threadId
            ? new CodexQueueItem(
                id,
                threadId,
                Int64(row.Page, row.Row, "queue_order"),
                Int64(row.Page, row.Row, "created_at_ms"),
                Int64(row.Page, row.Row, "updated_at_ms"),
                revisions.TryGetValue(threadId, out long revision) ? revision : null,
                HasValue(row.Page, row.Row, "payload_json"))
            : null;
    }

    private static CodexThreadArtifact? MapArtifact(RawRow row)
    {
        return RequiredText(row, "id") is { } id &&
               RequiredText(row, "thread_id") is { } threadId &&
               RequiredText(row, "artifact_type") is { } artifactType &&
               RequiredText(row, "identity_key") is { } identityKey
            ? new CodexThreadArtifact(
                id,
                threadId,
                artifactType,
                identityKey,
                Int64(row.Page, row.Row, "created_at"),
                HasValue(row.Page, row.Row, "payload"))
            : null;
    }

    private static CodexDesktopCatalogEntry? MapCatalogEntry(RawRow row)
    {
        return RequiredText(row, "host_id") is { } hostId &&
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
                Int64(row.Page, row.Row, "observation_sequence"),
                Bool(row.Page, row.Row, "missing_candidate"),
                Text(row.Page, row.Row, "thread_source"),
                Double(row.Page, row.Row, "source_recency_at"),
                Bool(row.Page, row.Row, "pending_observed_title"),
                Text(row.Page, row.Row, "project_id"),
                Text(row.Page, row.Row, "conversation_origin"))
            : null;
    }

    private static CodexThreadSummary? MapSummary(RawRow row)
    {
        return RequiredText(row, "principal_key") is { } principal &&
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
                Int64(row.Page, row.Row, "revision"),
                Int64(row.Page, row.Row, "updated_at"))
            : null;
    }

    private static string? RequiredText(RawRow row, string column)
    {
        return Text(row.Page, row.Row, column) is { Length: > 0 } value ? value : null;
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Text(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        int index = -1;
        for (int i = 0; i < page.Columns.Count; i++)
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
        string? text = Text(page, row, column);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    private static int? NullableInt(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        long? value = Int64(page, row, column);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }

    private static bool? Bool(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        return Int64(page, row, column) switch { 0 => false, 1 => true, _ => null };
    }

    private static double? Double(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        string? text = Text(page, row, column);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : null;
    }

    private static bool? HasValue(CodexStateRawPage page, CodexStateRawRow row, string column)
    {
        return page.Columns.Contains(column, StringComparer.OrdinalIgnoreCase) ? Text(page, row, column) is not null : null;
    }

    private sealed record RawRow(CodexStateRawPage Page, CodexStateRawRow Row);

    private sealed record ReadRowsResult(IReadOnlyList<RawRow> Rows, bool IsTruncated)
    {
        public static ReadRowsResult Empty { get; } = new([], false);
    }

    private sealed record SourceContext(
        CodexNativeSourceInfo Info,
        IReadOnlyList<string> Tables,
        CodexStateInspectionResult? Inspection);
}