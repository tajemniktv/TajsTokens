using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>One read transaction over durable, content-free evidence. Never queries raw Codex payloads.</summary>
public sealed class SqliteForecastDatasetReader(string databasePath)
{
    public async Task<CodexForecastDataset> ReadAsync(string provider, string profile,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken, string? accountKey = null,
        bool includeQuota = true)
    {
        if (fromUtc >= toUtc) throw new ArgumentException("A non-empty chronological range is required.");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var captured = DateTimeOffset.UtcNow;
        string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        long? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        DateTimeOffset? OptionalTime(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Time(r.GetString(i));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name = 'account_key';";
        var hasAccountKey = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('quota_snapshots') WHERE name = 'observation_id';";
        var hasProvenance = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
        command.Parameters.AddWithValue("$from", Utc(fromUtc));
        command.Parameters.AddWithValue("$lookback", Utc(fromUtc.AddHours(-2)));
        command.Parameters.AddWithValue("$to", Utc(toUtc));
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$account", accountKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$includeQuota", includeQuota ? 1 : 0);
        command.CommandText = $"""
            SELECT kind,captured_at_utc,used_percent,window_minutes,resets_at_utc,source,{(hasAccountKey ? "account_key" : "''")},
                {(hasProvenance ? "observation_id,source_identity,session_id,limit_id,plan_type,lane,collected_at_utc,has_source_timestamp" : "NULL,NULL,NULL,NULL,NULL,NULL,NULL,1")}
            FROM quota_snapshots WHERE provider=$provider AND profile=$profile AND $includeQuota=1
              AND captured_at_utc >= $from AND captured_at_utc <= $to
              AND (source LIKE 'codex-app-server:%' OR source LIKE 'codex-rollout:%')
              {(hasProvenance ? "AND (source_identity IS NULL OR EXISTS(SELECT 1 FROM rollout_files f WHERE f.source_identity=quota_snapshots.source_identity))" : "")}
              AND ($account IS NULL OR {(hasAccountKey ? "account_key=$account" : "0")})
            ORDER BY captured_at_utc LIMIT 500001;
            """;
        var quota = new List<QuotaSnapshot>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                quota.Add(new QuotaSnapshot(Enum.Parse<QuotaWindowKind>(reader.GetString(0)), Time(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), OptionalTime(reader, 4), provider, profile, reader.GetString(5),
                    reader.GetString(6) is { Length: > 0 } account ? account : null)
                {
                    ObservationId = Text(reader, 7) is { Length: > 0 } id ? id : null,
                    SourceIdentity = Text(reader, 8), SessionId = Text(reader, 9), LimitId = Text(reader, 10),
                    PlanType = Text(reader, 11), Lane = Text(reader, 12), CollectedAtUtc = OptionalTime(reader, 13),
                    HasSourceTimestamp = reader.GetInt32(14) != 0
                });
        if (quota.Count > 500000) throw new InvalidOperationException("Quota evaluation range exceeds the 500,000-row bound; narrow the range.");
        command.CommandText = """
            SELECT w.source_record_id,w.source_identity,w.source_file,w.start_byte_offset,w.end_byte_offset,
                   w.session_id,w.event_type,w.observed_at_utc,w.captured_at_utc,w.turn_id,w.root_turn_id,
                   w.parent_thread_id,w.model,w.reasoning_effort,w.context_window_tokens,w.contract_version,
                   w.started_at_unix_seconds,w.completed_at_unix_seconds,w.duration_ms,w.time_to_first_token_ms,w.session_source_kind
            FROM codex_workload_observations w
            WHERE w.observed_at_utc <= $to
              AND EXISTS(SELECT 1 FROM rollout_files f WHERE f.source_identity=w.source_identity)
            ORDER BY w.observed_at_utc,w.start_byte_offset LIMIT 100001;
            """;
        var workload = new List<CodexWorkloadObservation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                workload.Add(new CodexWorkloadObservation(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetString(5), reader.GetString(6), OptionalTime(reader, 7), Time(reader.GetString(8)), Text(reader, 9), Text(reader, 10), Text(reader, 11),
                    Text(reader, 12), Text(reader, 13), Number(reader, 14), reader.GetString(15), Number(reader, 16), Number(reader, 17), Number(reader, 18), Number(reader, 19), Text(reader, 20)));
        if (workload.Count > 100000) throw new InvalidOperationException("Workload metadata exceeds the supported replay bound; a paged evidence read is required.");
        command.CommandText = """
            SELECT session_id,observed_at_utc,captured_at_utc,model,reasoning_effort,uncached_input_tokens,
                   cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,reasoning_output_tokens,reported_total_tokens
            FROM codex_native_token_events WHERE observed_at_utc > $lookback AND observed_at_utc <= $to
            ORDER BY observed_at_utc LIMIT 500001;
            """;
        var tokens = new List<CodexPredictiveTokenEvent>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                tokens.Add(new CodexPredictiveTokenEvent(reader.GetString(0), Time(reader.GetString(1)), OptionalTime(reader, 2), Text(reader, 3), Text(reader, 4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10)));
        if (tokens.Count > 500000) throw new InvalidOperationException("Token replay exceeds 500,000 rows; narrow the range.");
        command.CommandText = """
            SELECT event_id,session_id,agent_id,observed_at_utc,model,input_tokens,context_window_tokens,is_compaction,record_bytes,captured_at_utc
            FROM context_observations WHERE observed_at_utc > $lookback AND observed_at_utc <= $to
            ORDER BY observed_at_utc LIMIT 500001;
            """;
        var context = new List<CodexContextObservation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                context.Add(new CodexContextObservation(reader.GetString(0), reader.GetString(1), Text(reader, 2), Time(reader.GetString(3)), Text(reader, 4),
                    Number(reader, 5), Number(reader, 6), reader.GetInt64(7) != 0, Number(reader, 8), OptionalTime(reader, 9)));
        if (context.Count > 500000) throw new InvalidOperationException("Context replay exceeds 500,000 rows; narrow the range.");
        transaction.Commit();
        return new CodexForecastDataset(quota, workload, tokens, context, captured,
            "Quota history includes app-server and rollout evidence; eligibility is decided by quota-history/v1. Legacy missing provenance remains unknown. Workload is local installation history, not verified account identity. Backfilled event-time reconstruction and strict collection-time replay are separate modes.");
    }
}
