// Taj's Tokens | SqliteTelemetryRepository.ServerEvidence.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Providers;

#endregion

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    public async Task SaveServerEvidenceAsync(CodexServerCollection collection, CancellationToken token)
    {
        if (collection.Observations.Count > 9) throw new ArgumentException("Collection exceeds the bounded surface/thread batch.");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (CodexServerObservation row in collection.Observations)
        {
            if (row.ContractVersion != CodexServerEvidenceParser.Contract || row.FetchStartedAtUtc > row.CollectedAtUtc)
                throw new ArgumentException("Invalid server evidence provenance.");
            if (JsonSerializer.Serialize(row).Length > 2 * 1024 * 1024) throw new ArgumentException("Evidence exceeds storage bound.");
            (string json, long? setId) = await CodexServerEvidenceStorage.PackAsync(connection, transaction, row, token);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                  INSERT INTO codex_server_evidence(observation_id,surface,thread_id,correlated_account_key,
                                      fetch_started_at_utc,collected_at_utc,contract_version,state,evidence_json,activity_bucket_set_id)
                                  VALUES($id,$surface,$thread,$account,$started,$collected,$contract,$state,$json,$set)
                                  ON CONFLICT(observation_id) DO NOTHING;
                                  """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$surface", row.Surface.ToString());
            command.Parameters.AddWithValue("$thread", row.ThreadId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$account", row.CorrelatedAccountKey ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$started",
                row.FetchStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$collected", row.CollectedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$contract", row.ContractVersion);
            command.Parameters.AddWithValue("$state", row.State.ToString());
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$set", setId ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(token);
            // An identical retry is idempotent; a conflicting reuse of identity is not a silent rewrite.
            command.CommandText = "SELECT evidence_json FROM codex_server_evidence WHERE observation_id=$id;";
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), json, StringComparison.Ordinal))
                throw new InvalidOperationException("Server observation identity collision; transaction rolled back.");
            command.CommandText = "SELECT activity_bucket_set_id FROM codex_server_evidence WHERE observation_id=$id;";
            object? savedSet = await command.ExecuteScalarAsync(token);
            if ((savedSet is DBNull ? null : (long?)savedSet) != setId)
                throw new InvalidOperationException("Server observation bucket identity collision; transaction rolled back.");
        }
        transaction.Commit();
    }
}