using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
/// Projects TajsTokens-owned disjoint Codex token events into the model/hourly shapes consumed by
/// Overview. Rollout parsing and counter semantics stay owned by the Observatory writer path. The
/// potentially expensive historical aggregation is memoized by a writer-maintained revision plus
/// cheap table-shape metadata, so unchanged warm refreshes do not rescan the full token-event table.
/// </summary>
public sealed class SqliteNativeCodexAccountingProvider(string databasePath) : ICodexTokenAccountingProvider
{
    private const string ProviderId = "codex-native";
    private readonly string _readConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared
    }.ToString();
    private readonly string _writeConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private bool _revisionStoreInitialized;
    private AccountingStamp? _cachedStamp;
    private CodexTokenAccountingSnapshot? _cachedSnapshot;

    public async Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureRevisionStoreAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = new SqliteConnection(_readConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();

            var stamp = await ReadStampAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (_cachedStamp == stamp && _cachedSnapshot is not null)
            {
                transaction.Commit();
                return _cachedSnapshot;
            }

            // Both expensive projections share one read transaction, so model totals and hourly
            // buckets describe the same committed SQLite generation even if ingestion writes nearby.
            var usage = await LoadModelUsageAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var hourly = await LoadHourlyUsageAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            var asOfUtc = usage.Select(item => (DateTimeOffset?)item.ObservedAtUtc)
                .Concat(hourly.Select(item => item.StartUtc))
                .Where(item => item is not null)
                .Max();
            var snapshot = new CodexTokenAccountingSnapshot(
                "Native Codex",
                "Local normalized Codex rollout history only; remote/cloud-only sessions are not assumed to be zero.",
                usage,
                hourly,
                AsOfUtc: asOfUtc,
                Revision: stamp.Revision);
            _cachedStamp = stamp;
            _cachedSnapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async Task EnsureRevisionStoreAsync(CancellationToken cancellationToken)
    {
        if (_revisionStoreInitialized)
        {
            return;
        }

        await using var connection = new SqliteConnection(_writeConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS codex_native_accounting_revision (
                id INTEGER PRIMARY KEY CHECK(id = 1),
                revision INTEGER NOT NULL
            );
            INSERT INTO codex_native_accounting_revision(id, revision)
            VALUES(1, 0)
            ON CONFLICT(id) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _revisionStoreInitialized = true;
    }

    private static async Task<AccountingStamp> ReadStampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT (SELECT revision FROM codex_native_accounting_revision WHERE id = 1),
                   COUNT(*),
                   COALESCE(MAX(rowid), 0)
            FROM codex_native_token_events;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AccountingStamp(0, 0, 0);
        }

        return new AccountingStamp(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<IReadOnlyList<TokenUsage>> LoadModelUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(NULLIF(e.model, ''), '(unknown)') AS model,
                   MAX(e.observed_at_utc) AS observed_at_utc,
                   SUM(e.uncached_input_tokens),
                   SUM(e.cache_read_tokens),
                   SUM(e.cache_write_tokens),
                   SUM(e.non_reasoning_output_tokens),
                   SUM(e.reasoning_output_tokens),
                   SUM(e.reported_total_tokens)
            FROM codex_native_token_events e
            GROUP BY COALESCE(NULLIF(e.model, ''), '(unknown)')
            HAVING SUM(e.uncached_input_tokens) +
                   SUM(e.cache_read_tokens) +
                   SUM(e.cache_write_tokens) +
                   SUM(e.non_reasoning_output_tokens) +
                   SUM(e.reasoning_output_tokens) +
                   SUM(e.reported_total_tokens) > 0
            ORDER BY SUM(e.reported_total_tokens) DESC, model;
            """;

        var results = new List<TokenUsage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                   SUM(e.reasoning_output_tokens) +
                   SUM(e.reported_total_tokens) > 0
            ORDER BY hour_utc;
            """;

        var results = new List<TokenTimeBucket>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

    private sealed record AccountingStamp(long Revision, long EventCount, long MaxRowId);
}
