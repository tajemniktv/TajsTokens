using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    // Matches the pre-existing 512-row partition exactly, including nullable provenance.
    // A registry plus expression index permits bounded seeks instead of ranking retained history.
    private const string ResetStreamKey =
        "json_array(provider,profile,kind,source,account_key,limit_id,plan_type,session_id,window_minutes)";
    private const string ResetColumns = """
        kind,captured_at_utc,used_percent,window_minutes,resets_at_utc,provider,profile,source,account_key,
        observation_id,source_identity,session_id,limit_id,plan_type,lane,collected_at_utc,has_source_timestamp
        """;

    private async Task InitializeResetTailsAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            ALTER TABLE quota_reset_cache_state ADD COLUMN pending_rows INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE quota_reset_cache_state ADD COLUMN last_event_at_utc TEXT;
            ALTER TABLE quota_reset_cache_state ADD COLUMN next_future_at_utc TEXT;
            ALTER TABLE quota_reset_cache_state ADD COLUMN refreshed_at_utc TEXT;
            CREATE TABLE quota_reset_streams(stream_key TEXT PRIMARY KEY);
            CREATE INDEX idx_quota_snapshots_reset_tail ON quota_snapshots({{ResetStreamKey}},captured_at_utc DESC);
            CREATE TRIGGER invalidate_reset_cache_after_quota_insert AFTER INSERT ON quota_snapshots
            WHEN (SELECT needs_rebuild FROM quota_reset_cache_state WHERE singleton=1)=0
            BEGIN
                INSERT OR IGNORE INTO quota_reset_streams VALUES(
                    json_array(NEW.provider,NEW.profile,NEW.kind,NEW.source,NEW.account_key,
                               NEW.limit_id,NEW.plan_type,NEW.session_id,NEW.window_minutes));
                UPDATE quota_reset_cache_state SET pending_rows=pending_rows+1,
                    needs_rebuild=CASE WHEN pending_rows>=511 OR
                        julianday(NEW.captured_at_utc)<=julianday(last_event_at_utc) THEN 1 ELSE 0 END
                    WHERE singleton=1;
                DELETE FROM quota_reset_events WHERE
                    (SELECT needs_rebuild FROM quota_reset_cache_state WHERE singleton=1)=1;
            END;
            CREATE TRIGGER invalidate_reset_cache_after_quota_update AFTER UPDATE ON quota_snapshots
            BEGIN
                DELETE FROM quota_reset_events;
                UPDATE quota_reset_cache_state SET needs_rebuild=1 WHERE singleton=1;
            END;
            UPDATE quota_reset_cache_state SET needs_rebuild=1 WHERE singleton=1;
            """;
        await command.ExecuteNonQueryAsync(token);
        await RefreshResetEventsAsync(connection, transaction, true, _timeProvider.GetUtcNow(), token);
        command.CommandText = "UPDATE intelligence_schema SET version=7 WHERE component='phase4-intelligence';";
        await command.ExecuteNonQueryAsync(token);
        transaction.Commit();
    }

    public async Task<int> RefreshQuotaResetEventsAsync(CancellationToken cancellationToken)
    {
        await InitializeIntelligenceAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        // One writer transaction: acquisition cannot race evidence reads and derived cache writes.
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            SELECT needs_rebuild,pending_rows,next_future_at_utc,refreshed_at_utc
            FROM quota_reset_cache_state WHERE singleton=1;
            """;
        bool rebuild;
        int pending;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using (SqliteDataReader reader = await state.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Reset cache state is missing.");
            rebuild = reader.GetInt32(0) != 0 ||
                      !reader.IsDBNull(2) && ParseUtc(reader.GetString(2)) <= now ||
                      !reader.IsDBNull(3) && ParseUtc(reader.GetString(3)) > now;
            pending = reader.GetInt32(1);
        }
        if (!rebuild && pending == 0) return 0;
        int count = await RefreshResetEventsAsync(connection, transaction, rebuild, now, cancellationToken);
        transaction.Commit();
        return count;
    }

    private static async Task<int> RefreshResetEventsAsync(
        SqliteConnection connection, SqliteTransaction transaction, bool rebuild,
        DateTimeOffset now, CancellationToken token)
    {
        var rows = new List<QuotaSnapshot>();
        using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        if (rebuild)
        {
            read.CommandText = $"SELECT {ResetColumns} FROM quota_snapshots ORDER BY captured_at_utc;";
            await ReadRowsAsync();
        }
        else
        {
            using SqliteCommand streams = connection.CreateCommand();
            streams.Transaction = transaction;
            streams.CommandText = "SELECT stream_key FROM quota_reset_streams;";
            var keys = new List<string>();
            await using (SqliteDataReader reader = await streams.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) keys.Add(reader.GetString(0));
            read.CommandText = $"""
                SELECT {ResetColumns} FROM quota_snapshots WHERE {ResetStreamKey}=$stream
                AND kind IN ('FiveHour','Weekly') ORDER BY captured_at_utc DESC LIMIT 512;
                """;
            SqliteParameter key = read.Parameters.Add("$stream", SqliteType.Text);
            foreach (string stream in keys)
            {
                key.Value = stream;
                await ReadRowsAsync();
            }
            // Preserve the original ranked query's chronological eligibility/conflict handling.
            rows.Sort((left, right) => left.CapturedAtUtc.CompareTo(right.CapturedAtUtc));
        }

        using SqliteCommand write = connection.CreateCommand();
        write.Transaction = transaction;
        if (rebuild)
        {
            write.CommandText = $$"""
                DELETE FROM quota_reset_events;
                DELETE FROM quota_reset_streams;
                INSERT INTO quota_reset_streams SELECT DISTINCT {{ResetStreamKey}} FROM quota_snapshots;
                """;
            await write.ExecuteNonQueryAsync(token);
        }
        QuotaSnapshot[] eligible = QuotaHistoryPolicy.ReplayRows(
            QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(rows, now)).SelectMany(x => x));
        int count = 0;
        foreach (QuotaResetEvent reset in new QuotaResetDetector().Detect(eligible))
        {
            token.ThrowIfCancellationRequested();
            count += await WriteQuotaResetEventAsync(connection, transaction, reset, token);
        }

        DateTimeOffset? latest = rows.Where(row => row.CapturedAtUtc <= now)
            .Select(row => (DateTimeOffset?)row.CapturedAtUtc).Max();
        DateTimeOffset? future = rows.Where(row => row.CapturedAtUtc > now)
            .Select(row => (DateTimeOffset?)row.CapturedAtUtc).Min();
        write.CommandText = """
            UPDATE quota_reset_cache_state SET needs_rebuild=0,pending_rows=0,
                last_event_at_utc=CASE WHEN $rebuild OR last_event_at_utc IS NULL OR
                    julianday($last)>julianday(last_event_at_utc) THEN $last ELSE last_event_at_utc END,
                next_future_at_utc=CASE WHEN $rebuild OR next_future_at_utc IS NULL OR
                    julianday($future)<julianday(next_future_at_utc) THEN $future ELSE next_future_at_utc END,
                refreshed_at_utc=$now WHERE singleton=1;
            """;
        write.Parameters.AddWithValue("$rebuild", rebuild);
        write.Parameters.AddWithValue("$last", latest is { } at ? SerializeUtc(at) : DBNull.Value);
        write.Parameters.AddWithValue("$future", future is { } next ? SerializeUtc(next) : DBNull.Value);
        write.Parameters.AddWithValue("$now", SerializeUtc(now));
        await write.ExecuteNonQueryAsync(token);
        return count;

        async Task ReadRowsAsync()
        {
            await using SqliteDataReader reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(ReadQuotaSnapshot(reader));
        }
    }
}
