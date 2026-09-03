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

    /// <summary>
    /// Stable acquisition-location label. This is discovery provenance only; it is not a
    /// provider, database, or domain classification.
    /// </summary>
    public string DiscoveryKind { get; init; } = "explicit";

    public string DisplayName => $"{FileName} · {SourceDescription} · {SizeBytes:N0} bytes · {LastWriteTimeUtc: u}";
}

public sealed record CodexStateColumnInfo(
    int Ordinal,
    string Name,
    string DeclaredType,
    bool NotNull,
    string? DefaultValue,
    bool IsPrimaryKey)
{
    /// <summary>
    /// Raw value from PRAGMA table_xinfo hidden column. Zero means ordinary; other values are
    /// retained as source metadata (for example generated or virtual-table columns).
    /// </summary>
    public int Hidden { get; init; }

    public bool IsHidden => Hidden != 0;
}

public sealed record CodexStateSchemaObjectInfo(
    string Name,
    string ObjectType,
    string? Sql)
{
    public string? AssociatedTableName { get; init; }
}

public sealed record CodexStateIndexInfo(
    string Name,
    bool IsUnique,
    string Origin,
    bool IsPartial,
    IReadOnlyList<string> Columns)
{
    /// <summary>
    /// The raw sqlite_master definition, including expression/collation details that PRAGMA
    /// index_info does not expose. It remains source text, not an interpreted capability.
    /// </summary>
    public string? Sql { get; init; }
}

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
    string RowFingerprint)
{
    public bool RowComparisonComplete { get; init; }

    public bool RowComparisonBounded { get; init; }

    public string RowComparisonNote { get; init; } = string.Empty;

    public int RowObservationCount { get; init; }

    /// <summary>
    /// Raw inspection algorithm used for the row fingerprint (for example full, count-only, or
    /// unavailable). Fingerprints from different modes are not treated as content-comparable.
    /// </summary>
    public string RowFingerprintMode { get; init; } = string.Empty;

    public IReadOnlyList<CodexStateColumnInfo> Columns { get; init; } = Array.Empty<CodexStateColumnInfo>();

    public IReadOnlyList<CodexStateIndexInfo> Indexes { get; init; } = Array.Empty<CodexStateIndexInfo>();

    public string RowComparisonStatus => RowComparisonComplete
        ? $"complete ({RowObservationCount:N0} rows observed)"
        : RowComparisonBounded
            ? $"bounded/count-only ({RowObservationCount:N0} rows observed)"
            : "unavailable";
}

/// <summary>
/// A bounded raw row observation retained in an in-memory inspection snapshot. The identity is
/// an inspection aid (a primary-key value when one is exposed, otherwise a content hash), not a
/// claim that the source has stable domain identity.
/// </summary>
public sealed record CodexStateRowObservation(
    string DatabasePath,
    string SourceDescription,
    string TableName,
    string IdentityKind,
    string RowIdentity,
    string RowHash,
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?> Values)
{
    public DateTimeOffset CapturedAtUtc { get; init; }

    public bool UsesSourcePrimaryKey => string.Equals(IdentityKind, "primary-key", StringComparison.Ordinal);

    public string SourceSummary =>
        $"{Path.GetFileName(DatabasePath)} · {TableName} · {IdentityKind} · {RowIdentity}";

    public string DisplayText => string.Join(
        "  |  ",
        Values.Select(CodexStateDbExplorerService.FormatRawValue));
}

public sealed record CodexStateInspectionSnapshot(
    string DatabasePath,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<CodexStateTableSnapshot> Tables)
{
    /// <summary>
    /// Every object observed in sqlite_master, including indexes and triggers that are not
    /// represented by <see cref="CodexStateTableSnapshot"/> rows.
    /// </summary>
    public IReadOnlyList<CodexStateSchemaObjectInfo> SchemaObjects { get; init; } =
        Array.Empty<CodexStateSchemaObjectInfo>();

    /// <summary>
    /// A deterministic fingerprint of the discovered sqlite_master objects and their declared
    /// columns/indexes. It intentionally excludes row content.
    /// </summary>
    public string SchemaFingerprint { get; init; } = string.Empty;

    /// <summary>
    /// Rows retained only for bounded, in-memory comparison evidence. These values are never
    /// written to the TajsTokens database by the explorer.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CodexStateRowObservation>> RowObservations { get; init; } =
        new Dictionary<string, IReadOnlyList<CodexStateRowObservation>>(StringComparer.OrdinalIgnoreCase);

    public string SourceDescription { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, CodexStateTableSnapshot> ByName =>
        Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
}

public sealed record CodexStateInspectionResult(
    CodexStateDatabaseCandidate Database,
    IReadOnlyList<CodexStateTableInfo> Tables,
    CodexStateInspectionSnapshot Snapshot)
{
    public string SchemaFingerprint => Snapshot.SchemaFingerprint;

    public IReadOnlyList<CodexStateSchemaObjectInfo> SchemaObjects => Snapshot.SchemaObjects;

    public IReadOnlyList<CodexStateTableInfo> FocusedTables => Tables
        .Where(table => CodexStateDbExplorerService.IsInterestingTable(table.Name))
        .ToArray();
}

/// <summary>
/// Result of inspecting all currently discovered source instances. A failed source is retained
/// as an unavailable item so one locked, invalid, or disappearing file cannot hide other sources.
/// </summary>
public sealed record CodexStateSourceInspection(
    CodexStateDatabaseCandidate Database,
    CodexStateInspectionResult? Inspection,
    string? Error)
{
    public bool IsAvailable => Inspection is not null;

    public string Status => IsAvailable ? "available" : "unavailable";
}

public sealed record CodexStateMultiInspectionResult(
    IReadOnlyList<CodexStateSourceInspection> Sources)
{
    public IReadOnlyList<CodexStateSourceInspection> AvailableSources =>
        Sources.Where(source => source.IsAvailable).ToArray();

    public IReadOnlyList<CodexStateSourceInspection> UnavailableSources =>
        Sources.Where(source => !source.IsAvailable).ToArray();
}

public sealed record CodexStateTableDiff(
    string Name,
    string ChangeKind,
    long? PreviousRowCount,
    long? CurrentRowCount,
    bool SchemaChanged,
    bool RowsChanged)
{
    public string BaselineDatabasePath { get; init; } = string.Empty;

    public string CurrentDatabasePath { get; init; } = string.Empty;

    public string BaselineSourceDescription { get; init; } = string.Empty;

    public string CurrentSourceDescription { get; init; } = string.Empty;

    public DateTimeOffset BaselineCapturedAtUtc { get; init; }

    public DateTimeOffset CurrentCapturedAtUtc { get; init; }

    public bool RowComparisonComplete { get; init; }

    public bool RowComparisonBounded { get; init; }

    public string RowComparisonNote { get; init; } = string.Empty;

    public IReadOnlyList<CodexStateRowDiff> RowChanges { get; init; } = Array.Empty<CodexStateRowDiff>();

    public IReadOnlyList<string> AddedColumns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RemovedColumns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ChangedColumns { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RemovedIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ChangedIndexes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<CodexStateRowDiff> RowChangeCandidates => RowChanges;

    public bool HasRowLevelChanges => RowChanges.Count > 0;

    public string Summary => ChangeKind switch
    {
        "added" => $"Added · {CurrentRowCount:N0} rows",
        "removed" => $"Removed · {PreviousRowCount:N0} rows",
        "incomplete" => $"Rows not fully compared · {PreviousRowCount:N0} → {CurrentRowCount:N0}",
        _ when SchemaChanged && RowsChanged => $"Schema and rows changed · {PreviousRowCount:N0} → {CurrentRowCount:N0}",
        _ when SchemaChanged => "Schema changed",
        _ when RowsChanged => $"Rows changed · {PreviousRowCount:N0} → {CurrentRowCount:N0}",
        _ => "No row differences observed"
    };

    public string EvidenceSummary
    {
        get
        {
            var summary = string.IsNullOrWhiteSpace(RowComparisonNote)
                ? (RowComparisonComplete ? "complete row observation" : "row comparison unavailable")
                : RowComparisonNote;
            return RowChanges.Count == 0
                ? AppendSchemaSummary(summary)
                : AppendSchemaSummary($"{summary}; {RowChanges.Count:N0} row change candidate(s)");
        }
    }

    private string AppendSchemaSummary(string summary)
    {
        var details = new List<string>();
        AppendNames(details, "+columns", AddedColumns);
        AppendNames(details, "-columns", RemovedColumns);
        AppendNames(details, "~columns", ChangedColumns);
        AppendNames(details, "+indexes", AddedIndexes);
        AppendNames(details, "-indexes", RemovedIndexes);
        AppendNames(details, "~indexes", ChangedIndexes);
        return details.Count == 0
            ? summary
            : $"{summary}; schema details: {string.Join("; ", details)}";
    }

    private static void AppendNames(ICollection<string> details, string label, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        const int maxNames = 12;
        var displayed = names.Take(maxNames).ToArray();
        var suffix = names.Count > displayed.Length ? $", … +{names.Count - displayed.Length:N0} more" : string.Empty;
        details.Add($"{label} {string.Join(", ", displayed)}{suffix}");
    }
}

/// <summary>
/// A raw row-level difference candidate. It is tied to both inspected source instances and is
/// intentionally not a domain event or relationship.
/// </summary>
public sealed record CodexStateRowDiff(
    string TableName,
    string ChangeKind,
    string RowIdentity,
    string? PreviousRowHash,
    string? CurrentRowHash,
    IReadOnlyList<string> PreviousColumns,
    IReadOnlyList<object?> PreviousValues,
    IReadOnlyList<string> CurrentColumns,
    IReadOnlyList<object?> CurrentValues)
{
    public string BaselineDatabasePath { get; init; } = string.Empty;

    public string CurrentDatabasePath { get; init; } = string.Empty;

    public string BaselineSourceDescription { get; init; } = string.Empty;

    public string CurrentSourceDescription { get; init; } = string.Empty;

    public DateTimeOffset BaselineCapturedAtUtc { get; init; }

    public DateTimeOffset CurrentCapturedAtUtc { get; init; }

    public string IdentityKind { get; init; } = "unknown";

    public string Summary => $"{ChangeKind} · {IdentityKind} · {RowIdentity}";
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

    public string BaselineSourceDescription { get; init; } = string.Empty;

    public string CurrentSourceDescription { get; init; } = string.Empty;

    public bool SchemaChanged => !string.Equals(
        BaselineSchemaFingerprint,
        CurrentSchemaFingerprint,
        StringComparison.Ordinal);

    /// <summary>
    /// True when the comparison has any schema/row result entry, including an explicit incomplete
    /// coverage entry. Use <see cref="HasObservedChanges"/> when only confirmed differences are
    /// desired.
    /// </summary>
    public bool HasChanges => SchemaChanged || Tables.Count > 0;

    public bool HasResults => HasChanges;

    public bool HasIncompleteComparisons => Tables.Any(table => !table.RowComparisonComplete);

    public bool HasObservedChanges => SchemaChanged || Tables.Any(table =>
        table.ChangeKind is "added" or "removed" or "changed");
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
    public CodexStateDatabaseCandidate? SourceCandidate { get; init; }

    public DateTimeOffset InspectionCapturedAtUtc { get; init; }

    public string DiscoveryKind => SourceCandidate?.DiscoveryKind ?? string.Empty;

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
