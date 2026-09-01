using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
/// Projects TajsTokens-owned disjoint Codex token events into the model/hourly shapes consumed by
/// Overview. This is intentionally a read-only projection: rollout parsing and counter semantics stay
/// owned by the Observatory writer path, while UI refreshes perform only indexed/grouped reads.
/// </summary>
public sealed class SqliteNativeCodexAccountingProvider(string databasePath) : ICodexTokenAccountingProvider
{
    private const string ProviderId = "codex-native";
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var usage = await LoadModelUsageAsync(connection, cancellationToken);
        var hourly = await LoadHourlyUsageAsync(connection, cancellationToken);
        return new CodexTokenAccountingSnapshot(
            "Native Codex",
            "Local normalized Codex rollout history only; remote/cloud-only sessions are not assumed to be zero.",
            usage,
            hourly);
    }

    private static async Task<IReadOnlyList<TokenUsage>> LoadModelUsageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(NULLIF(e.model, ''), NULLIF(a.model, ''), '(unknown)') AS model,
                   MAX(e.observed_at_utc) AS observed_at_utc,
                   SUM(e.uncached_input_tokens),
                   SUM(e.cache_read_tokens),
                   SUM(e.cache_write_tokens),
                   SUM(e.non_reasoning_output_tokens),
                   SUM(e.reasoning_output_tokens),
                   SUM(e.reported_total_tokens)
            FROM codex_native_token_events e
            LEFT JOIN agents a ON a.agent_id = e.agent_id
            GROUP BY COALESCE(NULLIF(e.model, ''), NULLIF(a.model, ''), '(unknown)')
            HAVING SUM(e.uncached_input_tokens) +
                   SUM(e.cache_read_tokens) +
                   SUM(e.cache_write_tokens) +
                   SUM(e.non_reasoning_output_tokens) +
                   SUM(e.reasoning_output_tokens) > 0
            ORDER BY SUM(e.reported_total_tokens) DESC, model;
            """;

        var results = new List<TokenUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var model = reader.GetString(0);
            var observed = ParseUtc(reader.GetString(1));
            results.Add(new TokenUsage(
                ProviderId,
                "codex",
                model,
                observed,
                ReadBreakdown(reader, 2),
                Profile: "default"));
        }

        return results;
    }

    private static async Task<IReadOnlyList<TokenTimeBucket>> LoadHourlyUsageAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT substr(e.observed_at_utc, 1, 13) || ':00:00+00:00' AS hour_utc,
                   SUM(e.uncached_input_tokens),
                   SUM(e.cache_read_tokens),
                   SUM(e.cache_write_tokens),
                   SUM(e.non_reasoning_output_tokens),
                   SUM(e.reasoning_output_tokens),
                   SUM(e.reported_total_tokens)
            FROM codex_native_token_events e
            GROUP BY substr(e.observed_at_utc, 1, 13)
            HAVING SUM(e.uncached_input_tokens) +
                   SUM(e.cache_read_tokens) +
                   SUM(e.cache_write_tokens) +
                   SUM(e.non_reasoning_output_tokens) +
                   SUM(e.reasoning_output_tokens) > 0
            ORDER BY hour_utc;
            """;

        var results = new List<TokenTimeBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var startUtc = ParseUtc(reader.GetString(0));
            results.Add(new TokenTimeBucket(
                ProviderId,
                startUtc.ToLocalTime().ToString("yyyy-MM-dd HH:00", CultureInfo.CurrentCulture),
                startUtc,
                ReadBreakdown(reader, 1)));
        }

        return results;
    }

    private static TokenBreakdown ReadBreakdown(SqliteDataReader reader, int firstColumn) => new(
        reader.GetInt64(firstColumn),
        reader.GetInt64(firstColumn + 1),
        reader.GetInt64(firstColumn + 2),
        reader.GetInt64(firstColumn + 3),
        reader.GetInt64(firstColumn + 4),
        reader.GetInt64(firstColumn + 5));

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
