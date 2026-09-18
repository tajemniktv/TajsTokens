using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Lossless physical sharing only. Account scope and observation time remain on each fetch.</summary>
internal static class CodexServerEvidenceStorage
{
    internal static async Task MigrateAsync(SqliteConnection connection, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS codex_account_day_values (
                id INTEGER PRIMARY KEY, start_date TEXT NOT NULL, tokens INTEGER NOT NULL,
                UNIQUE(start_date,tokens));
            CREATE TABLE IF NOT EXISTS codex_account_bucket_sets (id INTEGER PRIMARY KEY, fingerprint TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS codex_account_bucket_members (
                set_id INTEGER NOT NULL REFERENCES codex_account_bucket_sets(id), ordinal INTEGER NOT NULL,
                day_id INTEGER NOT NULL REFERENCES codex_account_day_values(id),
                PRIMARY KEY(set_id,ordinal));
            ALTER TABLE codex_server_evidence ADD COLUMN activity_bucket_set_id INTEGER REFERENCES codex_account_bucket_sets(id);
            """;
        await command.ExecuteNonQueryAsync(token);
        // Page through legacy fetches without holding the entire history in memory.
        long cursor = 0;
        while (true)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT rowid,evidence_json FROM codex_server_evidence WHERE rowid>$cursor ORDER BY rowid LIMIT 128;";
            command.Parameters.AddWithValue("$cursor", cursor);
            var batch = new List<(long Id, string Json)>();
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) batch.Add((reader.GetInt64(0), reader.GetString(1)));
            if (batch.Count == 0) break;
            foreach (var (id, json) in batch)
            {
                var row = JsonSerializer.Deserialize<CodexServerObservation>(json) ?? throw new InvalidDataException("Invalid server evidence.");
                var packed = await PackAsync(connection, transaction, row, token);
                command.Parameters.Clear();
                command.CommandText = "UPDATE codex_server_evidence SET evidence_json=$json,activity_bucket_set_id=$set WHERE rowid=$id;";
                command.Parameters.AddWithValue("$json", packed.Json);
                command.Parameters.AddWithValue("$set", packed.SetId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(token);
                cursor = id;
            }
        }
        command.CommandText = "PRAGMA user_version=14;";
        command.Parameters.Clear();
        await command.ExecuteNonQueryAsync(token);
        transaction.Commit();
    }

    internal static async Task<(string Json, long? SetId)> PackAsync(SqliteConnection connection,
        SqliteTransaction transaction, CodexServerObservation row, CancellationToken token)
    {
        var days = row.Activity?.DailyUsageBuckets;
        if (days is null) return (JsonSerializer.Serialize(row), null);
        if (days.Count > 4000) throw new ArgumentException("Account bucket bound exceeded.");
        // Include ordering and empty membership. Null has no set; [] has an empty set.
        var serialized = JsonSerializer.Serialize(days);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO codex_account_bucket_sets(fingerprint) VALUES($hash) ON CONFLICT DO NOTHING;";
        command.Parameters.AddWithValue("$hash", fingerprint);
        var inserted = await command.ExecuteNonQueryAsync(token) != 0;
        command.CommandText = "SELECT id FROM codex_account_bucket_sets WHERE fingerprint=$hash;";
        var setId = (long)(await command.ExecuteScalarAsync(token))!;
        if (inserted)
        {
            for (var i = 0; i < days.Count; i++)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO codex_account_day_values(start_date,tokens) VALUES($date,$tokens) ON CONFLICT DO NOTHING;
                    INSERT INTO codex_account_bucket_members(set_id,ordinal,day_id)
                    SELECT $set,$ordinal,id FROM codex_account_day_values WHERE start_date=$date AND tokens=$tokens;
                    """;
                command.Parameters.AddWithValue("$date", days[i].StartDate);
                command.Parameters.AddWithValue("$tokens", days[i].Tokens);
                command.Parameters.AddWithValue("$set", setId);
                command.Parameters.AddWithValue("$ordinal", i);
                await command.ExecuteNonQueryAsync(token);
            }
        }
        return (JsonSerializer.Serialize(row with { Activity = row.Activity! with { DailyUsageBuckets = null } }), setId);
    }

    internal static async Task<IReadOnlyList<CodexAccountDay>> ReadDaysAsync(SqliteConnection connection,
        SqliteTransaction transaction, long setId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT d.start_date,d.tokens FROM codex_account_bucket_members m
            JOIN codex_account_day_values d ON d.id=m.day_id
            WHERE m.set_id=$set ORDER BY m.ordinal LIMIT 4001;
            """;
        command.Parameters.AddWithValue("$set", setId);
        var days = new List<CodexAccountDay>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) days.Add(new(reader.GetString(0), reader.GetInt64(1)));
        if (days.Count > 4000) throw new InvalidDataException("Stored account bucket bound exceeded.");
        return days;
    }
}
