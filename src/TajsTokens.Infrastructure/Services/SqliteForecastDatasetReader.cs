// Taj's Tokens | SqliteForecastDatasetReader.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Infrastructure.Services;

/// <summary>One read transaction over durable, content-free evidence. Never queries raw Codex payloads.</summary>
public sealed class SqliteForecastDatasetReader(string databasePath, IReadOnlyList<RolloutAccountAssociation>? accountAssociations = null)
    : ICodexForecastDatasetReader
{
    public async Task<CodexForecastDataset> ReadAsync(
        string provider,
        string profile,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken,
        string? accountKey = null,
        bool includeQuota = true,
        bool includeLedger = false)
        => await ReadCoreAsync(provider, profile, fromUtc, toUtc, cancellationToken, accountKey, includeQuota, includeLedger, false);

    internal Task<CodexForecastDataset> ReadQuotaOnlyAsync(string provider, string profile,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken token, string? accountKey)
        => ReadCoreAsync(provider, profile, fromUtc, toUtc, token, accountKey, true, false, true);

    // Presentation needs the selected native ledger and quota, not replay prefixes,
    // turn metadata or context samples. Keep both quantities in one read transaction.
    public Task<CodexForecastDataset> ReadLedgerAndQuotaAsync(string provider, string profile,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken token, string? accountKey = null,
        bool includeQuota = true)
        => ReadCoreAsync(provider, profile, fromUtc, toUtc, token, accountKey, includeQuota, true, false, ledgerOnly: true);

    private async Task<CodexForecastDataset> ReadCoreAsync(string provider, string profile,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken,
        string? accountKey, bool includeQuota, bool includeLedger, bool quotaOnly, bool ledgerOnly = false)
    {
        using var measurement = WorkDiagnostics.Measure(WorkPhase.DatasetRead);
        if (fromUtc >= toUtc) throw new ArgumentException("A non-empty chronological range is required.");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(true);
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        Dictionary<string, string>? repeatedText = null;

        string Shared(string value)
        {
            // Per-read value sharing, not global string interning. These common labels/IDs
            // already belong to the returned evidence. Unique record/turn IDs stay unpooled.
            if (value.Length > 512) return value;
            repeatedText ??= new(StringComparer.Ordinal);
            if (repeatedText.TryGetValue(value, out string? existing)) return existing;
            if (repeatedText.Count < 16384) repeatedText.Add(value, value);
            return value;
        }

        string? SharedText(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Shared(r.GetString(i));

        string Utc(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        DateTimeOffset Time(string value)
        {
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        string? Text(SqliteDataReader r, int i)
        {
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        long? Number(SqliteDataReader r, int i)
        {
            return r.IsDBNull(i) ? null : r.GetInt64(i);
        }

        DateTimeOffset? OptionalTime(SqliteDataReader r, int i)
        {
            return r.IsDBNull(i) ? null : Time(r.GetString(i));
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name = 'account_key';";
        bool hasAccountKey = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name = 'observation_id';";
        bool hasProvenance = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('codex_workload_observations') WHERE name = 'service_tier';";
        bool hasServiceTier = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        command.Parameters.AddWithValue("$from", Utc(fromUtc));
        command.Parameters.AddWithValue("$lookback", Utc(fromUtc.AddHours(-2)));
        command.Parameters.AddWithValue("$to", Utc(toUtc));
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$account", accountKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$includeQuota", includeQuota ? 1 : 0);
        command.Parameters.AddWithValue(
            "$asserted",
            accountAssociations?.Any(x => x.IsValid && x.Provider == provider &&
                                          x.Profile == profile && x.AccountKey == accountKey && x.AssertedAtUtc <= toUtc) == true
                ? 1
                : 0);
        command.CommandText = $"""
                               SELECT kind,captured_at_utc,used_percent,window_minutes,resets_at_utc,source,{(hasAccountKey ? "account_key" : "''")},
                                   {(hasProvenance ? "observation_id,source_identity,session_id,limit_id,plan_type,lane,collected_at_utc,has_source_timestamp" : "NULL,NULL,NULL,NULL,NULL,NULL,NULL,0")}
                               FROM quota_snapshots WHERE provider=$provider AND profile=$profile AND $includeQuota=1
                                 AND captured_at_utc >= $from AND captured_at_utc <= $to
                                 AND (source LIKE 'codex-app-server:%' OR source LIKE 'codex-rollout:%')
                                 {(hasProvenance ? "AND (source_identity IS NULL OR EXISTS(SELECT 1 FROM rollout_files f WHERE f.source_identity=quota_snapshots.source_identity))" : "")}
                                 AND ($account IS NULL OR {(hasAccountKey ? "account_key=$account OR ($asserted=1 AND (account_key IS NULL OR account_key=''))" : "0")})
                               ORDER BY captured_at_utc LIMIT 500001;
                               """;
        var quota = new List<QuotaSnapshot>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                quota.Add(
                    new QuotaSnapshot(
                        Enum.Parse<QuotaWindowKind>(reader.GetString(0)),
                        Time(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : reader.GetDouble(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        OptionalTime(reader, 4),
                        provider,
                        profile,
                        Shared(reader.GetString(5)),
                        reader.GetString(6) is { Length: > 0 } account ? Shared(account) : null)
                    {
                        ObservationId = Text(reader, 7) is { Length: > 0 } id ? id : null,
                        SourceIdentity = SharedText(reader, 8),
                        SessionId = SharedText(reader, 9),
                        LimitId = SharedText(reader, 10),
                        PlanType = SharedText(reader, 11),
                        Lane = SharedText(reader, 12),
                        CollectedAtUtc = OptionalTime(reader, 13),
                        HasSourceTimestamp = reader.GetInt32(14) != 0 &&
                                             (!string.IsNullOrEmpty(Text(reader, 7)) || OptionalTime(reader, 13) is not null),
                    });
        }
        if (quota.Count > 500000)
            throw new ForecastDatasetReadLimitException("Quota evaluation range exceeds the 500,000-row bound; narrow the range.");
        if (quotaOnly)
        {
            transaction.Commit();
            measurement?.Complete(rows: quota.Count, committedTransactions: 1);
            return new(quota, [], [], [], captured, "Quota-only evidence read.") { AccountAssociations = accountAssociations ?? [] };
        }
        var workload = new List<CodexWorkloadObservation>();
        if (!ledgerOnly)
        {
            command.CommandText = """
                                  SELECT w.source_record_id,w.source_identity,w.source_file,w.start_byte_offset,w.end_byte_offset,
                                         w.session_id,w.event_type,w.observed_at_utc,w.captured_at_utc,w.turn_id,w.root_turn_id,
                                         w.parent_thread_id,w.model,w.reasoning_effort,w.context_window_tokens,w.contract_version,
                                         w.started_at_unix_seconds,w.completed_at_unix_seconds,w.duration_ms,w.time_to_first_token_ms,w.session_source_kind,
                                  """ + (hasServiceTier ? "w.service_tier" : "NULL") + """

                                                                                       FROM codex_workload_observations w
                                                                                       WHERE w.observed_at_utc <= $to
                                                                                         AND w.session_id IN (
                                                                                             SELECT session_id FROM codex_native_token_events WHERE source_timestamp_quality = 'Valid' AND source_event_at_utc > $lookback AND source_event_at_utc <= $to
                                                                                             UNION SELECT session_id FROM codex_workload_observations WHERE observed_at_utc > $lookback AND observed_at_utc <= $to)
                                                                                         AND EXISTS(SELECT 1 FROM rollout_files f WHERE f.source_identity=w.source_identity)
                                                                                       ORDER BY w.observed_at_utc,w.start_byte_offset LIMIT 100001;
                                                                                       """;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    workload.Add(
                        new CodexWorkloadObservation(
                            reader.GetString(0),
                            Shared(reader.GetString(1)),
                            Shared(reader.GetString(2)),
                            reader.GetInt64(3),
                            reader.GetInt64(4),
                            Shared(reader.GetString(5)),
                            Shared(reader.GetString(6)),
                            OptionalTime(reader, 7),
                            Time(reader.GetString(8)),
                            Text(reader, 9),
                            SharedText(reader, 10),
                            SharedText(reader, 11),
                            SharedText(reader, 12),
                            SharedText(reader, 13),
                            Number(reader, 14),
                            Shared(reader.GetString(15)),
                            Number(reader, 16),
                            Number(reader, 17),
                            Number(reader, 18),
                            Number(reader, 19),
                            SharedText(reader, 20),
                            SharedText(reader, 21)));
            }
            if (workload.Count > 100000)
                throw new ForecastDatasetReadLimitException("Workload metadata exceeds the supported replay bound; a paged evidence read is required.");
        }
        command.CommandText = """
                              SELECT e.session_id,e.source_event_at_utc,e.captured_at_utc,e.model,e.reasoning_effort,e.uncached_input_tokens,
                                     e.cache_read_tokens,e.cache_write_tokens,e.non_reasoning_output_tokens,e.reasoning_output_tokens,e.reported_total_tokens
                              """ + (includeLedger
                                  ? """
                                    ,e.source_event_id,
                                     (SELECT CASE WHEN COUNT(*)=1 THEN MAX(f.source_identity) END FROM rollout_files f WHERE f.file_path=e.source_file),
                                     COALESCE(s.repository,'(unknown)'),COALESCE(s.thread_id,e.session_id)
                                    """
                                  : "") + " FROM codex_native_token_events e " +
                              (includeLedger ? "LEFT JOIN sessions s ON s.session_id=e.session_id " : "") +
                              "WHERE e.source_timestamp_quality = 'Valid' AND " +
                              (ledgerOnly ? "e.source_event_at_utc >= $from AND e.source_event_at_utc < $to "
                                  : "e.source_event_at_utc > $lookback AND e.source_event_at_utc <= $to ") +
                              "ORDER BY e.source_event_at_utc LIMIT 500001;";
        var tokens = new List<CodexPredictiveTokenEvent>();
        var ledger = new List<CodexLedgerEntry>();
        int tokenRows = 0;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (++tokenRows > 500000)
                    throw new ForecastDatasetReadLimitException("Token replay exceeds 500,000 rows; narrow the range.");
                var work = new CodexPredictiveTokenEvent(
                    Shared(reader.GetString(0)),
                    Time(reader.GetString(1)),
                    OptionalTime(reader, 2),
                    SharedText(reader, 3),
                    SharedText(reader, 4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10));
                if (!ledgerOnly) tokens.Add(work);
                if (!includeLedger || work.ObservedAtUtc < fromUtc || work.ObservedAtUtc >= toUtc) continue;
                string? identity = Text(reader, 12);
                string[] matches = RolloutAccountAssociationPolicy.Matches(
                    provider,
                    profile,
                    identity,
                    work.SessionId,
                    work.ObservedAtUtc,
                    accountAssociations ?? [],
                    captured).Select(x => x.AccountKey).Distinct().ToArray();
                ledger.Add(
                    new CodexLedgerEntry(
                        reader.GetString(11),
                        identity,
                        reader.GetString(14),
                        reader.GetString(13),
                        work.ObservedAtUtc,
                        work.CapturedAtUtc,
                        matches.Length == 1 ? matches[0] : null,
                        matches.Length == 1 ? AccountEvidenceClass.UserDeclaredSingleAccount :
                        matches.Length > 1 ? AccountEvidenceClass.Conflicting : AccountEvidenceClass.Unattributed,
                        work));
            }
        }
        var context = new List<CodexContextObservation>();
        if (!ledgerOnly)
        {
            command.CommandText = """
                                  SELECT event_id,session_id,agent_id,source_event_at_utc,model,input_tokens,context_window_tokens,is_compaction,record_bytes,captured_at_utc
                                  FROM context_observations WHERE source_timestamp_quality = 'Valid' AND source_event_at_utc > $lookback AND source_event_at_utc <= $to
                                  ORDER BY source_event_at_utc LIMIT 500001;
                                  """;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    context.Add(
                        new CodexContextObservation(
                            reader.GetString(0),
                            Shared(reader.GetString(1)),
                            SharedText(reader, 2),
                            Time(reader.GetString(3)),
                            SharedText(reader, 4),
                            Number(reader, 5),
                            Number(reader, 6),
                            reader.GetInt64(7) != 0,
                            Number(reader, 8),
                            OptionalTime(reader, 9)));
            }
            if (context.Count > 500000) throw new ForecastDatasetReadLimitException("Context replay exceeds 500,000 rows; narrow the range.");
        }
        transaction.Commit();
        measurement?.Complete(rows: quota.Count + workload.Count + tokenRows + context.Count, committedTransactions: 1);
        return new CodexForecastDataset(
            quota,
            workload,
            tokens,
            context,
            captured,
            ledgerOnly ? "Selected ledger and quota evidence captured together; predictive metadata/context intentionally omitted." :
                "Quota history includes app-server and rollout evidence; eligibility is decided by quota-history/v1. Legacy missing provenance remains unknown. Workload is local installation history, not verified account identity. Backfilled event-time reconstruction and strict collection-time replay are separate modes.")
        {
            AccountAssociations = accountAssociations ?? [], Ledger = ledger,
        };
    }
}

internal sealed class ForecastDatasetReadLimitException(string message) : InvalidOperationException(message);
