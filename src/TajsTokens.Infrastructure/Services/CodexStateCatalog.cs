using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Services;

internal sealed record CodexStateThread(
    string ThreadId,
    string RolloutPath,
    string RolloutPathHash,
    long CreatedAtMs,
    long UpdatedAtMs,
    long TokensUsed,
    string? Model,
    string? ReasoningEffort,
    bool Archived);

internal sealed record CodexStateEdge(
    string ParentThreadId,
    string ChildThreadId,
    string Status,
    long ChildCreatedAtMs);

internal sealed record CodexStateCatalogBatch(
    string DatabasePath,
    int TotalThreadCount,
    long MaxUpdatedAtMs,
    IReadOnlyList<CodexStateThread> Threads,
    IReadOnlyList<CodexStateEdge> Edges);

/// <summary>
/// Reads Codex's private local thread catalog as an optional, read-only acceleration source.
/// The schema is deliberately fingerprinted rather than treated as a stable provider contract.
/// Unknown, malformed or unreadable databases return <see langword="null"/> so callers can fail
/// open to rollout filesystem discovery.
/// </summary>
internal sealed class CodexStateCatalog
{
    private static readonly string[] s_requiredThreadColumns =
    [
        "id",
        "rollout_path",
        "created_at",
        "created_at_ms",
        "updated_at",
        "updated_at_ms",
        "tokens_used",
        "model",
        "reasoning_effort",
        "archived"
    ];

    private readonly string _codexHome;

    public CodexStateCatalog(string codexHome)
    {
        _codexHome = Path.GetFullPath(codexHome);
    }

    public async Task<CodexStateCatalogBatch?> TryReadSinceAsync(
        long minimumUpdatedAtMs,
        CancellationToken cancellationToken)
    {
        foreach (var databasePath in DiscoverCandidateDatabases())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await TryReadDatabaseAsync(
                    databasePath,
                    Math.Max(0, minimumUpdatedAtMs),
                    cancellationToken);
                if (result is { TotalThreadCount: > 0 })
                {
                    return result;
                }

                // A newly created/rotated but empty private DB must not mask an older populated
                // candidate or the filesystem fallback. Empty Codex history is cheap to rediscover.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsCatalogFailure(exception))
            {
                // A private Codex DB can disappear, rotate, be locked by an incompatible build, or
                // change schema/data shape at any time. The observatory caller owns the fallback.
            }
        }

        return null;
    }

    private IReadOnlyList<string> DiscoverCandidateDatabases()
    {
        if (!Directory.Exists(_codexHome))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(_codexHome, "state_*.sqlite", SearchOption.TopDirectoryOnly)
                .Select(TryBuildCandidate)
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .OrderByDescending(candidate => candidate.Generation)
                .ThenByDescending(candidate => candidate.LastWrite)
                .Select(candidate => candidate.Path)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static StateDatabaseCandidate? TryBuildCandidate(string path)
    {
        var fileName = Path.GetFileName(path);
        const string prefix = "state_";
        const string suffix = ".sqlite";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var generationText = fileName[prefix.Length..^suffix.Length];
        if (!int.TryParse(
                generationText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var generation))
        {
            return null;
        }

        return new StateDatabaseCandidate(
            Path.GetFullPath(path),
            generation,
            TryGetLastWriteUtc(path));
    }

    private static async Task<CodexStateCatalogBatch?> TryReadDatabaseAsync(
        string databasePath,
        long minimumUpdatedAtMs,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasRecognizedThreadsSchemaAsync(connection, cancellationToken))
        {
            return null;
        }

        // The current recognized Codex schema has populated millisecond timestamps and a dedicated
        // updated_at_ms index. If a private build leaves them null, use the compatibility fallback
        // rather than wrapping the indexed column in COALESCE and quietly turning every refresh into
        // a full scan/sort.
        var nullTimestampCommand = connection.CreateCommand();
        nullTimestampCommand.CommandText = """
            SELECT 1
            FROM threads
            WHERE created_at_ms IS NULL OR updated_at_ms IS NULL
            LIMIT 1;
            """;
        if (await nullTimestampCommand.ExecuteScalarAsync(cancellationToken) is not null)
        {
            return null;
        }

        var totalCommand = connection.CreateCommand();
        totalCommand.CommandText = "SELECT COUNT(*) FROM threads;";
        var totalThreadCount = checked((int)ReadRequiredInt64(
            await totalCommand.ExecuteScalarAsync(cancellationToken),
            "threads count"));

        var threadCommand = connection.CreateCommand();
        threadCommand.CommandText = """
            SELECT id,
                   rollout_path,
                   created_at_ms,
                   updated_at_ms,
                   tokens_used,
                   model,
                   reasoning_effort,
                   archived
            FROM threads
            WHERE updated_at_ms >= $minimum
            ORDER BY updated_at_ms, id;
            """;
        threadCommand.Parameters.AddWithValue("$minimum", minimumUpdatedAtMs);

        var threads = new List<CodexStateThread>();
        long maxUpdatedAtMs = 0;
        await using (var reader = await threadCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var threadId = ReadRequiredString(reader.GetValue(0), "threads.id");
                var rolloutPath = ReadRequiredString(reader.GetValue(1), "threads.rollout_path");
                var normalizedPath = Path.GetFullPath(rolloutPath);
                var createdAtMs = ReadRequiredInt64(reader.GetValue(2), "threads.created_at_ms");
                var updatedAtMs = ReadRequiredInt64(reader.GetValue(3), "threads.updated_at_ms");
                var tokensUsed = ReadRequiredInt64(reader.GetValue(4), "threads.tokens_used");
                var archived = ReadRequiredInt64(reader.GetValue(7), "threads.archived") != 0;

                maxUpdatedAtMs = Math.Max(maxUpdatedAtMs, updatedAtMs);
                threads.Add(new CodexStateThread(
                    threadId,
                    normalizedPath,
                    HashPath(normalizedPath),
                    createdAtMs,
                    updatedAtMs,
                    tokensUsed,
                    ReadOptionalString(reader.GetValue(5)),
                    ReadOptionalString(reader.GetValue(6)),
                    archived));
            }
        }

        var edges = await ReadSpawnEdgesAsync(connection, cancellationToken);
        return new CodexStateCatalogBatch(
            databasePath,
            totalThreadCount,
            maxUpdatedAtMs,
            threads,
            edges);
    }

    private static async Task<bool> HasRecognizedThreadsSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'threads' LIMIT 1;";
        if (await tableCommand.ExecuteScalarAsync(cancellationToken) is null)
        {
            return false;
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(threads);";
        await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(ReadRequiredString(reader.GetValue(1), "threads column name"));
        }

        return s_requiredThreadColumns.All(columns.Contains);
    }

    private static async Task<IReadOnlyList<CodexStateEdge>> ReadSpawnEdgesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'thread_spawn_edges' LIMIT 1;";
        if (await tableCommand.ExecuteScalarAsync(cancellationToken) is null)
        {
            return [];
        }

        var edges = new List<CodexStateEdge>();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.parent_thread_id,
                   e.child_thread_id,
                   e.status,
                   child.created_at_ms
            FROM thread_spawn_edges e
            JOIN threads parent ON parent.id = e.parent_thread_id
            JOIN threads child ON child.id = e.child_thread_id
            WHERE e.parent_thread_id <> e.child_thread_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new CodexStateEdge(
                ReadRequiredString(reader.GetValue(0), "thread_spawn_edges.parent_thread_id"),
                ReadRequiredString(reader.GetValue(1), "thread_spawn_edges.child_thread_id"),
                ReadOptionalString(reader.GetValue(2)) ?? "unknown",
                ReadRequiredInt64(reader.GetValue(3), "thread_spawn_edges child created_at_ms")));
        }

        return RemoveCyclicEdges(edges);
    }

    private static IReadOnlyList<CodexStateEdge> RemoveCyclicEdges(IReadOnlyList<CodexStateEdge> edges)
    {
        var accepted = new List<CodexStateEdge>(edges.Count);
        var parentsByChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (parentsByChild.ContainsKey(edge.ChildThreadId))
            {
                continue;
            }

            var cursor = edge.ParentThreadId;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                edge.ChildThreadId
            };
            var cycle = false;
            while (parentsByChild.TryGetValue(cursor, out var parent))
            {
                if (!visited.Add(cursor))
                {
                    cycle = true;
                    break;
                }
                cursor = parent;
            }

            if (cycle || !visited.Add(cursor))
            {
                continue;
            }

            parentsByChild[edge.ChildThreadId] = edge.ParentThreadId;
            accepted.Add(edge);
        }

        return accepted;
    }

    private static string ReadRequiredString(object? value, string field)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidDataException($"Recognized Codex state has invalid {field}.");
    }

    private static string? ReadOptionalString(object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        throw new InvalidDataException("Recognized Codex state contains a non-text optional field.");
    }

    private static long ReadRequiredInt64(object? value, string field)
    {
        try
        {
            return value switch
            {
                long integer => integer,
                int integer => integer,
                short integer => integer,
                byte integer => integer,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => throw new InvalidDataException($"Recognized Codex state has invalid {field}.")
            };
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"Recognized Codex state has out-of-range {field}.", exception);
        }
    }

    private static bool IsCatalogFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or InvalidCastException or FormatException or OverflowException;

    private static string HashPath(string path)
    {
        var normalized = OperatingSystem.IsWindows()
            ? path.ToUpperInvariant()
            : path;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static DateTime TryGetLastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private sealed record StateDatabaseCandidate(string Path, int Generation, DateTime LastWrite);
}
