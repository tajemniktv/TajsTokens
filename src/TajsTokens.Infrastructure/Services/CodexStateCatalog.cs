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
    string Status);

internal sealed record CodexStateCatalogBatch(
    string DatabasePath,
    int TotalThreadCount,
    long MaxUpdatedAtMs,
    IReadOnlyList<CodexStateThread> Threads,
    IReadOnlyList<CodexStateEdge> Edges);

/// <summary>
/// Reads Codex's private local thread catalog as an optional, read-only acceleration source.
/// The schema is deliberately fingerprinted rather than treated as a stable provider contract.
/// Unknown or unreadable databases return <see langword="null"/> so callers can fail open to
/// rollout filesystem discovery.
/// </summary>
internal sealed class CodexStateCatalog
{
    private static readonly string[] RequiredThreadColumns =
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
                if (result is not null)
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                // A private Codex DB can disappear, rotate, be locked by an incompatible build, or
                // change schema/data shape at any time. The observatory caller owns the filesystem fallback.
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
                .Select(path => TryBuildCandidate(path))
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
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
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

        var totalCommand = connection.CreateCommand();
        totalCommand.CommandText = "SELECT COUNT(*) FROM threads;";
        var totalThreadCount = Convert.ToInt32(
            await totalCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);

        var threadCommand = connection.CreateCommand();
        threadCommand.CommandText = """
            SELECT id,
                   rollout_path,
                   COALESCE(created_at_ms, created_at * 1000),
                   COALESCE(updated_at_ms, updated_at * 1000),
                   tokens_used,
                   model,
                   reasoning_effort,
                   archived
            FROM threads
            WHERE COALESCE(updated_at_ms, updated_at * 1000) >= $minimum
            ORDER BY COALESCE(updated_at_ms, updated_at * 1000), id;
            """;
        threadCommand.Parameters.AddWithValue("$minimum", minimumUpdatedAtMs);

        var threads = new List<CodexStateThread>();
        long maxUpdatedAtMs = 0;
        await using (var reader = await threadCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var rolloutPath = reader.GetString(1);
                if (string.IsNullOrWhiteSpace(rolloutPath))
                {
                    throw new InvalidDataException("Recognized Codex state contains an empty rollout_path.");
                }

                var normalizedPath = Path.GetFullPath(rolloutPath);
                var updatedAtMs = reader.GetInt64(3);
                maxUpdatedAtMs = Math.Max(maxUpdatedAtMs, updatedAtMs);
                threads.Add(new CodexStateThread(
                    reader.GetString(0),
                    normalizedPath,
                    HashPath(normalizedPath),
                    reader.GetInt64(2),
                    updatedAtMs,
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt64(7) != 0));
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
            columns.Add(reader.GetString(1));
        }

        return RequiredThreadColumns.All(columns.Contains);
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
            SELECT e.parent_thread_id, e.child_thread_id, e.status
            FROM thread_spawn_edges e
            JOIN threads parent ON parent.id = e.parent_thread_id
            JOIN threads child ON child.id = e.child_thread_id
            WHERE e.parent_thread_id <> e.child_thread_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var parent = reader.GetString(0);
            var child = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(child))
            {
                continue;
            }

            edges.Add(new CodexStateEdge(
                parent,
                child,
                reader.IsDBNull(2) ? "unknown" : reader.GetString(2)));
        }

        return RemoveCyclicEdges(edges);
    }

    private static IReadOnlyList<CodexStateEdge> RemoveCyclicEdges(IReadOnlyList<CodexStateEdge> edges)
    {
        var accepted = new List<CodexStateEdge>(edges.Count);
        var parentsByChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            // Codex currently enforces one parent per child by primary key. Keep that invariant even
            // if a future/private schema or corrupted copy violates it.
            if (parentsByChild.ContainsKey(edge.ChildThreadId))
            {
                continue;
            }

            var cursor = edge.ParentThreadId;
            var cycle = false;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                edge.ChildThreadId
            };
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
