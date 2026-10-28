// Taj's Tokens | SqliteNativeCodexAccountingProvider.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Infrastructure.Providers;

/// <summary>
///     Projects TajsTokens-owned disjoint Codex token events into the model/hourly shapes consumed by
///     Overview. Rollout parsing and counter semantics stay owned by the Observatory writer path. The
///     historical aggregation uses its own token-row revision. Context-only evidence preserves those
///     arrays while advancing the separate generation used for forecast freshness.
/// </summary>
public sealed class SqliteNativeCodexAccountingProvider(string databasePath) : ICodexTokenAccountingProvider
{
    private const string ProviderId = "codex-native";
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    private readonly string _readConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared,
    }.ToString();

    private readonly string _writeConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared,
    }.ToString();

    private CodexTokenAccountingSnapshot? _cachedSnapshot;
    private AccountingStamp? _cachedStamp;
    private bool _revisionStoreInitialized;
    private string? _databaseIdentity;
    private string? _storageGeneration;

    public async Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var work = WorkDiagnostics.Measure(WorkPhase.NativeProjection);
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string identity = CodexSessionIngestionService.GetSourceIdentity(databasePath);
            if (identity.StartsWith("fallback", StringComparison.Ordinal)) identity = Guid.NewGuid().ToString("N");
            if (_databaseIdentity != identity)
            {
                if (_cachedSnapshot is not null) WorkDiagnostics.Log.CacheInvalidated((int)WorkPhase.NativeProjection, 1);
                _cachedSnapshot = null;
                _cachedStamp = null;
                _revisionStoreInitialized = false;
                // Restoring a database can reuse revision values. Reopen its physical generation.
                using var oldReader = new SqliteConnection(_readConnectionString);
                using var oldWriter = new SqliteConnection(_writeConnectionString);
                SqliteConnection.ClearPool(oldReader);
                SqliteConnection.ClearPool(oldWriter);
                _databaseIdentity = identity;
                _storageGeneration = Guid.NewGuid().ToString("N");
            }
            await EnsureRevisionStoreAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = new SqliteConnection(_readConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction();

            AccountingStamp stamp = await ReadStampAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (_cachedStamp?.ProjectionRevision == stamp.ProjectionRevision && _cachedSnapshot is not null)
            {
                transaction.Commit();
                // Context-only evidence still invalidates forecast freshness. Reuse numeric
                // projections without concealing the broader evidence generation's advance.
                if (_cachedStamp.Revision != stamp.Revision)
                    _cachedSnapshot = _cachedSnapshot with { Revision = stamp.Revision };
                _cachedStamp = stamp;
                work?.Complete(rows: 0, committedTransactions: 1, cacheHits: 1);
                return _cachedSnapshot;
            }

            // Both expensive projections share one read transaction, so model totals and hourly
            // buckets describe the same committed SQLite generation even if ingestion writes nearby.
            if (_cachedSnapshot is not null) WorkDiagnostics.Log.CacheInvalidated((int)WorkPhase.NativeProjection, 2);
            IReadOnlyList<TokenUsage> usage = await LoadModelUsageAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TokenTimeBucket> hourly =
                await LoadHourlyUsageAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            DateTimeOffset? asOfUtc = usage.Select(item => item.CapturedAtUtc)
                .Where(item => item is not null)
                .Max();
            var snapshot = new CodexTokenAccountingSnapshot(
                "Native Codex",
                "Local normalized Codex rollout history only; remote/cloud-only sessions are not assumed to be zero.",
                usage,
                hourly,
                AsOfUtc: asOfUtc,
                Revision: stamp.Revision) { StorageGeneration = _storageGeneration };
            _cachedStamp = stamp;
            _cachedSnapshot = snapshot;
            work?.Complete(rows: usage.Count + hourly.Count, committedTransactions: 1, cacheHits: 0);
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
        await SqliteNativeTokenRevision.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        _revisionStoreInitialized = true;
    }

    private static async Task<AccountingStamp> ReadStampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT (SELECT revision FROM codex_native_accounting_revision WHERE id = 1),
                                     (SELECT revision FROM codex_native_token_projection_revision WHERE id = 1);
                              """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Native accounting revision is unavailable.");
        }

        return new AccountingStamp(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<TokenUsage>> LoadModelUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT COALESCE(NULLIF(e.model, ''), '(unknown)') AS model,
                                     CASE WHEN COUNT(e.source_event_at_utc) = COUNT(*)
                                               AND MIN(e.source_timestamp_quality) = 'Valid'
                                               AND MAX(e.source_timestamp_quality) = 'Valid'
                                          THEN MAX(e.source_event_at_utc) END AS source_event_at_utc,
                                     MAX(e.captured_at_utc) AS captured_at_utc,
                                     CASE WHEN COUNT(DISTINCT e.source_timestamp_quality) = 1
                                          THEN MAX(e.source_timestamp_quality) ELSE 'Mixed' END AS time_quality,
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
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string model = reader.GetString(0);
            DateTimeOffset? observed = reader.IsDBNull(1) ? null : ParseUtc(reader.GetString(1));
            results.Add(
                new TokenUsage(
                    ProviderId,
                    "codex",
                    model,
                    observed,
                    ReadBreakdown(reader, 4),
                    "default")
                {
                    SourceEventAtUtc = observed,
                    CapturedAtUtc = reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
                    SourceTimestampQuality = Enum.TryParse(reader.GetString(3), out CodexSourceTimestampQuality quality)
                        ? quality
                        : CodexSourceTimestampQuality.LegacyUnverified,
                });
        }

        return results;
    }

    private static async Task<IReadOnlyList<TokenTimeBucket>> LoadHourlyUsageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT substr(e.source_event_at_utc, 1, 13) || ':00:00+00:00' AS hour_utc,
                                     SUM(e.uncached_input_tokens),
                                     SUM(e.cache_read_tokens),
                                     SUM(e.cache_write_tokens),
                                     SUM(e.non_reasoning_output_tokens),
                                     SUM(e.reasoning_output_tokens),
                                     SUM(e.reported_total_tokens)
                              FROM codex_native_token_events e
                              WHERE e.source_event_at_utc IS NOT NULL AND e.source_timestamp_quality = 'Valid'
                              GROUP BY substr(e.source_event_at_utc, 1, 13)
                              HAVING SUM(e.uncached_input_tokens) +
                                     SUM(e.cache_read_tokens) +
                                     SUM(e.cache_write_tokens) +
                                     SUM(e.non_reasoning_output_tokens) +
                                     SUM(e.reasoning_output_tokens) +
                                     SUM(e.reported_total_tokens) > 0
                              ORDER BY hour_utc;
                              """;

        var results = new List<TokenTimeBucket>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset startUtc = ParseUtc(reader.GetString(0));
            results.Add(
                new TokenTimeBucket(
                    ProviderId,
                    startUtc.ToLocalTime().ToString("yyyy-MM-dd HH:00", CultureInfo.CurrentCulture),
                    startUtc,
                    ReadBreakdown(reader, 1)));
        }

        return results;
    }

    private static TokenBreakdown ReadBreakdown(SqliteDataReader reader, int firstColumn)
    {
        return new TokenBreakdown(
            reader.GetInt64(firstColumn),
            reader.GetInt64(firstColumn + 1),
            reader.GetInt64(firstColumn + 2),
            reader.GetInt64(firstColumn + 3),
            reader.GetInt64(firstColumn + 4),
            reader.GetInt64(firstColumn + 5));
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private sealed record AccountingStamp(long Revision, long ProjectionRevision);
}
