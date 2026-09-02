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
    public string RowCountSummary => RowCount < 0 ? "Row count unavailable" : $"{RowCount:N0} rows";

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
    public IReadOnlyDictionary<string, CodexStateTableSnapshot> ByName =>
        Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
}

public sealed record CodexStateInspectionResult(
    CodexStateDatabaseCandidate Database,
    IReadOnlyList<CodexStateTableInfo> Tables,
    CodexStateInspectionSnapshot Snapshot)
{
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
    public bool HasChanges => Tables.Count > 0;
}
