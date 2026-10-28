using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Services;

namespace TajsTokens.Infrastructure.Persistence;

internal sealed record CalibrationEvidenceStamp(string StorageIdentity, long Revision, long FeatureRevision,
    DateTimeOffset? NextTimeBoundaryUtc);

/// <summary>Tracks inputs of the released forecast reader, independently of derived writes.
/// All state and triggers belong to the app database. Missing/unknown schemas disable reuse.</summary>
internal sealed class SqliteCalibrationEvidence(string databasePath)
{
    private static readonly string[] Tables =
        ["quota_snapshots", "codex_native_token_events", "codex_workload_observations", "context_observations", "rollout_files"];
    private string? _initializedIdentity;
    private long _schemaVersion = -1;
    private long _timeRevision = -1;
    private DateTimeOffset? _nextBoundary;
    private DateTimeOffset _timeReadAt;

    public async Task<CalibrationEvidenceStamp?> ReadAsync(DateTimeOffset now, CancellationToken token, bool includeTimeBoundary = true)
    {
        using var work = WorkDiagnostics.Measure(WorkPhase.CalibrationEvidence);
        string? identity = SqliteDatabaseGeneration.ReadIdentity(databasePath);
        if (identity is null) return null;
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA schema_version;";
            long schema = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
            if (_initializedIdentity != identity || schema != _schemaVersion)
            {
                if (!await EnsureAsync(connection, token).ConfigureAwait(false)) return null;
                command.CommandText = "PRAGMA schema_version;";
                _schemaVersion = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
                _initializedIdentity = identity;
                _timeRevision = -1;
            }

            // Stamp and time qualification must describe one committed generation.
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            command.Transaction = transaction;
            command.CommandText = "SELECT revision,feature_revision FROM calibration_evidence_state WHERE id=1;";
            long revision, features;
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                revision = reader.GetInt64(0); features = reader.GetInt64(1);
            }
            DateTimeOffset? boundary = _nextBoundary;
            int cacheHits = includeTimeBoundary ? 1 : -1;
            if (includeTimeBoundary && (_timeRevision != features || now < _timeReadAt || boundary <= now))
            {
                cacheHits = 0;
                boundary = await ReadNextBoundaryAsync(connection, transaction, now, token).ConfigureAwait(false);
                _timeRevision = features;
                _nextBoundary = boundary;
                _timeReadAt = now;
            }
            transaction.Commit();
            // A restored file must not inherit the old file's stamp even at equal revisions.
            if (SqliteDatabaseGeneration.ReadIdentity(databasePath) != identity) return null;
            work?.Complete(rows: 1, cacheHits: cacheHits);
            return new(identity, revision, features, includeTimeBoundary ? boundary : null);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException)
        {
            _initializedIdentity = null;
            _timeRevision = -1;
            return null;
        }
    }

    private static async Task<bool> EnsureAsync(SqliteConnection connection, CancellationToken token)
    {
        var columns = new Dictionary<string, string[]>();
        foreach (string table in Tables)
        {
            using var info = connection.CreateCommand();
            info.CommandText = $"PRAGMA table_info({table});";
            var names = new List<string>();
            await using var reader = await info.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) names.Add(reader.GetString(1));
            if (names.Count == 0) return false;
            // Only source membership affects these reads. last_seen/size/path maintenance
            // neither changes evidence nor invalidates an otherwise reusable preparation.
            columns[table] = table == "rollout_files" ? ["source_identity"] : names.ToArray();
        }
        string signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "calibration-evidence/v1;" + string.Join(';', Tables.Select(t => t + ":" + string.Join(',', columns[t]))))));
        using SqliteTransaction transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS calibration_evidence_state (
                id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL,
                feature_revision INTEGER NOT NULL, contract_signature TEXT NOT NULL);
            INSERT INTO calibration_evidence_state VALUES(1,0,0,'') ON CONFLICT(id) DO NOTHING;
            SELECT contract_signature FROM calibration_evidence_state WHERE id=1;
            """;
        string? current = (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN (" +
            string.Join(',', Tables.SelectMany(t => new[] { "insert", "update", "delete" }
                .Select(a => $"'calibration_evidence_{t}_{a}_v1'"))) + ");";
        bool complete = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) == Tables.Length * 3;
        if (current != signature || !complete)
        {
            foreach (string table in Tables)
            foreach (string action in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                string name = $"calibration_evidence_{table}_{action.ToLowerInvariant()}_v1";
                string when = action == "UPDATE" ? "WHEN " + string.Join(" OR ", columns[table].Select(c =>
                    $"OLD.\"{c.Replace("\"", "\"\"")}\" IS NOT NEW.\"{c.Replace("\"", "\"\"")}\"")) : "";
                string feature = table == "quota_snapshots" ? "" : ", feature_revision=feature_revision+1";
                command.CommandText = $"""
                    DROP TRIGGER IF EXISTS {name};
                    CREATE TRIGGER {name} AFTER {action} ON {table} {when}
                    BEGIN UPDATE calibration_evidence_state SET revision=revision+1{feature} WHERE id=1; END;
                    """;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            command.CommandText = "UPDATE calibration_evidence_state SET contract_signature=$signature,revision=revision+1,feature_revision=feature_revision+1 WHERE id=1;";
            command.Parameters.AddWithValue("$signature", signature);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
        return true;
    }

    private static async Task<DateTimeOffset?> ReadNextBoundaryAsync(SqliteConnection connection,
        SqliteTransaction transaction, DateTimeOffset now, CancellationToken token)
    {
        DateTimeOffset? next = null;
        foreach ((string Table, string Observed, string Filter) in new[]
        {
            ("codex_native_token_events", "source_event_at_utc", "WHERE source_timestamp_quality='Valid'"),
            ("codex_workload_observations", "observed_at_utc", ""),
            ("context_observations", "source_event_at_utc", "WHERE source_timestamp_quality='Valid'"),
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Time alone can admit future-dated/late-collected evidence or move the 30-day
            // workload read's two-hour lookback. Compute this only when feature evidence
            // changes or the previously known boundary is reached, not on every quota poll.
            command.CommandText = $"""
                SELECT MIN(CASE WHEN {Observed}>$now THEN {Observed} END),
                       MIN(CASE WHEN captured_at_utc>$now THEN captured_at_utc END),
                       MIN(CASE WHEN {Observed}>$from THEN {Observed} END)
                FROM {Table} {Filter};
                """;
            command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$from", now.AddDays(-30).AddHours(-2).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) continue;
            for (int i = 0; i < 3; i++)
            {
                if (reader.IsDBNull(i)) continue;
                DateTimeOffset at = DateTimeOffset.Parse(reader.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (i == 2) at = at.AddDays(30).AddHours(2);
                if (at > now && (next is null || at < next)) next = at;
            }
        }
        return next;
    }
}
