using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>
/// Query-optimized, content-free read model for the Observatory UI. Expensive filtering and
/// topology/storage scoping happen in SQLite before limits are applied so the UI does not need to
/// materialize global telemetry collections as history grows.
/// </summary>
public sealed class SqliteCodexObservatoryReadModel(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task<IReadOnlyList<CodexSessionOverview>> SearchSessionsAsync(
        string? search,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id,
                   (SELECT ar.parent_agent_id FROM agent_relationships ar WHERE ar.child_agent_id = s.session_id LIMIT 1),
                   COALESCE((SELECT a.name FROM agents a WHERE a.agent_id = s.session_id LIMIT 1), s.session_id),
                   s.repository, s.started_at_utc, s.last_activity_at_utc, s.status,
                   (SELECT a.model FROM agents a WHERE a.agent_id = s.session_id LIMIT 1),
                   COALESCE((SELECT SUM(t.uncached_input_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.cache_read_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.cache_write_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.non_reasoning_output_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.reasoning_output_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT SUM(t.reported_total_tokens) FROM codex_native_token_events t WHERE t.session_id = s.session_id), 0),
                   COALESCE((SELECT COUNT(*) FROM context_observations c WHERE c.session_id = s.session_id AND c.is_compaction = 1), 0),
                   (SELECT MAX(CASE WHEN c.context_window_tokens > 0 AND c.input_tokens IS NOT NULL
                                    THEN c.input_tokens * 100.0 / c.context_window_tokens END)
                    FROM context_observations c WHERE c.session_id = s.session_id),
                   COALESCE((SELECT SUM(r.record_bytes)
                             FROM rollout_records r
                             WHERE r.session_id = s.session_id
                               AND EXISTS(SELECT 1 FROM rollout_files f WHERE f.source_identity = r.source_identity)), 0),
                   COALESCE((SELECT COUNT(*) FROM usage_events e WHERE e.session_id = s.session_id), 0)
            FROM sessions s
            WHERE EXISTS(SELECT 1 FROM rollout_files f WHERE f.session_id = s.session_id)
              AND (
                  $search = '' OR
                  instr(lower(s.session_id), lower($search)) > 0 OR
                  instr(lower(s.repository), lower($search)) > 0 OR
                  instr(lower(s.status), lower($search)) > 0 OR
                  EXISTS(
                      SELECT 1 FROM agents a
                      WHERE a.agent_id = s.session_id
                        AND (instr(lower(a.name), lower($search)) > 0 OR
                             instr(lower(COALESCE(a.model, '')), lower($search)) > 0)
                  )
              )
            ORDER BY COALESCE(s.last_activity_at_utc, s.started_at_utc) DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$search", search?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));

        var results = new List<CodexSessionOverview>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CodexSessionOverview(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                new CodexNativeTokenTotals(
                    reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                    reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13)),
                reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetDouble(15),
                reader.GetInt64(16),
                reader.GetInt32(17)));
        }

        return results;
    }

    public async Task<CodexAgentTopology> GetAgentTopologyAsync(
        string selectedSessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string treeCte = """
            WITH RECURSIVE
            ancestors(id, depth) AS (
                SELECT $session, 0
                UNION ALL
                SELECT ar.parent_agent_id, ancestors.depth + 1
                FROM agent_relationships ar
                JOIN ancestors ON ar.child_agent_id = ancestors.id
                WHERE ancestors.depth < 32
            ),
            root(id) AS (
                SELECT id FROM ancestors ORDER BY depth DESC LIMIT 1
            ),
            tree(id, depth) AS (
                SELECT id, 0 FROM root
                UNION
                SELECT ar.child_agent_id, tree.depth + 1
                FROM agent_relationships ar
                JOIN tree ON ar.parent_agent_id = tree.id
                WHERE tree.depth < 32
            )
            """;

        var agentsCommand = connection.CreateCommand();
        agentsCommand.CommandText = treeCte + """
            SELECT a.agent_id, a.session_id, a.name, a.state, a.last_seen_utc, a.model
            FROM agents a
            JOIN tree ON tree.id = a.agent_id
            ORDER BY tree.depth, a.last_seen_utc;
            """;
        agentsCommand.Parameters.AddWithValue("$session", selectedSessionId);

        var agents = new List<Agent>();
        await using (var reader = await agentsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                agents.Add(new Agent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    Enum.TryParse<AgentRuntimeState>(reader.GetString(3), out var state)
                        ? state
                        : AgentRuntimeState.Unknown,
                    ParseUtc(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var relationshipsCommand = connection.CreateCommand();
        relationshipsCommand.CommandText = treeCte + """
            SELECT ar.parent_agent_id, ar.child_agent_id, ar.linked_at_utc
            FROM agent_relationships ar
            JOIN tree parent_tree ON parent_tree.id = ar.parent_agent_id
            JOIN tree child_tree ON child_tree.id = ar.child_agent_id
            ORDER BY ar.linked_at_utc;
            """;
        relationshipsCommand.Parameters.AddWithValue("$session", selectedSessionId);

        var relationships = new List<AgentRelationship>();
        await using (var reader = await relationshipsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                relationships.Add(new AgentRelationship(
                    reader.GetString(0),
                    reader.GetString(1),
                    ParseUtc(reader.GetString(2))));
            }
        }

        return new CodexAgentTopology(agents, relationships);
    }

    public async Task<IReadOnlyList<CodexRolloutStorageSummary>> GetSessionStorageAsync(
        string sessionId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.file_path, f.session_id, f.size_bytes,
                   COALESCE((SELECT COUNT(*) FROM rollout_records r WHERE r.source_identity = f.source_identity), 0),
                   COALESCE((SELECT MAX(r.record_bytes) FROM rollout_records r WHERE r.source_identity = f.source_identity), 0),
                   f.last_seen_at_utc
            FROM rollout_files f
            WHERE f.session_id = $session
            ORDER BY f.size_bytes DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$take", Math.Max(0, take));

        var results = new List<CodexRolloutStorageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CodexRolloutStorageSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                ParseUtc(reader.GetString(5))));
        }

        return results;
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

public sealed record CodexAgentTopology(
    IReadOnlyList<Agent> Agents,
    IReadOnlyList<AgentRelationship> Relationships);
