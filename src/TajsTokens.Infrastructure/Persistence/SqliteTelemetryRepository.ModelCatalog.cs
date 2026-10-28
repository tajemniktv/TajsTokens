using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    public async Task SaveModelCatalogReadAsync(CodexModelCatalogRead read, CancellationToken token)
    {
        if (!Enum.IsDefined(read.State) || read.StartedAtUtc == default || read.CompletedAtUtc < read.StartedAtUtc ||
            read.State == CodexModelCatalogReadState.Available && (read.Definition is null || read.SourceFetchedAtUtc is null) ||
            read.State != CodexModelCatalogReadState.Available && read.Definition is not null || read.Issue?.Length > 128)
            throw new ArgumentException("Invalid model catalog read.");
        string? payload = read.Definition is { } definition ? JsonSerializer.Serialize(definition) : null;
        if (payload is not null && Encoding.UTF8.GetByteCount(payload) > TtSnapshotMaxBytes) throw new InvalidOperationException("Catalog projection exceeds its storage bound.");
        await InitializeIntelligenceAsync(token);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        string at = read.CompletedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        string? fetched = read.SourceFetchedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (payload is not null)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO codex_model_catalog_definitions VALUES($id,$at,$at,$fetched,$fetched,$hash,$payload)
                ON CONFLICT(definition_id) DO UPDATE SET last_collected_at_utc=$at,last_source_fetched_at_utc=$fetched
                WHERE content_hash=$hash AND payload=$payload AND last_collected_at_utc<=$at;
                """;
            command.Parameters.AddWithValue("$id", read.Definition!.DefinitionId); command.Parameters.AddWithValue("$at", at);
            command.Parameters.AddWithValue("$fetched", (object?)fetched ?? DBNull.Value); command.Parameters.AddWithValue("$hash", TtHash(payload)); command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(token);
            using var check = connection.CreateCommand(); check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM codex_model_catalog_definitions WHERE definition_id=$id AND content_hash=$hash AND payload=$payload;";
            check.Parameters.AddWithValue("$id", read.Definition.DefinitionId); check.Parameters.AddWithValue("$hash", TtHash(payload)); check.Parameters.AddWithValue("$payload", payload);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Model catalog identity conflict; original metadata retained.");
        }
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO codex_model_catalog_read_state VALUES(1,$state,$started,$at,$fetched,$id,$issue)
            ON CONFLICT(singleton) DO UPDATE SET state=$state,started_at_utc=$started,completed_at_utc=$at,source_fetched_at_utc=$fetched,definition_id=$id,issue=$issue
            WHERE completed_at_utc<=$at;
            """;
        state.Parameters.AddWithValue("$state", (int)read.State); state.Parameters.AddWithValue("$started", read.StartedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        state.Parameters.AddWithValue("$at", at); state.Parameters.AddWithValue("$fetched", (object?)fetched ?? DBNull.Value);
        state.Parameters.AddWithValue("$id", (object?)read.Definition?.DefinitionId ?? DBNull.Value); state.Parameters.AddWithValue("$issue", (object?)read.Issue ?? DBNull.Value);
        await state.ExecuteNonQueryAsync(token); transaction.Commit();
    }

    public async Task<CodexModelCatalogHistory> ReadModelCatalogHistoryAsync(CancellationToken token)
    {
        // Product queries stay read-only, including before the first collector initialization.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token); using var transaction = connection.BeginTransaction(true);
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('codex_model_catalog_definitions','codex_model_catalog_read_state');";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 2) return CodexModelCatalogHistory.Empty;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT definition_id,first_collected_at_utc,last_collected_at_utc,first_source_fetched_at_utc,last_source_fetched_at_utc,content_hash,
                CASE WHEN length(CAST(payload AS BLOB))<=$bound THEN payload ELSE NULL END
            FROM codex_model_catalog_definitions ORDER BY last_collected_at_utc DESC,definition_id LIMIT 21;
            """;
        command.Parameters.AddWithValue("$bound", TtSnapshotMaxBytes);
        var versions = new List<CodexSavedModelCatalog>(); int invalid = 0, inspected = 0;
        using (var reader = await command.ExecuteReaderAsync(token))
        while (await reader.ReadAsync(token))
        {
            if (++inspected > 20) break;
            try
            {
                if (reader.IsDBNull(6)) { invalid++; continue; }
                string payload = reader.GetString(6);
                if (TtHash(payload) != reader.GetString(5)) { invalid++; continue; }
                var definition = JsonSerializer.Deserialize<CodexModelCatalogDefinition>(payload)!;
                using var json = JsonDocument.Parse(payload);
                if (definition.DefinitionId != reader.GetString(0) || json.RootElement.GetProperty("DefinitionId").GetString() != definition.DefinitionId)
                { invalid++; continue; }
                DateTimeOffset Time(int i) => DateTimeOffset.Parse(reader.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None);
                var first = Time(1); var last = Time(2);
                if (first > last) { invalid++; continue; }
                versions.Add(new(definition, first, last, reader.IsDBNull(3) ? null : Time(3), reader.IsDBNull(4) ? null : Time(4)));
            }
            catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or NullReferenceException or OverflowException)
            { invalid++; }
        }
        CodexModelCatalogRead? read = null;
        using var latest = connection.CreateCommand(); latest.Transaction = transaction;
        latest.CommandText = "SELECT state,started_at_utc,completed_at_utc,source_fetched_at_utc,definition_id,issue FROM codex_model_catalog_read_state WHERE singleton=1;";
        using (var reader = await latest.ExecuteReaderAsync(token))
        if (await reader.ReadAsync(token))
        {
            try
            {
                var state = (CodexModelCatalogReadState)reader.GetInt32(0);
                DateTimeOffset Time(int i) => DateTimeOffset.Parse(reader.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None);
                if (!Enum.IsDefined(state) || Time(1) > Time(2)) invalid++;
                else read = new(state, Time(1), Time(2), reader.IsDBNull(3) ? null : Time(3),
                    reader.IsDBNull(4) ? null : versions.FirstOrDefault(x => x.Definition.DefinitionId == reader.GetString(4))?.Definition,
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException) { invalid++; }
        }
        transaction.Commit();
        return new(read, Array.AsReadOnly(versions.ToArray()), inspected > 20 || invalid > 0, invalid);
    }
}
