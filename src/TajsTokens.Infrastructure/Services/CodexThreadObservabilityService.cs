using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Read-only, provider-native Codex thread view. The state catalog and thread-history stores are
/// queried on demand; no source rows are mirrored into the TajsTokens database.
/// </summary>
public sealed class CodexThreadObservabilityService
{
    private const int MaxHistoryRows = 5_000;
    private const int MaxCatalogRows = 10_000;
    private readonly CodexStateDbExplorerService _sources;

    public CodexThreadObservabilityService(string? codexHome = null, string? snapshotDirectory = null)
    {
        _sources = new CodexStateDbExplorerService(codexHome, snapshotDirectory);
    }

    public string CodexHome => _sources.CodexHome;

    /// <summary>
    /// Lists source-native thread rows from all readable state stores. Equal IDs from distinct
    /// source instances are retained only once for the list, choosing the newest observed row;
    /// the selected row still carries its source path.
    /// </summary>
    public async Task<IReadOnlyList<CodexThreadCatalogEntry>> SearchThreadsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken)
    {
        var candidates = _sources.DiscoverCandidates();
        var rows = new Dictionary<string, CodexThreadCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var normalizedSearch = search?.Trim() ?? string.Empty;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
                if (!await HasTableAsync(connection, "threads", cancellationToken))
                {
                    continue;
                }

                await foreach (var values in ReadRowsAsync(
                                   connection,
                                   "threads",
                                   whereClause: null,
                                   parameter: null,
                                   orderBy: null,
                                   MaxCatalogRows,
                                   cancellationToken))
                {
                    var entry = MapThread(values, candidate);
                    if (entry is null || !Matches(entry, normalizedSearch))
                    {
                        continue;
                    }

                    if (!rows.TryGetValue(entry.ThreadId, out var previous) || IsNewer(entry, previous))
                    {
                        rows[entry.ThreadId] = entry;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                // Search is best-effort across rotating private stores. The detailed read reports
                // source warnings; one unreadable candidate must not hide other threads.
            }
        }

        return rows.Values
            .OrderByDescending(entry => entry.RecencyAtUtc ?? entry.UpdatedAtUtc ?? entry.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, take))
            .ToArray();
    }

    public async Task<CodexThreadReadResult> ReadThreadAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("A thread ID is required.", nameof(threadId));
        }

        var warnings = new List<string>();
        var capturedAtUtc = DateTimeOffset.UtcNow;
        CodexThreadCatalogEntry? thread = null;
        CodexThreadProject? project = null;
        CodexThreadSection? section = null;
        var edges = Array.Empty<CodexThreadSpawnEdge>();
        var dynamicTools = Array.Empty<CodexThreadDynamicTool>();
        string? stateSourcePath = null;
        string? stateSourceDescription = null;
        bool? projectCapability = null;
        bool? projectRootsCapability = null;
        bool? sectionCapability = null;
        bool? dynamicToolsCapability = null;
        bool? spawnEdgesCapability = null;

        foreach (var candidate in _sources.DiscoverCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
                if (!await HasTableAsync(connection, "threads", cancellationToken))
                {
                    continue;
                }

                var values = await ReadSingleRowAsync(connection, "threads", "id", threadId, cancellationToken);
                if (values is null)
                {
                    continue;
                }

                thread = MapThread(values, candidate);
                if (thread is null)
                {
                    warnings.Add($"{candidate.Path}: threads row has no usable id.");
                    continue;
                }

                stateSourcePath = candidate.Path;
                stateSourceDescription = candidate.SourceDescription;
                projectCapability = await HasTableAsync(connection, "projects", cancellationToken);
                if (!string.IsNullOrWhiteSpace(thread.ProjectId) && projectCapability == true)
                {
                    projectRootsCapability = await HasTableAsync(connection, "project_roots", cancellationToken);
                    project = await ReadProjectAsync(connection, thread.ProjectId!, cancellationToken);
                }

                sectionCapability = await HasTableAsync(connection, "thread_sections", cancellationToken);
                if (!string.IsNullOrWhiteSpace(thread.SectionId) && sectionCapability == true)
                {
                    section = await ReadSectionAsync(connection, thread.SectionId!, cancellationToken);
                }

                dynamicToolsCapability = await HasTableAsync(connection, "thread_dynamic_tools", cancellationToken);
                if (dynamicToolsCapability == true)
                {
                    dynamicTools = await ReadDynamicToolsAsync(connection, threadId, cancellationToken);
                }

                spawnEdgesCapability = await HasTableAsync(connection, "thread_spawn_edges", cancellationToken);
                if (spawnEdgesCapability == true)
                {
                    edges = await ReadSpawnEdgesAsync(connection, threadId, cancellationToken);
                }

                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                warnings.Add($"{candidate.Path}: {Summarize(exception.Message)}");
            }
        }

        var turns = Array.Empty<CodexThreadTurn>();
        var items = Array.Empty<CodexThreadItem>();
        var realtimeItems = Array.Empty<CodexThreadRealtimeItem>();
        string? historySourcePath = null;
        string? historySourceDescription = null;
        bool? turnsCapability = null;
        bool? itemsCapability = null;
        bool? realtimeCapability = null;

        foreach (var candidate in _sources.DiscoverCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenReadOnlyAsync(candidate.Path, cancellationToken);
                var hasTurns = await HasTableAsync(connection, "thread_turns", cancellationToken);
                var hasItems = await HasTableAsync(connection, "thread_items", cancellationToken);
                var hasRealtime = await HasTableAsync(connection, "thread_realtime_items", cancellationToken);
                turnsCapability = hasTurns;
                itemsCapability = hasItems;
                realtimeCapability = hasRealtime;
                if (!hasTurns && !hasItems && !hasRealtime)
                {
                    continue;
                }

                if (hasTurns)
                {
                    turns = await ReadTurnsAsync(connection, threadId, cancellationToken);
                }
                if (hasItems)
                {
                    items = await ReadItemsAsync(connection, threadId, cancellationToken);
                }
                if (hasRealtime)
                {
                    realtimeItems = await ReadRealtimeItemsAsync(connection, threadId, cancellationToken);
                }

                if (turns.Length > 0 || items.Length > 0 || realtimeItems.Length > 0)
                {
                    historySourcePath = candidate.Path;
                    historySourceDescription = candidate.SourceDescription;
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsSourceFailure(exception))
            {
                warnings.Add($"{candidate.Path}: {Summarize(exception.Message)}");
            }
        }

        if (thread is null && historySourcePath is null)
        {
            warnings.Add($"No readable Codex source row was found for thread {threadId}.");
        }

        return new CodexThreadReadResult(
            thread,
            project,
            section,
            edges,
            dynamicTools,
            turns,
            items,
            realtimeItems,
            stateSourcePath,
            historySourcePath,
            warnings)
        {
            CapturedAtUtc = capturedAtUtc,
            ProjectCapabilityAvailable = projectCapability,
            ProjectRootsCapabilityAvailable = projectRootsCapability,
            SectionCapabilityAvailable = sectionCapability,
            DynamicToolsCapabilityAvailable = dynamicToolsCapability,
            SpawnEdgesCapabilityAvailable = spawnEdgesCapability,
            TurnsCapabilityAvailable = turnsCapability,
            ItemsCapabilityAvailable = itemsCapability,
            RealtimeCapabilityAvailable = realtimeCapability,
            StateSourceDescription = stateSourceDescription,
            HistorySourceDescription = historySourceDescription
        };
    }

    private static bool Matches(CodexThreadCatalogEntry entry, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        return new[]
            {
                entry.ThreadId, entry.Title, entry.Name, entry.Preview, entry.Cwd,
                entry.Model, entry.ModelProvider, entry.Source, entry.ThreadSource,
                entry.ProjectId, entry.GitBranch
            }
            .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsNewer(CodexThreadCatalogEntry candidate, CodexThreadCatalogEntry previous) =>
        (candidate.RecencyAtUtc ?? candidate.UpdatedAtUtc ?? DateTimeOffset.MinValue) >
        (previous.RecencyAtUtc ?? previous.UpdatedAtUtc ?? DateTimeOffset.MinValue);

    private static CodexThreadCatalogEntry? MapThread(
        IReadOnlyDictionary<string, object?> values,
        CodexStateDatabaseCandidate candidate)
    {
        var id = ReadOptionalString(values, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new CodexThreadCatalogEntry(
            id,
            candidate.Path,
            ReadOptionalString(values, "source"),
            ReadOptionalString(values, "thread_source"),
            ReadOptionalString(values, "model_provider"),
            ReadOptionalString(values, "title"),
            ReadOptionalString(values, "name"),
            ReadOptionalString(values, "preview"),
            ReadOptionalString(values, "first_user_message"),
            ReadOptionalString(values, "cwd"),
            ReadTimestamp(values, "created_at_ms", "created_at"),
            ReadTimestamp(values, "updated_at_ms", "updated_at"),
            ReadTimestamp(values, "recency_at_ms", "recency_at", zeroMeansMissing: true),
            ReadOptionalString(values, "model"),
            ReadOptionalString(values, "reasoning_effort"),
            ReadOptionalString(values, "sandbox_policy"),
            ReadOptionalString(values, "approval_mode"),
            ReadOptionalString(values, "history_mode"),
            ReadOptionalString(values, "memory_mode"),
            ReadOptionalString(values, "git_sha"),
            ReadOptionalString(values, "git_branch"),
            ReadOptionalString(values, "git_origin_url"),
            ReadOptionalString(values, "agent_nickname"),
            ReadOptionalString(values, "agent_role"),
            ReadOptionalString(values, "agent_path"),
            ReadOptionalString(values, "project_id"),
            ReadOptionalString(values, "thread_section_id"),
            ReadNullableInt(values, "section_position"),
            ReadNullableBool(values, "archived"),
            ReadNullableBool(values, "is_pinned"))
        {
            SourceDescription = candidate.SourceDescription,
            DiscoveryKind = candidate.DiscoveryKind
        };
    }

    private static async Task<CodexThreadProject?> ReadProjectAsync(
        SqliteConnection connection,
        string projectId,
        CancellationToken cancellationToken)
    {
        var values = await ReadSingleRowAsync(connection, "projects", "id", projectId, cancellationToken);
        if (values is null)
        {
            return null;
        }

        var roots = new List<string>();
        var rootsCapability = await HasTableAsync(connection, "project_roots", cancellationToken);
        if (rootsCapability)
        {
            await foreach (var root in ReadRowsAsync(
                               connection,
                               "project_roots",
                               "project_id = $value",
                               projectId,
                               "position ASC",
                               MaxHistoryRows,
                               cancellationToken))
            {
                var path = ReadOptionalString(root, "path");
                if (path is not null)
                {
                    roots.Add(path);
                }
            }
        }

        return new CodexThreadProject(
            projectId,
            ReadOptionalString(values, "name") ?? projectId,
            ReadOptionalString(values, "metadata"),
            ReadNullableInt(values, "position"),
            ReadTimestamp(values, "created_at_ms", "created_at"),
            ReadTimestamp(values, "updated_at_ms", "updated_at"),
            roots)
        {
            RootsCapabilityAvailable = rootsCapability
        };
    }

    private static async Task<CodexThreadSection?> ReadSectionAsync(
        SqliteConnection connection,
        string sectionId,
        CancellationToken cancellationToken)
    {
        var values = await ReadSingleRowAsync(connection, "thread_sections", "id", sectionId, cancellationToken);
        return values is null
            ? null
            : new CodexThreadSection(
                sectionId,
                ReadOptionalString(values, "name") ?? sectionId,
                ReadOptionalString(values, "appearance"));
    }

    private static async Task<CodexThreadDynamicTool[]> ReadDynamicToolsAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var tools = new List<CodexThreadDynamicTool>();
        await foreach (var values in ReadRowsAsync(
                           connection,
                           "thread_dynamic_tools",
                           "thread_id = $value",
                           threadId,
                           "position ASC",
                           MaxHistoryRows,
                           cancellationToken))
        {
            tools.Add(new CodexThreadDynamicTool(
                ReadNullableInt(values, "position") ?? tools.Count,
                ReadOptionalString(values, "name") ?? string.Empty,
                ReadOptionalString(values, "description") ?? string.Empty,
                ReadOptionalString(values, "input_schema") ?? string.Empty,
                ReadNullableBool(values, "defer_loading") ?? false,
                ReadOptionalString(values, "namespace")));
        }

        return tools.ToArray();
    }

    private static async Task<CodexThreadSpawnEdge[]> ReadSpawnEdgesAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var all = new List<(string Parent, string Child, string Status)>();
        await foreach (var values in ReadRowsAsync(
                           connection,
                           "thread_spawn_edges",
                           whereClause: null,
                           parameter: null,
                           orderBy: null,
                           MaxHistoryRows,
                           cancellationToken))
        {
            var parent = ReadOptionalString(values, "parent_thread_id");
            var child = ReadOptionalString(values, "child_thread_id");
            if (parent is not null && child is not null)
            {
                all.Add((parent, child, ReadOptionalString(values, "status") ?? "unknown"));
            }
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { threadId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in all)
            {
                if (reachable.Contains(edge.Parent) || reachable.Contains(edge.Child))
                {
                    changed |= reachable.Add(edge.Parent);
                    changed |= reachable.Add(edge.Child);
                }
            }
        }

        return all
            .Where(edge => reachable.Contains(edge.Parent) && reachable.Contains(edge.Child))
            .Select(edge => new CodexThreadSpawnEdge(
                edge.Parent,
                edge.Child,
                edge.Status,
                DistanceFrom(threadId, edge.Parent, edge.Child, all)))
            .OrderBy(edge => edge.Depth)
            .ThenBy(edge => edge.ParentThreadId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(edge => edge.ChildThreadId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int DistanceFrom(
        string selected,
        string parent,
        string child,
        IReadOnlyList<(string Parent, string Child, string Status)> edges)
    {
        if (string.Equals(selected, parent, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selected, child, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var queue = new Queue<(string Id, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selected };
        queue.Enqueue((selected, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges)
            {
                var next = string.Equals(edge.Parent, current.Id, StringComparison.OrdinalIgnoreCase)
                    ? edge.Child
                    : string.Equals(edge.Child, current.Id, StringComparison.OrdinalIgnoreCase)
                        ? edge.Parent
                        : null;
                if (next is null || !visited.Add(next))
                {
                    continue;
                }

                if (string.Equals(next, parent, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(next, child, StringComparison.OrdinalIgnoreCase))
                {
                    return current.Depth + 1;
                }

                queue.Enqueue((next, current.Depth + 1));
            }
        }

        return -1;
    }

    private static async Task<CodexThreadTurn[]> ReadTurnsAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var turns = new List<CodexThreadTurn>();
        await foreach (var values in ReadRowsAsync(
                           connection,
                           "thread_turns",
                           "thread_id = $value",
                           threadId,
                           "rollout_ordinal ASC",
                           MaxHistoryRows,
                           cancellationToken))
        {
            var turnId = ReadOptionalString(values, "turn_id");
            if (turnId is null)
            {
                continue;
            }

            turns.Add(new CodexThreadTurn(
                turnId,
                ReadNullableLong(values, "rollout_ordinal") ?? turns.Count,
                ReadOptionalString(values, "status") ?? "unknown",
                ReadOptionalString(values, "error_json"),
                ReadTimestamp(values, "started_at_ms", "started_at"),
                ReadTimestamp(values, "completed_at_ms", "completed_at"),
                ReadNullableLong(values, "duration_ms"),
                ReadOptionalString(values, "first_user_item_id"),
                ReadOptionalString(values, "final_agent_item_id"),
                ReadNullableLong(values, "rollout_byte_offset"),
                ReadNullableLong(values, "rollout_end_ordinal"),
                ReadNullableLong(values, "rollout_end_byte_offset")));
        }

        return turns.ToArray();
    }

    private static async Task<CodexThreadItem[]> ReadItemsAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var items = new List<CodexThreadItem>();
        await foreach (var values in ReadRowsAsync(
                           connection,
                           "thread_items",
                           "thread_id = $value",
                           threadId,
                           "rollout_ordinal ASC",
                           MaxHistoryRows,
                           cancellationToken))
        {
            var itemId = ReadOptionalString(values, "item_id");
            if (itemId is null)
            {
                continue;
            }

            items.Add(new CodexThreadItem(
                ReadOptionalString(values, "turn_id") ?? string.Empty,
                itemId,
                ReadNullableLong(values, "rollout_ordinal") ?? items.Count,
                ReadTimestamp(values, "created_at_ms", "created_at"),
                ReadOptionalString(values, "item_type") ?? string.Empty,
                ReadOptionalString(values, "item_json") ?? string.Empty,
                ReadNullableLong(values, "updated_at_ordinal")));
        }

        return items.ToArray();
    }

    private static async Task<CodexThreadRealtimeItem[]> ReadRealtimeItemsAsync(
        SqliteConnection connection,
        string threadId,
        CancellationToken cancellationToken)
    {
        var items = new List<CodexThreadRealtimeItem>();
        await foreach (var values in ReadRowsAsync(
                           connection,
                           "thread_realtime_items",
                           "thread_id = $value",
                           threadId,
                           "rollout_ordinal ASC",
                           MaxHistoryRows,
                           cancellationToken))
        {
            var itemId = ReadOptionalString(values, "item_id");
            if (itemId is null)
            {
                continue;
            }

            items.Add(new CodexThreadRealtimeItem(
                itemId,
                ReadNullableLong(values, "rollout_ordinal") ?? items.Count,
                ReadTimestamp(values, "created_at_ms", "created_at"),
                ReadOptionalString(values, "item_type") ?? string.Empty,
                ReadOptionalString(values, "item_json") ?? string.Empty));
        }

        return items.ToArray();
    }

    private static async Task<Dictionary<string, object?>?> ReadSingleRowAsync(
        SqliteConnection connection,
        string table,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        await foreach (var row in ReadRowsAsync(
                           connection,
                           table,
                           $"{QuoteIdentifier(column)} = $value",
                           value,
                           orderBy: null,
                           limit: 1,
                           cancellationToken))
        {
            return row;
        }

        return null;
    }

    private static async IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(
        SqliteConnection connection,
        string table,
        string? whereClause,
        string? parameter,
        string? orderBy,
        int limit,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var sql = $"SELECT * FROM {QuoteIdentifier(table)}";
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            sql += " WHERE " + whereClause;
        }
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sql += " ORDER BY " + orderBy;
        }
        sql += " LIMIT $limit;";
        command.CommandText = sql;
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("$value", parameter);
        }
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            yield return values;
        }
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string? ReadOptionalString(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value is null || value is DBNull)
        {
            return null;
        }

        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            byte[] bytes => Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static long? ReadNullableLong(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value is null || value is DBNull)
        {
            return null;
        }

        try
        {
            return value switch
            {
                long number => number,
                int number => number,
                short number => number,
                byte number => number,
                double number => checked((long)number),
                decimal number => checked((long)number),
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
                _ => null
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static int? ReadNullableInt(IReadOnlyDictionary<string, object?> values, string name)
    {
        var value = ReadNullableLong(values, name);
        return value is long number && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
    }

    private static bool? ReadNullableBool(IReadOnlyDictionary<string, object?> values, string name)
    {
        var number = ReadNullableLong(values, name);
        if (number is not null)
        {
            return number.Value != 0;
        }

        var text = ReadOptionalString(values, name);
        return bool.TryParse(text, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadTimestamp(
        IReadOnlyDictionary<string, object?> values,
        string preferred,
        string fallback,
        bool zeroMeansMissing = false)
    {
        if (values.TryGetValue(preferred, out var preferredValue) && preferredValue is not null && preferredValue is not DBNull)
        {
            var parsed = ParseTimestamp(preferredValue, zeroMeansMissing);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return values.TryGetValue(fallback, out var fallbackValue) && fallbackValue is not null && fallbackValue is not DBNull
            ? ParseTimestamp(fallbackValue, zeroMeansMissing)
            : null;
    }

    private static DateTimeOffset? ParseTimestamp(object value, bool zeroMeansMissing)
    {
        if (value is string text)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return null;
            }

            value = integer;
        }

        try
        {
            var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (zeroMeansMissing && number == 0)
            {
                return null;
            }

            return Math.Abs(number) >= 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number);
        }
        catch (Exception) when (value is IConvertible)
        {
            return null;
        }
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static bool IsSourceFailure(Exception? exception = null) =>
        exception is null || exception is SqliteException or IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or InvalidDataException or NotSupportedException or
        InvalidCastException or FormatException or OverflowException;

    private static string Summarize(string message) =>
        message.ReplaceLineEndings(" ").Trim() switch
        {
            var compact when compact.Length <= 260 => compact,
            var compact => compact[..260] + "…"
        };
}
