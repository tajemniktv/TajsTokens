using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

internal static class SqliteCodexWorkloadEvidence
{
    public const string Schema = """
        CREATE TABLE codex_workload_observations (
            source_record_id TEXT PRIMARY KEY,
            source_identity TEXT NOT NULL,
            source_file TEXT NOT NULL,
            start_byte_offset INTEGER NOT NULL,
            end_byte_offset INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            observed_at_utc TEXT,
            captured_at_utc TEXT NOT NULL,
            turn_id TEXT,
            root_turn_id TEXT,
            parent_thread_id TEXT,
            model TEXT,
            reasoning_effort TEXT,
            context_window_tokens INTEGER,
            contract_version TEXT NOT NULL,
            started_at_unix_seconds INTEGER,
            completed_at_unix_seconds INTEGER,
            duration_ms INTEGER,
            time_to_first_token_ms INTEGER,
            session_source_kind TEXT
        );
        CREATE INDEX idx_workload_time ON codex_workload_observations(observed_at_utc, session_id);
        CREATE INDEX idx_workload_turn ON codex_workload_observations(session_id, turn_id, observed_at_utc);
        """;

    public static async Task WriteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        CodexWorkloadObservation item, string safeFileLabel, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO codex_workload_observations(source_record_id,source_identity,source_file,
                start_byte_offset,end_byte_offset,session_id,event_type,observed_at_utc,captured_at_utc,
                turn_id,root_turn_id,parent_thread_id,model,reasoning_effort,context_window_tokens,contract_version,
                started_at_unix_seconds,completed_at_unix_seconds,duration_ms,time_to_first_token_ms,session_source_kind)
            VALUES($id,$identity,$file,$start,$end,$session,$type,$observed,$captured,$turn,$root,$parent,$model,$effort,$window,$contract,
                $nativeStart,$nativeEnd,$duration,$firstToken,$sourceKind)
            ON CONFLICT(source_record_id) DO NOTHING;
            """;
        object Value(object? value) => value ?? DBNull.Value;
        string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$id", item.SourceRecordId);
        command.Parameters.AddWithValue("$identity", item.SourceIdentity);
        command.Parameters.AddWithValue("$file", safeFileLabel);
        command.Parameters.AddWithValue("$start", item.StartByteOffset);
        command.Parameters.AddWithValue("$end", item.EndByteOffset);
        command.Parameters.AddWithValue("$session", item.SessionId);
        command.Parameters.AddWithValue("$type", item.EventType);
        command.Parameters.AddWithValue("$observed", item.ObservedAtUtc is DateTimeOffset observed ? Utc(observed) : DBNull.Value);
        command.Parameters.AddWithValue("$captured", Utc(item.CapturedAtUtc));
        command.Parameters.AddWithValue("$turn", Value(item.TurnId));
        command.Parameters.AddWithValue("$root", Value(item.RootTurnId));
        command.Parameters.AddWithValue("$parent", Value(item.ParentThreadId));
        command.Parameters.AddWithValue("$model", Value(item.Model));
        command.Parameters.AddWithValue("$effort", Value(item.ReasoningEffort));
        command.Parameters.AddWithValue("$window", Value(item.ContextWindowTokens));
        command.Parameters.AddWithValue("$contract", item.ContractVersion);
        command.Parameters.AddWithValue("$nativeStart", Value(item.StartedAtUnixSeconds));
        command.Parameters.AddWithValue("$nativeEnd", Value(item.CompletedAtUnixSeconds));
        command.Parameters.AddWithValue("$duration", Value(item.DurationMilliseconds));
        command.Parameters.AddWithValue("$firstToken", Value(item.TimeToFirstTokenMilliseconds));
        command.Parameters.AddWithValue("$sourceKind", Value(item.SessionSourceKind));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
