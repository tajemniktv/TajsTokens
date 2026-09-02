namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// A discovered Codex state database. This is source metadata only; it is not a TajsTokens
/// workspace, session, or provider identity.
/// </summary>
public sealed record CodexStateDatabaseCandidate(
    string Path,
    string FileName,
    int? Generation,
    DateTimeOffset LastWriteTimeUtc,
    long SizeBytes)
{
    public string SourceDescription { get; init; } = "Explicit source";

    public string DisplayName => $"{FileName} · {SourceDescription} · {SizeBytes:N0} bytes · {LastWriteTimeUtc: u}";
}

public sealed record CodexStateColumnInfo(
    int Ordinal,
    string Name,
    string DeclaredType,
    bool NotNull,
    string? DefaultValue,
    bool IsPrimaryKey);

public sealed record CodexStateIndexInfo(
    string Name,
    bool IsUnique,
    string Origin,
    bool IsPartial,
    IReadOnlyList<string> Columns);

public sealed record CodexStateTableInfo(
    string Name,
    string ObjectType,
    string? Sql,
    long RowCount,
    IReadOnlyList<CodexStateColumnInfo> Columns,
    IReadOnlyList<CodexStateIndexInfo> Indexes)
{
    public string SchemaFingerprint { get; init; } = string.Empty;

    public string RowCountSummary => RowCount < 0 ? "Row count unavailable" : $"{RowCount:N0} rows";

    public string SchemaFingerprintSummary => string.IsNullOrWhiteSpace(SchemaFingerprint)
        ? "Schema fingerprint unavailable"
        : $"Schema {SchemaFingerprint[..Math.Min(12, SchemaFingerprint.Length)]}…";

    public string ColumnSummary => Columns.Count == 0
        ? "(no declared columns)"
        : string.Join(", ", Columns.Select(column => column.Name));

    public string IndexSummary => Indexes.Count == 0
        ? "No indexes"
        : string.Join(", ", Indexes.Select(index => index.Name));
}

public sealed record CodexStateRawRow(
    IReadOnlyList<object?> Values,
    string DisplayText);

public sealed record CodexStateRawPage(
    string DatabasePath,
    string TableName,
    int PageIndex,
    int PageSize,
    long TotalRows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<CodexStateRawRow> Rows)
{
    public int PageCount => TotalRows <= 0
        ? 0
        : checked((int)Math.Min(int.MaxValue, (TotalRows + PageSize - 1) / PageSize));

    public string RangeSummary => TotalRows < 0
        ? "Rows loaded (total unavailable)"
        : TotalRows == 0
        ? "0 rows"
        : $"Rows {PageIndex * PageSize + 1:N0}–{Math.Min(TotalRows, (PageIndex + 1L) * PageSize):N0} of {TotalRows:N0}";
}

public sealed record CodexStateTableSnapshot(
    string Name,
    string ObjectType,
    long RowCount,
    string SchemaFingerprint,
    string RowFingerprint);

public sealed record CodexStateInspectionSnapshot(
    string DatabasePath,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<CodexStateTableSnapshot> Tables)
{
    /// <summary>
    /// A deterministic fingerprint of the discovered sqlite_master objects and their declared
    /// columns/indexes. It intentionally excludes row content.
    /// </summary>
    public string SchemaFingerprint { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, CodexStateTableSnapshot> ByName =>
        Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
}

public sealed record CodexStateInspectionResult(
    CodexStateDatabaseCandidate Database,
    IReadOnlyList<CodexStateTableInfo> Tables,
    CodexStateInspectionSnapshot Snapshot)
{
    public string SchemaFingerprint => Snapshot.SchemaFingerprint;

    public IReadOnlyList<CodexStateTableInfo> FocusedTables => Tables
        .Where(table => CodexStateDbExplorerService.IsInterestingTable(table.Name))
        .ToArray();
}

public sealed record CodexStateTableDiff(
    string Name,
    string ChangeKind,
    long? PreviousRowCount,
    long? CurrentRowCount,
    bool SchemaChanged,
    bool RowsChanged)
{
    public string Summary => ChangeKind switch
    {
        "added" => $"Added · {CurrentRowCount:N0} rows",
        "removed" => $"Removed · {PreviousRowCount:N0} rows",
        _ when SchemaChanged && RowsChanged => $"Schema and rows changed · {PreviousRowCount:N0} → {CurrentRowCount:N0}",
        _ when SchemaChanged => "Schema changed",
        _ => $"Rows changed · {PreviousRowCount:N0} → {CurrentRowCount:N0}"
    };
}

public sealed record CodexStateInspectionDiff(
    DateTimeOffset BaselineCapturedAtUtc,
    DateTimeOffset ComparedAtUtc,
    IReadOnlyList<CodexStateTableDiff> Tables)
{
    public string BaselineDatabasePath { get; init; } = string.Empty;

    public string CurrentDatabasePath { get; init; } = string.Empty;

    public string BaselineSchemaFingerprint { get; init; } = string.Empty;

    public string CurrentSchemaFingerprint { get; init; } = string.Empty;

    public bool SchemaChanged => !string.Equals(
        BaselineSchemaFingerprint,
        CurrentSchemaFingerprint,
        StringComparison.Ordinal);

    public bool HasChanges => SchemaChanged || Tables.Count > 0;
}

/// <summary>
/// A raw match for an exact source-native column/value lookup. This is deliberately not a
/// relationship or domain object: each match remains tied to its originating database object.
/// </summary>
public sealed record CodexStateKeyTraceMatch(
    string DatabasePath,
    string SourceDescription,
    string TableName,
    string ColumnName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?> Values)
{
    public string SourceTableSummary =>
        $"{Path.GetFileName(DatabasePath)} · {TableName} · {ColumnName}";

    public string ColumnSummary => string.Join("  |  ", Columns);

    public string DisplayText => string.Join(
        "  |  ",
        Values.Select(CodexStateDbExplorerService.FormatRawValue));
}

public sealed record CodexStateKeyTraceResult(
    string ColumnName,
    string Value,
    IReadOnlyList<CodexStateKeyTraceMatch> Matches)
{
    public IReadOnlyList<string> UnavailableSources { get; init; } = Array.Empty<string>();

    public bool MayBeTruncated { get; init; }

    public bool HasMatches => Matches.Count > 0;

    public string Summary
    {
        get
        {
            var summary = HasMatches
                ? $"{Matches.Count:N0} exact source matches for {ColumnName}={Value}"
                : $"No exact source matches for {ColumnName}={Value}";
            if (MayBeTruncated)
            {
                summary += "; result limit reached (matches may be omitted)";
            }

            return UnavailableSources.Count == 0
                ? summary
                : $"{summary}; {UnavailableSources.Count:N0} source/object lookup(s) unavailable";
        }
    }
}
