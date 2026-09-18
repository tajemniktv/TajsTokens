using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    public async Task SaveServerEvidenceAsync(CodexServerCollection collection, CancellationToken token)
    {
        if (collection.Observations.Count > 9) throw new ArgumentException("Collection exceeds the bounded surface/thread batch.");
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        foreach (var row in collection.Observations)
        {
            if (row.ContractVersion != CodexServerEvidenceParser.Contract || row.FetchStartedAtUtc > row.CollectedAtUtc)
                throw new ArgumentException("Invalid server evidence provenance.");
            var json = JsonSerializer.Serialize(row);
            if (json.Length > 2 * 1024 * 1024) throw new ArgumentException("Evidence exceeds storage bound.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO codex_server_evidence(observation_id,surface,thread_id,correlated_account_key,
                    fetch_started_at_utc,collected_at_utc,contract_version,state,evidence_json)
                VALUES($id,$surface,$thread,$account,$started,$collected,$contract,$state,$json)
                ON CONFLICT(observation_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$surface", row.Surface.ToString());
            command.Parameters.AddWithValue("$thread", row.ThreadId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$account", row.CorrelatedAccountKey ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$started", row.FetchStartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$collected", row.CollectedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$contract", row.ContractVersion);
            command.Parameters.AddWithValue("$state", row.State.ToString());
            command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync(token);
            // An identical retry is idempotent; a conflicting reuse of identity is not a silent rewrite.
            command.CommandText = "SELECT evidence_json FROM codex_server_evidence WHERE observation_id=$id;";
            if (!string.Equals((string?)await command.ExecuteScalarAsync(token), json, StringComparison.Ordinal))
                throw new InvalidOperationException("Server observation identity collision; transaction rolled back.");
        }
        transaction.Commit();
    }
}
