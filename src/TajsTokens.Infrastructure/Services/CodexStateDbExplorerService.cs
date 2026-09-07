using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

internal enum CodexOrderedSourceTable
{
    Jobs,
    Stage1Outputs,
    ThreadGoals,
    QueuedItems,
    ThreadArtifacts,
    LocalThreadCatalog,
    ThreadTurnSummaries
}

internal enum CodexRelationSourceTable
{
    ThreadGoalContinuationDeferrals,
    QueuedThreadRevisions
}

/// <summary>
/// Read-only inspection boundary for Codex's private SQLite databases and captured snapshots.
///
/// This service intentionally returns source-shaped schema and values. It does not reuse the
/// observatory models and does not write to either Codex or the TajsTokens database.
/// </summary>
public sealed class CodexStateDbExplorerService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private const int MaxRelationKeys = 250;
    private static readonly string RelationParameterList = string.Join(", ", Enumerable.Range(0, MaxRelationKeys).Select(static index => string.Concat("$value", index.ToString(CultureInfo.InvariantCulture))));
    private const int MaxFingerprintRows = 100_000;
    private const long MaxFingerprintDatabaseBytes = 64L * 1024 * 1024;

    private readonly string _codexHome;
    private readonly string? _snapshotDirectory;

    private sealed record OrderedSourceSpecification(
        string TableName,
        string OrderedQuery,
        string UnorderedQuery,
        IReadOnlyList<string> RequiredOrderColumns);

    private sealed record RelationSourceSpecification(
        string TableName,
        string OrderedQuery,
        string UnorderedQuery,
        string RequiredOrderColumn);

    private sealed record RowFingerprintResult(
        string Fingerprint,
        IReadOnlyList<CodexStateRowObservation> Observations,
        bool Complete,
        bool Bounded,
        string Note,
        string Mode)
    {
        public static RowFingerprintResult CountOnly(string fingerprint, string note) =>
            new(fingerprint, Array.Empty<CodexStateRowObservation>(), false, true, note, "count-only");

        public static RowFingerprintResult Unavailable(string fingerprint, string note) =>
            new(fingerprint, Array.Empty<CodexStateRowObservation>(), false, false, note, "unavailable");
    }

    public CodexStateDbExplorerService(string? codexHome = null, string? snapshotDirectory = null)
    {
        var useDefaultRoots = string.IsNullOrWhiteSpace(codexHome);
        _codexHome = Path.GetFullPath(string.IsNullOrWhiteSpace(codexHome)
            ? ResolveCodexHome()
            : codexHome);
        var resolvedSnapshotDirectory = snapshotDirectory ?? (useDefaultRoots ? ResolveSnapshotDirectory() : null);
        _snapshotDirectory = string.IsNullOrWhiteSpace(resolvedSnapshotDirectory)
            ? null
            : Path.GetFullPath(resolvedSnapshotDirectory);
    }

    public string CodexHome => _codexHome;

    public string? SnapshotDirectory => _snapshotDirectory;

    /// <summary>
    /// Finds Codex state databases and captured SQLite snapshots without opening or modifying them.
    /// </summary>
    public IReadOnlyList<CodexStateDatabaseCandidate> DiscoverCandidates()
    {
        try
        {
            var roots = new List<(string Path, string Pattern, string Description, string Kind)>();
            // Codex keeps several private SQLite stores in its home directory. The filename is
            // only a discovery hint; every .sqlite/.db file is surfaced for schema inspection.
            AddRoot(roots, _codexHome, "*.db", "Codex home", "codex-home");
            AddRoot(roots, _codexHome, "*.sqlite", "Codex home", "codex-home");
            AddRoot(roots, Path.Combine(_codexHome, "sqlite"), "*.db", "Codex sqlite folder", "codex-sqlite-folder");
            AddRoot(roots, Path.Combine(_codexHome, "sqlite"), "*.sqlite", "Codex sqlite folder", "codex-sqlite-folder");
            if (_snapshotDirectory is not null)
            {
                AddRoot(roots, _snapshotDirectory, "*.sqlite", "Snapshot folder", "snapshot-folder");
                AddRoot(roots, _snapshotDirectory, "*.db", "Snapshot folder", "snapshot-folder");
            }

            return roots
                .SelectMany(root => EnumerateRoot(root.Path, root.Pattern, root.Description, root.Kind))
                .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.SourceDescription, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(candidate => candidate.Generation ?? -1)
                .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Inspects every currently discovered source independently. Inspection failures are returned
    /// alongside successful results so an unavailable, locked, invalid, or disappearing source
    /// does not prevent local inspection of the remaining files.
    /// </summary>
    public async Task<CodexStateMultiInspectionResult> InspectDiscoveredAsync(
        bool includeRowFingerprints = false,
        CancellationToken cancellationToken = default)
    {
        var sources = new List<CodexStateSourceInspection>();
        foreach (var candidate in DiscoverCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var inspection = await InspectAsync(
                    candidate.Path,
                    includeRowFingerprints,
                    cancellationToken);
                sources.Add(new CodexStateSourceInspection(candidate, inspection, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceInspectionFailure(exception))
            {
                sources.Add(new CodexStateSourceInspection(
                    candidate,
                    null,
                    SummarizeSourceFailure(exception)));
            }
        }

        return new CodexStateMultiInspectionResult(sources);
    }

    public async Task<CodexStateInspectionResult> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
        => await InspectAsync(databasePath, includeRowFingerprints: true, cancellationToken: cancellationToken);

    public async Task<CodexStateInspectionResult> InspectAsync(
        string databasePath,
        bool includeRowFingerprints,
        CancellationToken cancellationToken = default)
    {
        var candidate = CreateCandidateForPath(databasePath);
        await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);

        var tables = new List<CodexStateTableInfo>();
        var schemaObjects = new List<CodexStateSchemaObjectInfo>();
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_master
            ORDER BY type COLLATE BINARY, name COLLATE BINARY;
            """;

        await using var schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
        while (await schemaReader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectType = schemaReader.GetString(0);
            var objectName = schemaReader.GetString(1);
            var associatedTableName = schemaReader.IsDBNull(2) ? null : schemaReader.GetString(2);
            // sqlite_master columns are (type, name, tbl_name, sql). Keep the raw object
            // definition for every object, including standalone indexes and triggers.
            var sql = schemaReader.IsDBNull(3) ? null : schemaReader.GetString(3);
            schemaObjects.Add(new CodexStateSchemaObjectInfo(objectName, objectType, sql)
            {
                AssociatedTableName = associatedTableName
            });

            if (objectType is not ("table" or "view"))
            {
                continue;
            }

            var columns = await ReadColumnsAsync(connection, objectName, cancellationToken);
            var indexes = await ReadIndexesAsync(connection, objectName, cancellationToken);
            var rowCount = await TryReadRowCountAsync(connection, objectName, cancellationToken);

            var table = new CodexStateTableInfo(objectName, objectType, sql, rowCount, columns, indexes);
            tables.Add(table with { SchemaFingerprint = FingerprintSchema(table) });
        }

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var snapshots = new List<CodexStateTableSnapshot>(tables.Count);
        var rowObservations = new Dictionary<string, IReadOnlyList<CodexStateRowObservation>>(
            StringComparer.OrdinalIgnoreCase);
        var allowDeepRowFingerprint = includeRowFingerprints && candidate.SizeBytes <= MaxFingerprintDatabaseBytes;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schemaFingerprint = table.SchemaFingerprint;
            var rowResult = includeRowFingerprints
                ? await ComputeRowFingerprintAsync(
                    connection,
                    table,
                    allowDeepRowFingerprint,
                    candidate,
                    cancellationToken)
                : RowFingerprintResult.CountOnly(FingerprintBoundedRowObservation(table),
                    "count-only inspection requested");
            snapshots.Add(new CodexStateTableSnapshot(
                table.Name,
                table.ObjectType,
                table.RowCount,
                schemaFingerprint,
                rowResult.Fingerprint)
            {
                RowComparisonComplete = rowResult.Complete,
                RowComparisonBounded = rowResult.Bounded,
                RowComparisonNote = rowResult.Note,
                RowObservationCount = rowResult.Observations.Count,
                RowFingerprintMode = rowResult.Mode,
                Columns = table.Columns,
                Indexes = table.Indexes
            });
            rowObservations[table.Name] = rowResult.Observations
                .Select(observation => observation with { CapturedAtUtc = capturedAtUtc })
                .ToArray();
        }

        var snapshot = new CodexStateInspectionSnapshot(
            candidate.Path,
            capturedAtUtc,
            snapshots)
        {
            SchemaObjects = schemaObjects,
            SchemaFingerprint = ComputeSchemaFingerprint(schemaObjects, snapshots),
            RowObservations = rowObservations,
            SourceDescription = candidate.SourceDescription
        };

        return new CodexStateInspectionResult(candidate, tables, snapshot);
    }

    public async Task<CodexStateRawPage> ReadPageAsync(
        string databasePath,
        string tableName,
        int pageIndex = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        pageIndex = Math.Max(0, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var candidate = CreateCandidateForPath(databasePath);
        await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
        var objectType = await ReadObjectTypeAsync(connection, tableName, cancellationToken);
        if (objectType is null)
        {
            throw new KeyNotFoundException($"The source database does not contain a table or view named '{tableName}'.");
        }

        var totalRows = await TryReadRowCountAsync(connection, tableName, cancellationToken);
        var offset = checked((long)pageIndex * pageSize);
        var columns = new List<string>();
        var rows = new List<CodexStateRawRow>();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(reader.GetName(index));
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[reader.FieldCount];
            var display = new StringBuilder();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                if (index > 0)
                {
                    display.Append("  |  ");
                }

                display.Append(FormatRawValue(values[index]));
            }

            rows.Add(new CodexStateRawRow(values, display.ToString()));
        }

        return new CodexStateRawPage(
            candidate.Path,
            tableName,
            pageIndex,
            pageSize,
            totalRows,
            columns,
            rows);
    }

    /// <summary>
    /// Reads a bounded page after applying source-native ordering. Ordering belongs at the SQLite
    /// boundary so a table larger than the page cannot hide its newest/highest-ranked rows.
    /// Unknown order columns are ignored to keep schema variants inspectable.
    /// </summary>
    internal async Task<CodexStateRawPage> ReadOrderedPageAsync(
        string databasePath,
        CodexOrderedSourceTable sourceTable,
        int pageIndex = 0,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        pageIndex = Math.Max(0, pageIndex);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var specification = sourceTable switch
        {
            CodexOrderedSourceTable.Jobs => new OrderedSourceSpecification("jobs", "SELECT * FROM jobs ORDER BY started_at DESC, kind ASC, job_key ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM jobs LIMIT $limit OFFSET $offset;", ["started_at", "kind", "job_key"]),
            CodexOrderedSourceTable.Stage1Outputs => new OrderedSourceSpecification("stage1_outputs", "SELECT * FROM stage1_outputs ORDER BY source_updated_at DESC, thread_id ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM stage1_outputs LIMIT $limit OFFSET $offset;", ["source_updated_at", "thread_id"]),
            CodexOrderedSourceTable.ThreadGoals => new OrderedSourceSpecification("thread_goals", "SELECT * FROM thread_goals ORDER BY updated_at_ms DESC, thread_id ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM thread_goals LIMIT $limit OFFSET $offset;", ["updated_at_ms", "thread_id"]),
            CodexOrderedSourceTable.QueuedItems => new OrderedSourceSpecification("queued_items", "SELECT * FROM queued_items ORDER BY thread_id ASC, queue_order ASC, id ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM queued_items LIMIT $limit OFFSET $offset;", ["thread_id", "queue_order", "id"]),
            CodexOrderedSourceTable.ThreadArtifacts => new OrderedSourceSpecification("thread_artifacts", "SELECT * FROM thread_artifacts ORDER BY thread_id ASC, created_at ASC, id ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM thread_artifacts LIMIT $limit OFFSET $offset;", ["thread_id", "created_at", "id"]),
            CodexOrderedSourceTable.LocalThreadCatalog => new OrderedSourceSpecification("local_thread_catalog", "SELECT * FROM local_thread_catalog ORDER BY source_recency_at DESC, source_created_at DESC, host_id ASC, thread_id ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM local_thread_catalog LIMIT $limit OFFSET $offset;", ["source_recency_at", "source_created_at", "host_id", "thread_id"]),
            CodexOrderedSourceTable.ThreadTurnSummaries => new OrderedSourceSpecification("thread_turn_summaries", "SELECT * FROM thread_turn_summaries ORDER BY updated_at DESC, thread_id ASC LIMIT $limit OFFSET $offset;", "SELECT * FROM thread_turn_summaries LIMIT $limit OFFSET $offset;", ["updated_at", "thread_id"]),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceTable), sourceTable, "Unknown ordered Codex source table.")
        };

        var candidate = CreateCandidateForPath(databasePath);
        await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
        var objectType = await ReadObjectTypeAsync(connection, specification.TableName, cancellationToken);
        if (objectType is null)
        {
            throw new KeyNotFoundException($"The source database does not contain a table or view named '{specification.TableName}'.");
        }

        var availableColumns = (await ReadColumnsAsync(connection, specification.TableName, cancellationToken))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var totalRows = await TryReadRowCountAsync(connection, specification.TableName, cancellationToken);
        var offset = checked((long)pageIndex * pageSize);
        var query = specification.RequiredOrderColumns.All(availableColumns.Contains)
            ? specification.OrderedQuery
            : specification.UnorderedQuery;
        return await ReadRawPageAsync(
            connection,
            candidate.Path,
            specification.TableName,
            pageIndex,
            pageSize,
            totalRows,
            () =>
            {
                var command = new SqliteCommand(query, connection);
                command.Parameters.AddWithValue("$limit", pageSize);
                command.Parameters.AddWithValue("$offset", offset);
                return command;
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads a bounded, source-ordered page from the dedicated logs table. The query text is built
    /// only from fixed source-native column names; all user filters, limits and offsets are bound
    /// parameters. Log bodies are selected only when the caller explicitly opts in.
    /// </summary>
    internal async Task<CodexStateRawPage> ReadLogsPageAsync(
        string databasePath,
        CodexLogsQuery query,
        CodexLogsCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(capabilities);

        var pageIndex = Math.Max(0, query.PageIndex);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize - 1);
        var offset = checked((long)pageIndex * pageSize);
        var filters = BuildLogFilters(query, capabilities, out var parameters);
        var candidate = CreateCandidateForPath(databasePath);
        await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM logs{filters};";
        AddLogParameters(countCommand, parameters);
        var scalar = await countCommand.ExecuteScalarAsync(cancellationToken);
        var totalRows = scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);

        var messageColumn = capabilities.MessageColumn;
        var messageExpression = query.IncludeMessages && messageColumn is not null
            ? QuoteIdentifier(messageColumn)
            : "NULL";
        var messagePresentExpression = messageColumn is null
            ? "0"
            : $"{QuoteIdentifier(messageColumn)} IS NOT NULL";
        var select = $"""
            SELECT
                id AS id,
                ts AS ts,
                ts_nanos AS ts_nanos,
                level AS level,
                target AS target,
                {messageExpression} AS message,
                {messagePresentExpression} AS has_message,
                {SelectLogColumn(capabilities.HasModulePath, "module_path")} AS module_path,
                {SelectLogColumn(capabilities.HasFile, "file")} AS file,
                {SelectLogColumn(capabilities.HasLine, "line")} AS line,
                {SelectLogColumn(capabilities.HasThreadId, "thread_id")} AS thread_id,
                {SelectLogColumn(capabilities.HasProcessUuid, "process_uuid")} AS process_uuid,
                {SelectLogColumn(capabilities.HasEstimatedBytes, "estimated_bytes")} AS estimated_bytes
            FROM logs
            {filters}
            ORDER BY ts DESC, ts_nanos DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;

        return await ReadRawPageAsync(
            connection,
            candidate.Path,
            "logs",
            pageIndex,
            pageSize,
            totalRows,
            () =>
            {
                var command = new SqliteCommand(select, connection);
                AddLogParameters(command, parameters);
                command.Parameters.AddWithValue("$limit", pageSize + 1);
                command.Parameters.AddWithValue("$offset", offset);
                return command;
            },
            cancellationToken);
    }

    private static string SelectLogColumn(bool available, string columnName) =>
        available ? QuoteIdentifier(columnName) : "NULL";

    private static string BuildLogFilters(
        CodexLogsQuery query,
        CodexLogsCapabilities capabilities,
        out IReadOnlyList<(string Name, object Value)> parameters)
    {
        var clauses = new List<string>();
        var values = new List<(string Name, object Value)>();

        var levels = (query.Levels ?? Array.Empty<string>())
            .Select(level => level?.Trim())
            .Where(level => !string.IsNullOrWhiteSpace(level))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        if (levels.Length > 0)
        {
            var names = new List<string>(levels.Length);
            for (var index = 0; index < levels.Length; index++)
            {
                var name = $"$level{index.ToString(CultureInfo.InvariantCulture)}";
                names.Add(name);
                values.Add((name, levels[index]!));
            }

            clauses.Add($"level COLLATE NOCASE IN ({string.Join(", ", names)})");
        }

        if (query.FromUtc is { } fromUtc)
        {
            var name = "$from_ts";
            clauses.Add("ts >= $from_ts");
            values.Add((name, ToSourceTimestampBound(fromUtc)));
        }

        if (query.ToUtcExclusive is { } toUtcExclusive)
        {
            var name = "$to_ts";
            clauses.Add("ts < $to_ts");
            values.Add((name, ToSourceTimestampBound(toUtcExclusive)));
        }

        AddContainsFilter(clauses, values, "target", query.TargetContains, "$target");
        AddOptionalContainsFilter(clauses, values, capabilities.HasModulePath, "module_path", query.ModulePathContains, "$module_path");
        AddOptionalContainsFilter(clauses, values, capabilities.HasFile, "file", query.FileContains, "$file");

        AddOptionalExactFilter(clauses, values, capabilities.HasThreadId, "thread_id", query.ThreadId, "$thread_id");
        AddOptionalExactFilter(clauses, values, capabilities.HasProcessUuid, "process_uuid", query.ProcessUuid, "$process_uuid");

        if (!query.IncludeThreadless)
        {
            clauses.Add(capabilities.HasThreadId ? "thread_id IS NOT NULL" : "1 = 0");
        }

        parameters = values;
        return clauses.Count == 0
            ? string.Empty
            : $" WHERE {string.Join(" AND ", clauses)}";
    }

    private static void AddContainsFilter(
        ICollection<string> clauses,
        ICollection<(string Name, object Value)> parameters,
        string columnName,
        string? value,
        string parameterName)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
        {
            return;
        }

        clauses.Add($"{QuoteIdentifier(columnName)} LIKE {parameterName} ESCAPE '\\' COLLATE NOCASE");
        parameters.Add((parameterName, $"%{EscapeLikePattern(normalized)}%"));
    }

    private static void AddOptionalContainsFilter(
        ICollection<string> clauses,
        ICollection<(string Name, object Value)> parameters,
        bool available,
        string columnName,
        string? value,
        string parameterName)
    {
        if (TrimToNull(value) is null)
        {
            return;
        }

        if (!available)
        {
            clauses.Add("1 = 0");
            return;
        }

        AddContainsFilter(clauses, parameters, columnName, value, parameterName);
    }

    private static void AddOptionalExactFilter(
        ICollection<string> clauses,
        ICollection<(string Name, object Value)> parameters,
        bool available,
        string columnName,
        string? value,
        string parameterName)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
        {
            return;
        }

        if (!available)
        {
            clauses.Add("1 = 0");
            return;
        }

        clauses.Add($"{QuoteIdentifier(columnName)} = {parameterName}");
        parameters.Add((parameterName, normalized));
    }

    private static void AddLogParameters(
        SqliteCommand command,
        IReadOnlyList<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long ToSourceTimestampBound(DateTimeOffset value)
    {
        var seconds = value.ToUnixTimeSeconds();
        return value > DateTimeOffset.FromUnixTimeSeconds(seconds)
            ? checked(seconds + 1)
            : seconds;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Reads rows whose source-native relation key is one of the supplied values.</summary>
    internal async Task<CodexStateRawPage> ReadRowsByTextValuesAsync(
        string databasePath,
        CodexRelationSourceTable sourceTable,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Count, MaxRelationKeys);
        var specification = sourceTable switch
        {
            CodexRelationSourceTable.ThreadGoalContinuationDeferrals => new RelationSourceSpecification(
                "thread_goal_continuation_deferrals",
                BuildRelationQuery("thread_goal_continuation_deferrals", ordered: false),
                BuildRelationQuery("thread_goal_continuation_deferrals", ordered: false),
                string.Empty),
            CodexRelationSourceTable.QueuedThreadRevisions => new RelationSourceSpecification(
                "queued_thread_revisions",
                BuildRelationQuery("queued_thread_revisions", ordered: true),
                BuildRelationQuery("queued_thread_revisions", ordered: false),
                "revision"),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceTable), sourceTable, "Unknown Codex relation table.")
        };

        var candidate = CreateCandidateForPath(databasePath);
        await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
        var objectType = await ReadObjectTypeAsync(connection, specification.TableName, cancellationToken);
        if (objectType is null)
        {
            throw new KeyNotFoundException($"The source database does not contain a table or view named '{specification.TableName}'.");
        }

        var availableColumns = (await ReadColumnsAsync(connection, specification.TableName, cancellationToken))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!availableColumns.Contains("thread_id"))
        {
            return new CodexStateRawPage(candidate.Path, specification.TableName, 0, 1, 0, [], []);
        }

        var distinctValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctValues.Length == 0)
        {
            return new CodexStateRawPage(candidate.Path, specification.TableName, 0, 1, 0, [], []);
        }

        // The key set is already bounded by the gateway's visible base page. Do not apply a
        // second arbitrary row limit here: relation rows must remain complete for those keys.
        var query = specification.RequiredOrderColumn.Length > 0 && availableColumns.Contains(specification.RequiredOrderColumn)
            ? specification.OrderedQuery
            : specification.UnorderedQuery;
        return await ReadRawPageAsync(
            connection,
            candidate.Path,
            specification.TableName,
            0,
            MaxPageSize,
            -1,
            () =>
            {
                var command = new SqliteCommand(query, connection);
                for (var index = 0; index < MaxRelationKeys; index++)
                {
                    command.Parameters.AddWithValue(
                        string.Concat("$value", index.ToString(CultureInfo.InvariantCulture)),
                        index < distinctValues.Length ? distinctValues[index] : DBNull.Value);
                }

                return command;
            },
            cancellationToken);
    }

    private static string BuildRelationQuery(string tableName, bool ordered) =>
        tableName switch
        {
            "thread_goal_continuation_deferrals" => string.Concat(
                "SELECT * FROM thread_goal_continuation_deferrals WHERE thread_id IN (",
                RelationParameterList,
                ");"),
            "queued_thread_revisions" when ordered => string.Concat(
                "SELECT * FROM queued_thread_revisions WHERE thread_id IN (",
                RelationParameterList,
                ") ORDER BY revision DESC;"),
            "queued_thread_revisions" => string.Concat(
                "SELECT * FROM queued_thread_revisions WHERE thread_id IN (",
                RelationParameterList,
                ");"),
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown Codex relation table.")
        };

    /// <summary>Reads a source-native integer aggregate without applying a bounded row page.</summary>
    public async Task<long?> ReadMaxInt64Async(
        string databasePath,
        string tableName,
        string columnName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        var candidate = CreateCandidateForPath(databasePath);
        await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
        var objectType = await ReadObjectTypeAsync(connection, tableName, cancellationToken);
        if (objectType is null)
        {
            throw new KeyNotFoundException($"The source database does not contain a table or view named '{tableName}'.");
        }

        var availableColumns = (await ReadColumnsAsync(connection, tableName, cancellationToken))
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!availableColumns.Contains(columnName))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(CAST({QuoteIdentifier(columnName)} AS INTEGER)) FROM {QuoteIdentifier(tableName)};";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<CodexStateRawPage> ReadRawPageAsync(
        SqliteConnection connection,
        string databasePath,
        string tableName,
        int pageIndex,
        int pageSize,
        long totalRows,
        Func<SqliteCommand> createCommand,
        CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var rows = new List<CodexStateRawRow>();
        await using var command = createCommand();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(reader.GetName(index));
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[reader.FieldCount];
            var display = new StringBuilder();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                if (index > 0)
                {
                    display.Append("  |  ");
                }

                display.Append(FormatRawValue(values[index]));
            }

            rows.Add(new CodexStateRawRow(values, display.ToString()));
        }

        return new CodexStateRawPage(databasePath, tableName, pageIndex, pageSize, totalRows, columns, rows);
    }

    /// <summary>
    /// Searches the supplied source files for an exact column name and exact value. The query is
    /// intentionally per-object and unjoined: a match is evidence from that source object only,
    /// not an inferred relationship between databases or tables.
    /// </summary>
    public async Task<CodexStateKeyTraceResult> TraceKeyAsync(
        IEnumerable<string> databasePaths,
        string columnName,
        string value,
        int maxMatches = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(databasePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        maxMatches = Math.Clamp(maxMatches, 1, 5_000);

        var matches = new List<CodexStateKeyTraceMatch>();
        var unavailable = new List<string>();
        var paths = new List<string>();
        foreach (var path in databasePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(fullPath);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                unavailable.Add($"{path}: {exception.Message.ReplaceLineEndings(" ").Trim()}");
            }
        }

        var truncated = false;

        foreach (var databasePath in paths)
        {
            if (truncated)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            CodexStateInspectionResult inspection;
            try
            {
                // Schema-only inspection avoids scanning content-heavy source tables twice.
                inspection = await InspectAsync(
                    databasePath,
                    includeRowFingerprints: false,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // A single unavailable/corrupt source must not hide matches from the other
                // discovered stores. Preserve the failure as source evidence for the caller.
                unavailable.Add($"{databasePath}: {exception.Message.ReplaceLineEndings(" ").Trim()}");
                continue;
            }

            var matchingTables = inspection.Tables
                .Where(table => table.Columns.Any(column =>
                    string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (matchingTables.Length == 0)
            {
                continue;
            }

            SqliteConnection connection;
            try
            {
                connection = await OpenReadOnlyAsync(databasePath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                unavailable.Add($"{databasePath}: {exception.Message.ReplaceLineEndings(" ").Trim()}");
                continue;
            }

            await using (connection)
            {
                foreach (var table in matchingTables)
                {
                    if (truncated)
                    {
                        break;
                    }

                    var sourceColumn = table.Columns.First(column =>
                        string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)} WHERE {QuoteIdentifier(sourceColumn.Name)} COLLATE BINARY = $value COLLATE BINARY LIMIT $limit;";
                    command.Parameters.AddWithValue("$value", value);
                    command.Parameters.AddWithValue("$limit", maxMatches - matches.Count + 1);

                    try
                    {
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        var columns = Enumerable.Range(0, reader.FieldCount)
                            .Select(reader.GetName)
                            .ToArray();
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            if (matches.Count >= maxMatches)
                            {
                                truncated = true;
                                break;
                            }

                            var values = new object?[reader.FieldCount];
                            for (var index = 0; index < reader.FieldCount; index++)
                            {
                                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                            }

                            matches.Add(new CodexStateKeyTraceMatch(
                                inspection.Database.Path,
                                inspection.Database.SourceDescription,
                                table.Name,
                                sourceColumn.Name,
                                columns,
                                values)
                            {
                                SourceCandidate = inspection.Database,
                                InspectionCapturedAtUtc = inspection.Snapshot.CapturedAtUtc
                            });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        unavailable.Add($"{databasePath} · {table.Name}: {exception.Message.ReplaceLineEndings(" ").Trim()}");
                    }
                }
            }
        }

        return new CodexStateKeyTraceResult(columnName, value, matches)
        {
            UnavailableSources = unavailable,
            MayBeTruncated = truncated
        };
    }

    public static CodexStateInspectionDiff Compare(
        CodexStateInspectionSnapshot baseline,
        CodexStateInspectionSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var before = baseline.ByName;
        var after = current.ByName;
        var diffs = new List<CodexStateTableDiff>();

        foreach (var name in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var hasBefore = before.TryGetValue(name, out var previous);
            var hasAfter = after.TryGetValue(name, out var next);
            if (!hasBefore)
            {
                diffs.Add(CreateTableDiff(
                    baseline,
                    current,
                    name,
                    "added",
                    null,
                    next!.RowCount,
                    schemaChanged: true,
                    rowsChanged: true,
                    rowComparisonComplete: false,
                    rowComparisonBounded: false,
                    "table added; no baseline row comparison is available",
                    addedColumns: next.Columns.Select(column => column.Name).ToArray(),
                    addedIndexes: next.Indexes.Select(index => index.Name).ToArray()));
                continue;
            }

            if (!hasAfter)
            {
                diffs.Add(CreateTableDiff(
                    baseline,
                    current,
                    name,
                    "removed",
                    previous!.RowCount,
                    null,
                    schemaChanged: true,
                    rowsChanged: true,
                    rowComparisonComplete: false,
                    rowComparisonBounded: false,
                    "table removed; no current row comparison is available",
                    removedColumns: previous.Columns.Select(column => column.Name).ToArray(),
                    removedIndexes: previous.Indexes.Select(index => index.Name).ToArray()));
                continue;
            }

            var schemaChanged = !string.Equals(previous!.SchemaFingerprint, next!.SchemaFingerprint, StringComparison.Ordinal);
            var rowCountChanged = previous.RowCount >= 0 && next.RowCount >= 0 && previous.RowCount != next.RowCount;
            var rowFingerprintModesComparable =
                (!string.IsNullOrWhiteSpace(previous.RowFingerprintMode) ||
                 !string.IsNullOrWhiteSpace(next.RowFingerprintMode))
                    ? string.Equals(previous.RowFingerprintMode, next.RowFingerprintMode, StringComparison.Ordinal)
                    : true;
            var rowsChanged = rowCountChanged ||
                (rowFingerprintModesComparable &&
                 !string.Equals(previous.RowFingerprint, next.RowFingerprint, StringComparison.Ordinal));
            var rowComparisonComplete = previous.RowComparisonComplete && next.RowComparisonComplete;
            var rowComparisonBounded = previous.RowComparisonBounded || next.RowComparisonBounded;
            var rowIdentityPairingSafe = rowComparisonComplete && CanPairRows(baseline, current, previous, next);
            rowComparisonComplete = rowComparisonComplete && rowIdentityPairingSafe;
            var rowChanges = rowIdentityPairingSafe
                ? CompareRows(baseline, current, previous, next)
                : Array.Empty<CodexStateRowDiff>();
            var rowComparisonNote = BuildRowComparisonNote(
                previous,
                next,
                rowComparisonComplete,
                rowComparisonBounded,
                rowFingerprintModesComparable,
                rowIdentityPairingSafe);
            var schemaDetails = CompareSchemaDetails(previous, next);

            if (schemaChanged || rowsChanged || !rowComparisonComplete)
            {
                var changeKind = schemaChanged || rowsChanged ? "changed" : "incomplete";
                diffs.Add(CreateTableDiff(
                    baseline,
                    current,
                    name,
                    changeKind,
                    previous.RowCount,
                    next.RowCount,
                    schemaChanged,
                    rowsChanged,
                    rowComparisonComplete,
                    rowComparisonBounded,
                    rowComparisonNote,
                    rowChanges,
                    schemaDetails.AddedColumns,
                    schemaDetails.RemovedColumns,
                    schemaDetails.ChangedColumns,
                    schemaDetails.AddedIndexes,
                    schemaDetails.RemovedIndexes,
                    schemaDetails.ChangedIndexes));
            }
        }

        return new CodexStateInspectionDiff(
            baseline.CapturedAtUtc,
            current.CapturedAtUtc,
            diffs)
        {
            BaselineDatabasePath = baseline.DatabasePath,
            CurrentDatabasePath = current.DatabasePath,
            BaselineSchemaFingerprint = baseline.SchemaFingerprint,
            CurrentSchemaFingerprint = current.SchemaFingerprint,
            BaselineSourceDescription = baseline.SourceDescription,
            CurrentSourceDescription = current.SourceDescription
        };
    }

    private static CodexStateTableDiff CreateTableDiff(
        CodexStateInspectionSnapshot baseline,
        CodexStateInspectionSnapshot current,
        string name,
        string changeKind,
        long? previousRowCount,
        long? currentRowCount,
        bool schemaChanged,
        bool rowsChanged,
        bool rowComparisonComplete,
        bool rowComparisonBounded,
        string rowComparisonNote,
        IReadOnlyList<CodexStateRowDiff>? rowChanges = null,
        IReadOnlyList<string>? addedColumns = null,
        IReadOnlyList<string>? removedColumns = null,
        IReadOnlyList<string>? changedColumns = null,
        IReadOnlyList<string>? addedIndexes = null,
        IReadOnlyList<string>? removedIndexes = null,
        IReadOnlyList<string>? changedIndexes = null)
    {
        return new CodexStateTableDiff(
            name,
            changeKind,
            previousRowCount,
            currentRowCount,
            schemaChanged,
            rowsChanged)
        {
            BaselineDatabasePath = baseline.DatabasePath,
            CurrentDatabasePath = current.DatabasePath,
            BaselineSourceDescription = baseline.SourceDescription,
            CurrentSourceDescription = current.SourceDescription,
            BaselineCapturedAtUtc = baseline.CapturedAtUtc,
            CurrentCapturedAtUtc = current.CapturedAtUtc,
            RowComparisonComplete = rowComparisonComplete,
            RowComparisonBounded = rowComparisonBounded,
            RowComparisonNote = rowComparisonNote,
            RowChanges = rowChanges ?? Array.Empty<CodexStateRowDiff>(),
            AddedColumns = addedColumns ?? Array.Empty<string>(),
            RemovedColumns = removedColumns ?? Array.Empty<string>(),
            ChangedColumns = changedColumns ?? Array.Empty<string>(),
            AddedIndexes = addedIndexes ?? Array.Empty<string>(),
            RemovedIndexes = removedIndexes ?? Array.Empty<string>(),
            ChangedIndexes = changedIndexes ?? Array.Empty<string>()
        };
    }

    private static SchemaDifference CompareSchemaDetails(
        CodexStateTableSnapshot previous,
        CodexStateTableSnapshot next)
    {
        var beforeColumns = previous.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var afterColumns = next.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var addedColumns = afterColumns.Keys.Except(beforeColumns.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var removedColumns = beforeColumns.Keys.Except(afterColumns.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var changedColumns = beforeColumns.Keys.Intersect(afterColumns.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(name => !Equals(beforeColumns[name], afterColumns[name]))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var beforeIndexes = previous.Indexes.ToDictionary(index => index.Name, StringComparer.OrdinalIgnoreCase);
        var afterIndexes = next.Indexes.ToDictionary(index => index.Name, StringComparer.OrdinalIgnoreCase);
        var addedIndexes = afterIndexes.Keys.Except(beforeIndexes.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var removedIndexes = beforeIndexes.Keys.Except(afterIndexes.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var changedIndexes = beforeIndexes.Keys.Intersect(afterIndexes.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(name => !Equals(beforeIndexes[name], afterIndexes[name]))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SchemaDifference(
            addedColumns,
            removedColumns,
            changedColumns,
            addedIndexes,
            removedIndexes,
            changedIndexes);
    }

    private sealed record SchemaDifference(
        IReadOnlyList<string> AddedColumns,
        IReadOnlyList<string> RemovedColumns,
        IReadOnlyList<string> ChangedColumns,
        IReadOnlyList<string> AddedIndexes,
        IReadOnlyList<string> RemovedIndexes,
        IReadOnlyList<string> ChangedIndexes);

    private static bool CanPairRows(
        CodexStateInspectionSnapshot baseline,
        CodexStateInspectionSnapshot current,
        CodexStateTableSnapshot previous,
        CodexStateTableSnapshot next)
    {
        if (!baseline.RowObservations.TryGetValue(previous.Name, out var beforeRows) ||
            !current.RowObservations.TryGetValue(next.Name, out var afterRows))
        {
            return false;
        }

        return beforeRows.Select(row => row.RowIdentity)
                   .Distinct(StringComparer.Ordinal)
                   .Count() == beforeRows.Count &&
               afterRows.Select(row => row.RowIdentity)
                   .Distinct(StringComparer.Ordinal)
                   .Count() == afterRows.Count;
    }

    private static IReadOnlyList<CodexStateRowDiff> CompareRows(
        CodexStateInspectionSnapshot baseline,
        CodexStateInspectionSnapshot current,
        CodexStateTableSnapshot previous,
        CodexStateTableSnapshot next)
    {
        if (!baseline.RowObservations.TryGetValue(previous.Name, out var beforeRows) ||
            !current.RowObservations.TryGetValue(next.Name, out var afterRows))
        {
            return Array.Empty<CodexStateRowDiff>();
        }

        var beforeGroups = beforeRows.GroupBy(row => row.RowIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var afterGroups = afterRows.GroupBy(row => row.RowIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        // Duplicate identities are ambiguous source evidence. Do not manufacture a row pairing.
        if (beforeGroups.Any(group => group.Value.Length != 1) || afterGroups.Any(group => group.Value.Length != 1))
        {
            return Array.Empty<CodexStateRowDiff>();
        }

        var changes = new List<CodexStateRowDiff>();
        foreach (var identity in beforeGroups.Keys.Union(afterGroups.Keys, StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var hasBefore = beforeGroups.TryGetValue(identity, out var beforeGroup);
            var hasAfter = afterGroups.TryGetValue(identity, out var afterGroup);
            var beforeRow = hasBefore ? beforeGroup![0] : null;
            var afterRow = hasAfter ? afterGroup![0] : null;
            var changeKind = !hasBefore
                ? "added"
                : !hasAfter
                    ? "removed"
                    : string.Equals(beforeRow!.RowHash, afterRow!.RowHash, StringComparison.Ordinal)
                        ? null
                        : "changed";
            if (changeKind is null)
            {
                continue;
            }

            changes.Add(new CodexStateRowDiff(
                next.Name,
                changeKind,
                identity,
                beforeRow?.RowHash,
                afterRow?.RowHash,
                beforeRow?.Columns ?? Array.Empty<string>(),
                beforeRow?.Values ?? Array.Empty<object?>(),
                afterRow?.Columns ?? Array.Empty<string>(),
                afterRow?.Values ?? Array.Empty<object?>())
            {
                BaselineDatabasePath = baseline.DatabasePath,
                CurrentDatabasePath = current.DatabasePath,
                BaselineSourceDescription = baseline.SourceDescription,
                CurrentSourceDescription = current.SourceDescription,
                BaselineCapturedAtUtc = baseline.CapturedAtUtc,
                CurrentCapturedAtUtc = current.CapturedAtUtc,
                IdentityKind = afterRow?.IdentityKind ?? beforeRow?.IdentityKind ?? "unknown"
            });
        }

        return changes;
    }

    private static string BuildRowComparisonNote(
        CodexStateTableSnapshot previous,
        CodexStateTableSnapshot next,
        bool complete,
        bool bounded,
        bool rowFingerprintModesComparable,
        bool rowIdentityPairingSafe)
    {
        if (complete)
        {
            return $"complete row observation ({previous.RowObservationCount:N0} → {next.RowObservationCount:N0} rows)";
        }

        var notes = new[] { previous.RowComparisonNote, next.RowComparisonNote }
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var prefix = bounded ? "bounded/count-only" : "row comparison unavailable";
        if (!rowFingerprintModesComparable)
        {
            notes = notes.Append(
                    $"fingerprint modes differ ({previous.RowFingerprintMode} vs {next.RowFingerprintMode})")
                .ToArray();
        }

        if (!rowIdentityPairingSafe)
        {
            notes = notes.Append("row identities are missing or ambiguous; row-level pairing omitted").ToArray();
        }

        return notes.Length == 0 ? prefix : $"{prefix}: {string.Join("; ", notes)}";
    }

    /// <summary>
    /// Computes a database-level schema fingerprint from sorted source-object fingerprints. Row
    /// observations are deliberately excluded so the value changes only when schema metadata does.
    /// </summary>
    public static string ComputeSchemaFingerprint(
        IEnumerable<CodexStateTableSnapshot> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return ComputeSchemaFingerprint(Array.Empty<CodexStateSchemaObjectInfo>(), tables);
    }

    /// <summary>
    /// Computes a database fingerprint from the observed schema objects and table metadata. All
    /// values are length-prefixed so null, empty, and delimiter-containing source values remain
    /// distinct. Row content and row fingerprints are intentionally excluded.
    /// </summary>
    public static string ComputeSchemaFingerprint(
        IEnumerable<CodexStateSchemaObjectInfo> schemaObjects,
        IEnumerable<CodexStateTableSnapshot> tables)
    {
        ArgumentNullException.ThrowIfNull(schemaObjects);
        ArgumentNullException.ThrowIfNull(tables);

        var builder = new StringBuilder();
        foreach (var schemaObject in schemaObjects
                     .OrderBy(value => value.ObjectType, StringComparer.Ordinal)
                     .ThenBy(value => value.Name, StringComparer.Ordinal))
        {
            AppendSchemaText(builder, "object.type", schemaObject.ObjectType);
            AppendSchemaText(builder, "object.name", schemaObject.Name);
            AppendSchemaText(builder, "object.table", schemaObject.AssociatedTableName);
            AppendSchemaText(builder, "object.sql", schemaObject.Sql);
        }

        foreach (var table in tables
                     .OrderBy(value => value.ObjectType, StringComparer.Ordinal)
                     .ThenBy(value => value.Name, StringComparer.Ordinal))
        {
            AppendSchemaText(builder, "table.type", table.ObjectType);
            AppendSchemaText(builder, "table.name", table.Name);
            AppendSchemaText(builder, "table.schema", table.SchemaFingerprint);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// Creates a text export for a selected page. Column-name heuristics redact content-bearing
    /// values and paths, while the live explorer continues to expose the raw values in memory.
    /// </summary>
    public static string BuildSanitizedExport(CodexStateRawPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var builder = new StringBuilder();
        builder.AppendLine("Codex state SQLite inspection (sanitized export)");
        builder.Append("Database file: ").AppendLine(Path.GetFileName(page.DatabasePath));
        builder.Append("Table/view: ").AppendLine(page.TableName);
        builder.Append("Range: ").AppendLine(page.RangeSummary);
        builder.AppendLine();
        builder.AppendLine(string.Join("\t", page.Columns.Select(EscapeExportValue)));

        foreach (var row in page.Rows)
        {
            builder.AppendLine(string.Join("\t", page.Columns.Zip(row.Values, SanitizeForExport)
                .Select(EscapeExportValue)));
        }

        return builder.ToString();
    }

    public static string BuildCombinedSchemaExport(
        IEnumerable<CodexStateInspectionResult> inspections)
    {
        ArgumentNullException.ThrowIfNull(inspections);
        var results = inspections
            .Where(inspection => inspection is not null)
            .OrderBy(inspection => inspection.Database.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("Codex SQLite combined schema inspection");
        builder.AppendLine("Schema only · no table rows or interpreted TajsTokens facts");
        builder.AppendLine($"Databases: {results.Length:N0}");
        builder.AppendLine();

        foreach (var inspection in results)
        {
            builder.AppendLine($"-- SOURCE: {inspection.Database.Path}");
            builder.AppendLine($"-- SOURCE KIND: {inspection.Database.SourceDescription}");
            builder.AppendLine($"-- DISCOVERY KIND: {inspection.Database.DiscoveryKind}");
            builder.AppendLine($"-- OBJECTS: {inspection.SchemaObjects.Count:N0}");
            builder.AppendLine($"-- SCHEMA FINGERPRINT: {inspection.Snapshot.SchemaFingerprint}");
            builder.AppendLine();

            foreach (var table in inspection.Tables.OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("-- OBJECT: ").Append(table.ObjectType).Append(' ').AppendLine(table.Name);
                builder.Append("-- ROW COUNT OBSERVED: ").AppendLine(
                    table.RowCount < 0 ? "unavailable" : table.RowCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine(table.Sql ?? "-- SQL: (not provided)");
                foreach (var column in table.Columns)
                {
                    builder.Append("-- COLUMN ").Append(column.Ordinal.ToString(CultureInfo.InvariantCulture))
                        .Append(": ").Append(column.Name).Append(" ").Append(column.DeclaredType)
                        .Append(" NOT NULL=").Append(column.NotNull)
                        .Append(" PK=").Append(column.IsPrimaryKey)
                        .Append(" HIDDEN=").Append(column.Hidden)
                        .Append(" DEFAULT=").AppendLine(column.DefaultValue ?? "NULL");
                }

                foreach (var index in table.Indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
                {
                    builder.Append("-- INDEX: ").Append(index.Name)
                        .Append(" UNIQUE=").Append(index.IsUnique)
                        .Append(" ORIGIN=").Append(index.Origin)
                        .Append(" PARTIAL=").Append(index.IsPartial)
                        .Append(" COLUMNS=").Append(string.Join(", ", index.Columns))
                        .Append(" SQL=").AppendLine(index.Sql ?? "NULL");
                }

                builder.AppendLine();
            }

            foreach (var schemaObject in inspection.SchemaObjects
                         .Where(schemaObject => schemaObject.ObjectType is not ("table" or "view"))
                         .OrderBy(schemaObject => schemaObject.ObjectType, StringComparer.Ordinal)
                         .ThenBy(schemaObject => schemaObject.Name, StringComparer.Ordinal))
            {
                builder.Append("-- OBJECT: ").Append(schemaObject.ObjectType).Append(' ')
                    .AppendLine(schemaObject.Name);
                if (schemaObject.AssociatedTableName is not null)
                {
                    builder.Append("-- TABLE: ").AppendLine(schemaObject.AssociatedTableName);
                }

                builder.AppendLine(schemaObject.Sql ?? "-- SQL: (not provided)");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    public static bool IsInterestingTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        return new[]
            {
                "thread", "rollout", "token", "model", "goal", "queue", "log", "memory", "stage",
                "automation", "catalog", "summary", "timeline", "project", "artifact"
            }
            .Any(token => tableName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PRAGMA query_only = ON;";
                await queryOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var verify = connection.CreateCommand())
            {
                verify.CommandText = "PRAGMA query_only;";
                var value = await verify.ExecuteScalarAsync(cancellationToken);
                if (Convert.ToInt64(value, CultureInfo.InvariantCulture) != 1)
                {
                    throw new InvalidOperationException("SQLite did not enable query_only mode.");
                }
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<string?> ReadObjectTypeAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return (await command.ExecuteScalarAsync(cancellationToken)) as string;
    }

    private static async Task<IReadOnlyList<CodexStateColumnInfo>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new List<CodexStateColumnInfo>();
        await using var command = connection.CreateCommand();
        // table_xinfo includes generated/hidden columns that table_info omits. Keeping the raw
        // hidden flag makes schema variation visible without assigning a meaning to it.
        command.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(tableName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new CodexStateColumnInfo(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != 0,
                reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 0)
            {
                Hidden = reader.FieldCount > 6
                    ? Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)
                    : 0
            });
        }

        return columns;
    }

    private static async Task<IReadOnlyList<CodexStateIndexInfo>> ReadIndexesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var indexes = new List<CodexStateIndexInfo>();
        var descriptors = new List<(string Name, bool IsUnique, string Origin, bool IsPartial, string? Sql)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(1);
                descriptors.Add((
                    name,
                    Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0,
                    reader.FieldCount > 3 && !reader.IsDBNull(3) ? reader.GetString(3) : string.Empty,
                    reader.FieldCount > 4 && !reader.IsDBNull(4) && Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) != 0,
                    await ReadIndexSqlAsync(connection, name, cancellationToken)));
            }
        }

        foreach (var descriptor in descriptors)
        {
            var columns = await ReadIndexColumnsAsync(connection, descriptor.Name, cancellationToken);
            indexes.Add(new CodexStateIndexInfo(
                descriptor.Name,
                descriptor.IsUnique,
                descriptor.Origin,
                descriptor.IsPartial,
                columns)
            {
                Sql = descriptor.Sql
            });
        }

        return indexes;
    }

    private static async Task<string?> ReadIndexSqlAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadIndexColumnsAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        var columns = new List<(int Sequence, string Name)>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(2))
            {
                columns.Add((Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(2)));
            }
        }

        return columns.OrderBy(column => column.Sequence).Select(column => column.Name).ToArray();
    }

    private static async Task<long> TryReadRowCountAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)};";
        try
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return -1;
        }
    }

    private static async Task<RowFingerprintResult> ComputeRowFingerprintAsync(
        SqliteConnection connection,
        CodexStateTableInfo table,
        bool allowDeepRowFingerprint,
        CodexStateDatabaseCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!allowDeepRowFingerprint)
        {
            var note = candidate.SizeBytes > MaxFingerprintDatabaseBytes
                ? $"count-only: database exceeds {MaxFingerprintDatabaseBytes / (1024 * 1024):N0} MiB"
                : "count-only inspection requested";
            return RowFingerprintResult.CountOnly(FingerprintBoundedRowObservation(table), note);
        }

        if (table.RowCount > MaxFingerprintRows)
        {
            return RowFingerprintResult.CountOnly(
                FingerprintBoundedRowObservation(table),
                $"count-only: table exceeds {MaxFingerprintRows:N0} rows");
        }

        var rowHashes = new List<string>();
        var observations = new List<CodexStateRowObservation>();
        var bounded = false;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToArray();
            var columnPositions = columns
                .Select((name, position) => (name, position))
                .ToDictionary(item => item.name, item => item.position, StringComparer.OrdinalIgnoreCase);
            var primaryKeyColumns = table.Columns
                .Where(column => column.IsPrimaryKey)
                .OrderBy(column => column.Ordinal)
                .Select(column => column.Name)
                .ToArray();

            while (await reader.ReadAsync(cancellationToken))
            {
                if (observations.Count >= MaxFingerprintRows)
                {
                    // Retain at most the bounded sample. The extra row proves that the result is
                    // incomplete without requiring an unbounded read of a live source.
                    bounded = true;
                    break;
                }

                var values = new object?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                var rowHash = ComputeRowHash(columns, values);
                var (identityKind, rowIdentity) = BuildRowIdentity(
                    primaryKeyColumns,
                    columnPositions,
                    values,
                    rowHash);
                observations.Add(new CodexStateRowObservation(
                    candidate.Path,
                    candidate.SourceDescription,
                    table.Name,
                    identityKind,
                    rowIdentity,
                    rowHash,
                    columns,
                    values));
                rowHashes.Add(rowHash);
            }
        }
        catch (SqliteException exception)
        {
            return RowFingerprintResult.Unavailable(
                FingerprintBoundedRowObservation(table),
                $"row scan unavailable: {exception.Message.ReplaceLineEndings(" ").Trim()}");
        }

        if (rowHashes.Count == 0 && table.RowCount < 0)
        {
            rowHashes.Add("<unavailable>");
        }

        rowHashes.Sort(StringComparer.Ordinal);
        var material = $"rows={table.RowCount}\nbounded={bounded}\n{string.Join('\n', rowHashes)}";
        return new RowFingerprintResult(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
            observations,
            Complete: !bounded,
            Bounded: bounded,
            Note: bounded
                ? $"bounded row observation at {MaxFingerprintRows:N0} rows"
                : $"complete row observation ({observations.Count:N0} rows)",
            Mode: "full");
    }

    private static string ComputeRowHash(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?> values)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < columns.Count; index++)
        {
            AppendSchemaText(builder, "column", columns[index]);
            builder.Append("value:");
            AppendCanonical(builder, index < values.Count ? values[index] : null);
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static (string IdentityKind, string RowIdentity) BuildRowIdentity(
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyDictionary<string, int> columnPositions,
        IReadOnlyList<object?> values,
        string rowHash)
    {
        var positions = primaryKeyColumns
            .Select(name => columnPositions.TryGetValue(name, out var position) ? position : -1)
            .ToArray();
        if (positions.Length == primaryKeyColumns.Count &&
            positions.All(position => position >= 0 && position < values.Count && values[position] is not null))
        {
            var builder = new StringBuilder("pk:");
            for (var index = 0; index < primaryKeyColumns.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append('|');
                }

                builder.Append(primaryKeyColumns[index]).Append('=');
                AppendCanonical(builder, values[positions[index]]);
            }

            return ("primary-key", builder.ToString());
        }

        return ("row-hash", $"hash:{rowHash}");
    }

    private static string FingerprintBoundedRowObservation(CodexStateTableInfo table)
    {
        var material = $"rows={table.RowCount}\nbounded=true";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string FingerprintSchema(CodexStateTableInfo table)
    {
        var builder = new StringBuilder();
        AppendSchemaText(builder, "type", table.ObjectType);
        AppendSchemaText(builder, "name", table.Name);
        AppendSchemaText(builder, "sql", table.Sql);
        foreach (var column in table.Columns)
        {
            builder.Append("column:").Append(column.Ordinal).Append('|');
            AppendSchemaText(builder, "name", column.Name);
            AppendSchemaText(builder, "declared-type", column.DeclaredType);
            AppendSchemaText(builder, "not-null", column.NotNull.ToString());
            AppendSchemaText(builder, "default", column.DefaultValue);
            AppendSchemaText(builder, "primary-key", column.IsPrimaryKey.ToString());
            AppendSchemaText(builder, "hidden", column.Hidden.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var index in table.Indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            AppendSchemaText(builder, "index.name", index.Name);
            AppendSchemaText(builder, "index.unique", index.IsUnique.ToString());
            AppendSchemaText(builder, "index.origin", index.Origin);
            AppendSchemaText(builder, "index.partial", index.IsPartial.ToString());
            AppendSchemaText(builder, "index.columns", string.Join('\u001F', index.Columns));
            AppendSchemaText(builder, "index.sql", index.Sql);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendSchemaText(StringBuilder builder, string field, string? value)
    {
        builder.Append(field).Append(':');
        if (value is null)
        {
            builder.Append("<null>");
        }
        else
        {
            builder.Append(value.Length).Append(':').Append(value);
        }

        builder.Append('\n');
    }

    private static void AppendCanonical(StringBuilder builder, object? value)
    {
        string type;
        string representation;
        switch (value)
        {
            case null:
                type = "null";
                representation = string.Empty;
                break;
            case byte[] bytes:
                type = "blob";
                representation = Convert.ToHexString(bytes);
                break;
            case double number:
                type = "double";
                representation = number.ToString("R", CultureInfo.InvariantCulture);
                break;
            case float number:
                type = "single";
                representation = number.ToString("R", CultureInfo.InvariantCulture);
                break;
            case IFormattable formattable:
                type = value.GetType().FullName ?? value.GetType().Name;
                representation = formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
                break;
            default:
                type = value.GetType().FullName ?? value.GetType().Name;
                representation = value.ToString() ?? string.Empty;
                break;
        }

        // Length-prefix both pieces so delimiters/newlines in raw source values cannot create
        // an ambiguous row representation.
        builder.Append(type.Length).Append(':').Append(type)
            .Append('|').Append(representation.Length).Append(':').Append(representation);
    }

    private static object SanitizeForExport(string columnName, object? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        if (IsContentBearingColumn(columnName))
        {
            return "[redacted]";
        }

        if (value is byte[] bytes)
        {
            return $"<blob {bytes.Length:N0} bytes>";
        }

        if (value is string text && IsPathColumn(columnName))
        {
            return $"<path:{Path.GetFileName(text)}>";
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool IsContentBearingColumn(string columnName) =>
        new[] { "prompt", "content", "message", "body", "text", "reason", "summary", "description", "objective", "output", "arguments", "payload", "json", "metadata", "details", "stack", "trace" }
            .Any(token => columnName.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool IsPathColumn(string columnName) =>
        columnName.Contains("path", StringComparison.OrdinalIgnoreCase) ||
        columnName.Contains("file", StringComparison.OrdinalIgnoreCase);

    private static string EscapeExportValue(object value) =>
        (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        .Replace("\t", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    public static string FormatRawValue(object? value)
    {
        var formatted = value switch
        {
            null => "NULL",
            byte[] bytes => $"<blob {bytes.Length:N0} bytes> {Convert.ToHexString(bytes)}",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        const int maxDisplayCharacters = 16_384;
        return formatted.Length <= maxDisplayCharacters
            ? formatted
            : formatted[..maxDisplayCharacters] + "… [display truncated]";
    }

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsSourceInspectionFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or InvalidDataException or
            NotSupportedException or InvalidCastException or FormatException or OverflowException or
            ObjectDisposedException;

    private static string SummarizeSourceFailure(Exception exception) =>
        exception.Message.ReplaceLineEndings(" ").Trim();

    private CodexStateDatabaseCandidate CreateCandidateForPath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected Codex state database was not found.", fullPath);
        }

        return DiscoverCandidates().FirstOrDefault(candidate =>
                   string.Equals(candidate.Path, fullPath, StringComparison.OrdinalIgnoreCase))
               ?? TryCreateCandidate(fullPath, "Explicit source", "explicit")
               ?? new CodexStateDatabaseCandidate(
                    fullPath,
                    Path.GetFileName(fullPath),
                    null,
                    File.GetLastWriteTimeUtc(fullPath),
                    new FileInfo(fullPath).Length);
    }

    private static IEnumerable<CodexStateDatabaseCandidate> EnumerateRoot(
        string root,
        string pattern,
        string sourceDescription,
        string discoveryKind)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var candidate = TryCreateCandidate(file, sourceDescription, discoveryKind);
            if (candidate is not null)
            {
                yield return candidate;
            }
        }
    }

    private static void AddRoot(
        ICollection<(string Path, string Pattern, string Description, string Kind)> roots,
        string path,
        string pattern,
        string description,
        string kind)
    {
        roots.Add((path, pattern, description, kind));
    }

    private static CodexStateDatabaseCandidate? TryCreateCandidate(
        string path,
        string sourceDescription,
        string discoveryKind)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            var generation = TryParseGeneration(fileName);
            var info = new FileInfo(path);
            return new CodexStateDatabaseCandidate(
                Path.GetFullPath(path),
                fileName,
                generation,
                info.LastWriteTimeUtc,
                info.Length)
            {
                SourceDescription = sourceDescription,
                DiscoveryKind = discoveryKind
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? TryParseGeneration(string fileName)
    {
        var prefix = fileName.StartsWith("state_", StringComparison.OrdinalIgnoreCase)
            ? "state_"
            : fileName.StartsWith("thread_history_", StringComparison.OrdinalIgnoreCase)
                ? "thread_history_"
                : null;
        if (prefix is null)
        {
            return null;
        }

        var digits = fileName[prefix.Length..].TakeWhile(char.IsDigit).ToArray();
        return digits.Length == 0 ||
               !int.TryParse(new string(digits), NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            ? null
            : generation;
    }

    private static string ResolveCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configured;
    }

    private static string? ResolveSnapshotDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("TAJSTOKENS_CODEX_SNAPSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var privateDirectory = Path.Combine(directory.FullName, "private");
            if (Directory.Exists(privateDirectory))
            {
                return privateDirectory;
            }
        }

        var currentPrivate = Path.Combine(Environment.CurrentDirectory, "private");
        return Directory.Exists(currentPrivate) ? currentPrivate : null;
    }
}
