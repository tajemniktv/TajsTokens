using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Services;

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
    private const int MaxFingerprintRows = 100_000;
    private const long MaxFingerprintDatabaseBytes = 64L * 1024 * 1024;

    private readonly string _codexHome;
    private readonly string? _snapshotDirectory;

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
            var roots = new List<(string Path, string Pattern, string Description)>();
            AddRoot(roots, _codexHome, "state_*.sqlite", "Codex home");
            AddRoot(roots, Path.Combine(_codexHome, "sqlite"), "*.db", "Codex sqlite folder");
            AddRoot(roots, Path.Combine(_codexHome, "sqlite"), "*.sqlite", "Codex sqlite folder");
            if (_snapshotDirectory is not null)
            {
                AddRoot(roots, _snapshotDirectory, "*.sqlite", "Snapshot folder");
                AddRoot(roots, _snapshotDirectory, "*.db", "Snapshot folder");
            }

            return roots
                .SelectMany(root => EnumerateRoot(root.Path, root.Pattern, root.Description))
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
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = """
            SELECT type, name, sql
            FROM sqlite_master
            WHERE type IN ('table', 'view')
            ORDER BY name COLLATE BINARY;
            """;

        await using var schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
        while (await schemaReader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectType = schemaReader.GetString(0);
            var tableName = schemaReader.GetString(1);
            var sql = schemaReader.IsDBNull(2) ? null : schemaReader.GetString(2);
            var columns = await ReadColumnsAsync(connection, tableName, cancellationToken);
            var indexes = await ReadIndexesAsync(connection, tableName, cancellationToken);
            var rowCount = await TryReadRowCountAsync(connection, tableName, cancellationToken);

            var table = new CodexStateTableInfo(tableName, objectType, sql, rowCount, columns, indexes);
            tables.Add(table with { SchemaFingerprint = FingerprintSchema(table) });
        }

        var snapshots = new List<CodexStateTableSnapshot>(tables.Count);
        var allowDeepRowFingerprint = candidate.SizeBytes <= MaxFingerprintDatabaseBytes;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schemaFingerprint = table.SchemaFingerprint;
            var rowFingerprint = includeRowFingerprints
                ? await ComputeRowFingerprintAsync(
                    connection,
                    table,
                    allowDeepRowFingerprint,
                    cancellationToken)
                : FingerprintBoundedRowObservation(table);
            snapshots.Add(new CodexStateTableSnapshot(
                table.Name,
                table.ObjectType,
                table.RowCount,
                schemaFingerprint,
                rowFingerprint));
        }

        var snapshot = new CodexStateInspectionSnapshot(
            candidate.Path,
            DateTimeOffset.UtcNow,
            snapshots)
        {
            SchemaFingerprint = ComputeSchemaFingerprint(snapshots)
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
        var paths = databasePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var databasePath in paths)
        {
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
                    if (matches.Count >= maxMatches)
                    {
                        break;
                    }

                    var sourceColumn = table.Columns.First(column =>
                        string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)} WHERE {QuoteIdentifier(sourceColumn.Name)} = $value LIMIT $limit;";
                    command.Parameters.AddWithValue("$value", value);
                    command.Parameters.AddWithValue("$limit", maxMatches - matches.Count);

                    try
                    {
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        var columns = Enumerable.Range(0, reader.FieldCount)
                            .Select(reader.GetName)
                            .ToArray();
                        while (await reader.ReadAsync(cancellationToken))
                        {
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
                                values));
                            if (matches.Count >= maxMatches)
                            {
                                break;
                            }
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
            MayBeTruncated = matches.Count >= maxMatches
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
                diffs.Add(new CodexStateTableDiff(name, "added", null, next!.RowCount, true, true));
                continue;
            }

            if (!hasAfter)
            {
                diffs.Add(new CodexStateTableDiff(name, "removed", previous!.RowCount, null, true, true));
                continue;
            }

            var schemaChanged = !string.Equals(previous!.SchemaFingerprint, next!.SchemaFingerprint, StringComparison.Ordinal);
            var rowsChanged = !string.Equals(previous.RowFingerprint, next.RowFingerprint, StringComparison.Ordinal);
            if (schemaChanged || rowsChanged)
            {
                diffs.Add(new CodexStateTableDiff(
                    name,
                    "changed",
                    previous.RowCount,
                    next.RowCount,
                    schemaChanged,
                    rowsChanged));
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
            CurrentSchemaFingerprint = current.SchemaFingerprint
        };
    }

    /// <summary>
    /// Computes a database-level schema fingerprint from sorted source-object fingerprints. Row
    /// observations are deliberately excluded so the value changes only when schema metadata does.
    /// </summary>
    public static string ComputeSchemaFingerprint(
        IEnumerable<CodexStateTableSnapshot> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var material = string.Join(
            "\n",
            tables
                .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(table => table.ObjectType, StringComparer.Ordinal)
                .Select(table => $"{table.ObjectType}|{table.Name}|{table.SchemaFingerprint}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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
            builder.AppendLine($"-- OBJECTS: {inspection.Tables.Count:N0}");
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
                        .Append(" DEFAULT=").AppendLine(column.DefaultValue ?? "NULL");
                }

                foreach (var index in table.Indexes)
                {
                    builder.Append("-- INDEX: ").Append(index.Name)
                        .Append(" UNIQUE=").Append(index.IsUnique)
                        .Append(" ORIGIN=").Append(index.Origin)
                        .Append(" PARTIAL=").Append(index.IsPartial)
                        .Append(" COLUMNS=").AppendLine(string.Join(", ", index.Columns));
                }

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
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new CodexStateColumnInfo(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != 0,
                reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 0));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<CodexStateIndexInfo>> ReadIndexesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var indexes = new List<CodexStateIndexInfo>();
        var descriptors = new List<(string Name, bool IsUnique, string Origin, bool IsPartial)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                descriptors.Add((
                    reader.GetString(1),
                    Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture) != 0,
                    reader.FieldCount > 3 && !reader.IsDBNull(3) ? reader.GetString(3) : string.Empty,
                    reader.FieldCount > 4 && !reader.IsDBNull(4) && Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) != 0));
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
                columns));
        }

        return indexes;
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

    private static async Task<string> ComputeRowFingerprintAsync(
        SqliteConnection connection,
        CodexStateTableInfo table,
        bool allowDeepRowFingerprint,
        CancellationToken cancellationToken)
    {
        if (!allowDeepRowFingerprint || table.RowCount > MaxFingerprintRows)
        {
            return FingerprintBoundedRowObservation(table);
        }

        var rowHashes = new List<string>();
        var bounded = false;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {QuoteIdentifier(table.Name)};";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new StringBuilder();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    if (index > 0)
                    {
                        row.Append('\u001F');
                    }

                    AppendCanonical(row, reader.IsDBNull(index) ? null : reader.GetValue(index));
                }

                rowHashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.ToString()))));
                if (rowHashes.Count >= MaxFingerprintRows)
                {
                    // Keep refresh responsive for an unexpectedly large private table while retaining
                    // an explicit marker that the fingerprint is a bounded observation.
                    bounded = true;
                    break;
                }
            }
        }
        catch (SqliteException)
        {
            rowHashes.Clear();
            rowHashes.Add("<unavailable>");
        }

        if (rowHashes.Count == 0 && table.RowCount < 0)
        {
            rowHashes.Add("<unavailable>");
        }

        rowHashes.Sort(StringComparer.Ordinal);
        var material = $"rows={table.RowCount}\nbounded={bounded}\n{string.Join('\n', rowHashes)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string FingerprintBoundedRowObservation(CodexStateTableInfo table)
    {
        var material = $"rows={table.RowCount}\nbounded=true";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string FingerprintSchema(CodexStateTableInfo table)
    {
        var builder = new StringBuilder()
            .Append(table.ObjectType).Append('\n').Append(table.Name).Append('\n').Append(table.Sql).Append('\n');
        foreach (var column in table.Columns)
        {
            builder.Append(column.Ordinal).Append('|').Append(column.Name).Append('|')
                .Append(column.DeclaredType).Append('|').Append(column.NotNull).Append('|')
                .Append(column.DefaultValue).Append('|').Append(column.IsPrimaryKey).Append('\n');
        }

        foreach (var index in table.Indexes.OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(index.Name).Append('|').Append(index.IsUnique).Append('|')
                .Append(index.Origin).Append('|').Append(index.IsPartial).Append('|')
                .Append(string.Join(',', index.Columns)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("<null>");
                break;
            case byte[] bytes:
                builder.Append("blob:").Append(Convert.ToHexString(bytes));
                break;
            case double number:
                builder.Append("double:").Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float number:
                builder.Append("single:").Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case IFormattable formattable:
                builder.Append(value.GetType().FullName).Append(':')
                    .Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                builder.Append(value.GetType().FullName).Append(':').Append(value);
                break;
        }
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
               ?? TryCreateCandidate(fullPath, "Explicit source")
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
        string sourceDescription)
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
            var candidate = TryCreateCandidate(file, sourceDescription);
            if (candidate is not null)
            {
                yield return candidate;
            }
        }
    }

    private static void AddRoot(
        ICollection<(string Path, string Pattern, string Description)> roots,
        string path,
        string pattern,
        string description)
    {
        roots.Add((path, pattern, description));
    }

    private static CodexStateDatabaseCandidate? TryCreateCandidate(
        string path,
        string sourceDescription)
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
                SourceDescription = sourceDescription
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? TryParseGeneration(string fileName)
    {
        const string prefix = "state_";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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
