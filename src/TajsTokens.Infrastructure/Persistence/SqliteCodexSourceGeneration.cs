// Taj's Tokens | SqliteCodexSourceGeneration.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using Microsoft.Data.Sqlite;

namespace TajsTokens.Infrastructure.Persistence;

internal static class SqliteCodexSourceGeneration
{
    public static async Task RemoveUntimedTimelineAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sourceRecordId, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM usage_events WHERE event_id = $id OR event_id = $token;";
        command.Parameters.AddWithValue("$id", sourceRecordId);
        command.Parameters.AddWithValue("$token", sourceRecordId + ":token");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Both writer paths preserve numeric evidence and retire a replaced logical path before
    // reducing its new records. Preservation and projection removal share this transaction.
    // Prefix-verified metadata upgrades retain their identity and never enter this deletion path.
    public static async Task RetirePreviousAsync(SqliteConnection connection, SqliteTransaction transaction,
        string safeFileLabel, string sourceIdentity, CancellationToken cancellationToken)
    {
        using SqliteCommand previous = connection.CreateCommand();
        previous.Transaction = transaction;
        previous.CommandText = "SELECT 1 FROM rollout_files WHERE file_path = $file AND source_identity <> $identity LIMIT 1;";
        previous.Parameters.AddWithValue("$file", safeFileLabel);
        previous.Parameters.AddWithValue("$identity", sourceIdentity);
        if (await previous.ExecuteScalarAsync(cancellationToken) is null) return;

        await SqliteCodexRetiredEvidence.PreserveAsync(connection, transaction,
            safeFileLabel, sourceIdentity, cancellationToken);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM codex_native_token_events WHERE source_file = $file OR source_event_id IN (
                SELECT source_record_id FROM rollout_records WHERE source_identity IN (
                    SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity));
            DELETE FROM codex_counter_state WHERE source_file = $file OR last_source_event_id IN (
                SELECT source_record_id FROM rollout_records WHERE source_identity IN (
                    SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity));
            DELETE FROM codex_workload_observations WHERE source_identity IN (
                SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity);
            DELETE FROM quota_snapshots WHERE source_identity IN (
                SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity);
            DELETE FROM context_observations WHERE event_id IN (
                SELECT source_record_id || ':ctx' FROM rollout_records WHERE source_identity IN (
                    SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity));
            DELETE FROM usage_events WHERE event_id IN (
                SELECT source_record_id FROM rollout_records WHERE source_identity IN (
                    SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity))
                OR event_id IN (SELECT source_record_id || ':token' FROM rollout_records
                    WHERE source_identity IN (
                        SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity));
            DELETE FROM codex_parser_state WHERE source_identity IN (
                SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity);
            DELETE FROM rollout_records WHERE source_identity IN (
                SELECT source_identity FROM rollout_files WHERE file_path = $file AND source_identity <> $identity);
            DELETE FROM rollout_files WHERE file_path = $file AND source_identity <> $identity;
            """;
        command.Parameters.AddWithValue("$file", safeFileLabel);
        command.Parameters.AddWithValue("$identity", sourceIdentity);
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS codex_native_accounting_revision (
                    id INTEGER PRIMARY KEY CHECK(id = 1), revision INTEGER NOT NULL);
                INSERT INTO codex_native_accounting_revision(id, revision) VALUES(1, 1)
                ON CONFLICT(id) DO UPDATE SET revision = revision + 1;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
