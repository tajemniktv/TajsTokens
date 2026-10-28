using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    public async Task SaveWorkloadReferenceSelectionAsync(WorkloadReferenceSelection selection, CancellationToken token, bool onlyIfMissing = false,
        string? expectedPreviousId = null)
    {
        if (!selection.IsValid) throw new ArgumentException("Invalid reference selection.");
        string payload = JsonSerializer.Serialize(selection);
        if (Encoding.UTF8.GetByteCount(payload) > TtSnapshotMaxBytes) throw new ArgumentException("Selection exceeds storage bound.");
        await InitializeIntelligenceAsync(token);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        if (expectedPreviousId is not null)
        {
            command.CommandText = "SELECT selection_id FROM workload_reference_selections ORDER BY rowid DESC LIMIT 1;";
            if (await command.ExecuteScalarAsync(token) as string != expectedPreviousId)
                throw new InvalidOperationException("Reference selection changed concurrently; retry the explicit action.");
        }
        if (onlyIfMissing)
        {
            command.CommandText = "SELECT COUNT(*) FROM workload_reference_selections;";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0) return;
        }
        command.CommandText = "INSERT INTO workload_reference_selections VALUES($id,$at,$hash,$payload) ON CONFLICT(selection_id) DO NOTHING;";
        command.Parameters.AddWithValue("$id", selection.SelectionId);
        command.Parameters.AddWithValue("$at", selection.SelectedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hash", TtHash(payload)); command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT COUNT(*) FROM workload_reference_selections WHERE selection_id=$id AND content_hash=$hash AND payload=$payload;";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Reference selection identity conflict; original retained.");
        transaction.Commit();
    }

    public async Task<WorkloadReferenceState> ReadWorkloadReferenceSelectionAsync(CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workload_reference_selections';";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0) return new(null, null);
        command.CommandText = "SELECT selection_id,selected_at_utc,content_hash,CASE WHEN length(CAST(payload AS BLOB))<=$bound THEN payload ELSE NULL END FROM workload_reference_selections ORDER BY rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$bound", TtSnapshotMaxBytes);
        using var row = await command.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) return new(null, null);
        try
        {
            if (row.IsDBNull(3)) return new(null, null, Issue: "invalid-reference-selection");
            string payload = row.GetString(3);
            if (TtHash(payload) != row.GetString(2)) return new(null, null, Issue: "invalid-reference-selection");
            var selection = JsonSerializer.Deserialize<WorkloadReferenceSelection>(payload)!;
            using var json = JsonDocument.Parse(payload);
            var basis = json.RootElement.GetProperty("Basis");
            if (!selection.IsValid || selection.SelectionId != row.GetString(0) ||
                selection.SelectedAtUtc != DateTimeOffset.Parse(row.GetString(1), CultureInfo.InvariantCulture) ||
                json.RootElement.GetProperty("SelectionId").GetString() != selection.SelectionId ||
                basis.GetProperty("BasisId").GetString() != selection.Basis.BasisId ||
                basis.GetProperty("ReferenceScore").GetDecimal() != selection.Basis.ReferenceScore)
                return new(null, null, Issue: "invalid-reference-selection");
            return new(selection, null);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException or NullReferenceException or OverflowException)
        { return new(null, null, Issue: "invalid-reference-selection"); }
    }
}
