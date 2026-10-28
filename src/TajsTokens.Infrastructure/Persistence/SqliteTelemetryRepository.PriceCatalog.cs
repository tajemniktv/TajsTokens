using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    public async Task<IReadOnlyList<ApiPriceCatalogArchiveEntry>> ReadPriceCatalogHistoryAsync(string sourceKey, int take, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 128) throw new ArgumentException("Invalid price source key.");
        var ids = new List<string>();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(token); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='api_price_catalog_snapshots';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1) return [];
            command.CommandText = "SELECT snapshot_id FROM api_price_catalog_snapshots WHERE source_key=$key ORDER BY first_imported_at_utc DESC,snapshot_id LIMIT $take;";
            command.Parameters.AddWithValue("$key", sourceKey); command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 20));
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetString(0));
        }
        var entries = new List<ApiPriceCatalogArchiveEntry>();
        foreach (string id in ids)
        {
            var source = await ReadPriceCatalogSourceAsync(sourceKey, token, id);
            entries.Add(new(id, source.FirstImportedAtUtc, source.LastGood, source.InvalidStoredSnapshot));
        }
        return Array.AsReadOnly(entries.ToArray());
    }

    public async Task SavePriceCatalogImportAsync(ApiPriceImportAttempt attempt, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(attempt.SourceKey) || attempt.SourceKey.Length > 128 || !Enum.IsDefined(attempt.State) || attempt.AttemptedAtUtc == default ||
            (attempt.State == ApiPriceImportState.Accepted) != (attempt.Snapshot is not null) || attempt.Issue?.Length > 128)
            throw new ArgumentException("Invalid price catalog import.");
        string? payload = attempt.Snapshot is { } snapshot ? JsonSerializer.Serialize(snapshot) : null;
        if (payload is not null && Encoding.UTF8.GetByteCount(payload) > TtSnapshotMaxBytes) throw new InvalidOperationException("Price snapshot exceeds the storage bound.");
        await InitializeIntelligenceAsync(token);
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        string at = attempt.AttemptedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (payload is not null)
        {
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO api_price_catalog_snapshots VALUES($key,$id,$at,$hash,$payload) ON CONFLICT(source_key,snapshot_id) DO NOTHING;";
            insert.Parameters.AddWithValue("$key", attempt.SourceKey); insert.Parameters.AddWithValue("$id", attempt.Snapshot!.SnapshotId);
            insert.Parameters.AddWithValue("$at", at); insert.Parameters.AddWithValue("$hash", TtHash(payload)); insert.Parameters.AddWithValue("$payload", payload);
            await insert.ExecuteNonQueryAsync(token);
            using var check = connection.CreateCommand(); check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM api_price_catalog_snapshots WHERE source_key=$key AND snapshot_id=$id AND content_hash=$hash AND payload=$payload;";
            check.Parameters.AddWithValue("$key", attempt.SourceKey); check.Parameters.AddWithValue("$id", attempt.Snapshot.SnapshotId);
            check.Parameters.AddWithValue("$hash", TtHash(payload)); check.Parameters.AddWithValue("$payload", payload);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Price snapshot identity conflict; original retained.");
        }
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO api_price_catalog_import_state VALUES($key,$at,$state,$id,$issue)
            ON CONFLICT(source_key) DO UPDATE SET last_attempt_at_utc=$at,state=$state,
                last_good_id=COALESCE($id,last_good_id),issue=$issue WHERE last_attempt_at_utc<=$at;
            """;
        state.Parameters.AddWithValue("$key", attempt.SourceKey); state.Parameters.AddWithValue("$at", at);
        state.Parameters.AddWithValue("$state", (int)attempt.State); state.Parameters.AddWithValue("$id", (object?)attempt.Snapshot?.SnapshotId ?? DBNull.Value);
        state.Parameters.AddWithValue("$issue", (object?)attempt.Issue ?? DBNull.Value);
        await state.ExecuteNonQueryAsync(token); transaction.Commit();
    }

    public async Task<ApiPriceCatalogSource> ReadPriceCatalogSourceAsync(string sourceKey, CancellationToken token, string? snapshotId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 128) throw new ArgumentException("Invalid price source key.");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token); using var transaction = connection.BeginTransaction(true);
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('api_price_catalog_snapshots','api_price_catalog_import_state');";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 2) return new(sourceKey, null, null, null, null, false);
        ApiPriceImportState? state = null; DateTimeOffset? attempted = null; string? id = snapshotId;
        using var metadata = connection.CreateCommand(); metadata.Transaction = transaction;
        metadata.CommandText = "SELECT state,last_attempt_at_utc,last_good_id FROM api_price_catalog_import_state WHERE source_key=$key;";
        metadata.Parameters.AddWithValue("$key", sourceKey);
        using (var reader = await metadata.ExecuteReaderAsync(token))
        if (await reader.ReadAsync(token))
        {
            try
            {
                if (!Enum.IsDefined((ApiPriceImportState)reader.GetInt32(0)) || !DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return new(sourceKey, null, null, null, null, true);
                state = (ApiPriceImportState)reader.GetInt32(0); attempted = parsed;
                if (id is null && !reader.IsDBNull(2)) id = reader.GetString(2);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
            { return new(sourceKey, null, null, null, null, true); }
        }
        if (id is null) return new(sourceKey, state, attempted, null, null, false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT first_imported_at_utc,content_hash,CASE WHEN length(CAST(payload AS BLOB))<=$bound THEN payload ELSE NULL END
            FROM api_price_catalog_snapshots WHERE source_key=$key AND snapshot_id=$id;
            """;
        command.Parameters.AddWithValue("$key", sourceKey); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$bound", TtSnapshotMaxBytes);
        using var row = await command.ExecuteReaderAsync(token);
        if (!await row.ReadAsync(token)) return new(sourceKey, state, attempted, null, null, true);
        try
        {
            if (row.IsDBNull(2) || !DateTimeOffset.TryParse(row.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var imported)) return new(sourceKey, state, attempted, null, null, true);
            string payload = row.GetString(2);
            if (TtHash(payload) != row.GetString(1)) return new(sourceKey, state, attempted, null, null, true);
            var snapshot = JsonSerializer.Deserialize<ApiPriceCatalogSnapshot>(payload)!;
            using var json = JsonDocument.Parse(payload);
            if (snapshot.SnapshotId != id || json.RootElement.GetProperty("SnapshotId").GetString() != id) return new(sourceKey, state, attempted, null, null, true);
            return new(sourceKey, state, attempted, snapshot, imported, false);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException or NullReferenceException or OverflowException)
        { return new(sourceKey, state, attempted, null, null, true); }
    }
}
