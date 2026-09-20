// Taj's Tokens | SqliteCodexResponseEvidence.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Persistence;

internal static class SqliteCodexResponseEvidence
{
    public const string Schema = """
                                 CREATE TABLE IF NOT EXISTS codex_response_observations (
                                     source_record_id TEXT PRIMARY KEY, source_identity TEXT NOT NULL, source_file TEXT NOT NULL,
                                     start_byte_offset INTEGER NOT NULL, end_byte_offset INTEGER NOT NULL, owner_thread_id TEXT NOT NULL,
                                     observed_at_utc TEXT, captured_at_utc TEXT NOT NULL,
                                     reported_thread_id TEXT, turn_id TEXT, root_turn_id TEXT, runtime_session_id TEXT, response_id TEXT,
                                     usage_snapshot TEXT, turn_snapshot TEXT, thread_snapshot TEXT,
                                     diagnostics TEXT NOT NULL, contract_version TEXT NOT NULL
                                 );
                                 CREATE INDEX IF NOT EXISTS idx_response_owner ON codex_response_observations(owner_thread_id, captured_at_utc, source_record_id);
                                 CREATE INDEX IF NOT EXISTS idx_response_source ON codex_response_observations(source_identity);
                                 """;

    // JSON columns encode only this closed six-counter DTO, never source JSON or extension data.
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };

    public static async Task WriteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CodexResponseObservation row,
        string safeFileLabel,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO codex_response_observations VALUES
                              ($id,$source,$file,$start,$end,$owner,$observed,$captured,$thread,$turn,$root,$runtime,$response,
                               $usage,$turnUsage,$threadUsage,$diagnostics,$contract)
                              ON CONFLICT(source_record_id) DO NOTHING;
                              """;

        object Db(object? value)
        {
            return value ?? DBNull.Value;
        }

        string? Snapshot(CodexResponseSnapshot? value)
        {
            return value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
        }

        command.Parameters.AddWithValue("$id", row.SourceRecordId);
        command.Parameters.AddWithValue("$source", row.SourceIdentity);
        command.Parameters.AddWithValue("$file", safeFileLabel);
        command.Parameters.AddWithValue("$start", row.StartByteOffset);
        command.Parameters.AddWithValue("$end", row.EndByteOffset);
        command.Parameters.AddWithValue("$owner", row.OwnerThreadId);
        command.Parameters.AddWithValue("$observed", Db(row.ObservedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$captured", row.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$thread", Db(row.ReportedThreadId));
        command.Parameters.AddWithValue("$turn", Db(row.TurnId));
        command.Parameters.AddWithValue("$root", Db(row.RootTurnId));
        command.Parameters.AddWithValue("$runtime", Db(row.RuntimeSessionId));
        command.Parameters.AddWithValue("$response", Db(row.ResponseId));
        command.Parameters.AddWithValue("$usage", Db(Snapshot(row.Usage)));
        command.Parameters.AddWithValue("$turnUsage", Db(Snapshot(row.TurnUsage)));
        command.Parameters.AddWithValue("$threadUsage", Db(Snapshot(row.ThreadUsage)));
        command.Parameters.AddWithValue("$diagnostics", row.Diagnostics);
        command.Parameters.AddWithValue("$contract", row.ContractVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<CodexResponseEvidencePage> ReadAsync(
        SqliteConnection connection,
        string threadId,
        int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 1000);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
                              SELECT r.*, EXISTS(SELECT 1 FROM rollout_files f WHERE f.source_identity=r.source_identity) AS active
                              FROM codex_response_observations r WHERE owner_thread_id=$thread
                              ORDER BY active DESC, captured_at_utc DESC, source_record_id LIMIT $take;
                              """;
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$take", take + 1);
        var rows = new List<CodexResponseEvidenceRow>();
        int scanned = 0;
        int corrupt = 0;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (++scanned > take) break;
            try
            {
                string? Text(int i)
                {
                    return reader.IsDBNull(i) ? null : reader.GetString(i);
                }

                CodexResponseSnapshot? Snapshot(int i)
                {
                    if (Text(i) is not { } text) return null;
                    var snapshot = JsonSerializer.Deserialize<CodexResponseSnapshot>(text);
                    return snapshot?.Counters is not null ? snapshot : throw new JsonException();
                }

                rows.Add(
                    new CodexResponseEvidenceRow(
                        new CodexResponseObservation(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetInt64(3),
                            reader.GetInt64(4),
                            reader.GetString(5),
                            Text(6) is { } time ? DateTimeOffset.Parse(time, CultureInfo.InvariantCulture) : null,
                            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                            Text(8),
                            Text(9),
                            Text(10),
                            Text(11),
                            Text(12),
                            Snapshot(13),
                            Snapshot(14),
                            Snapshot(15),
                            reader.GetString(16),
                            reader.GetString(17)),
                        reader.GetInt64(18) != 0));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException)
            {
                corrupt++;
            }
        }
        return new CodexResponseEvidencePage(rows, scanned > take, corrupt);
    }
}