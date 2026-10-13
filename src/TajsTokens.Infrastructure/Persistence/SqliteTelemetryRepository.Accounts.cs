// Taj's Tokens | SqliteTelemetryRepository.Accounts.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    /// <summary>
    ///     Discover the source/account combinations actually retained in quota history. No current
    ///     login is projected onto older observations, and an empty legacy key remains unknown.
    /// </summary>
    public async Task<QuotaSourceAccountInventory> ReadQuotaSourceAccountsAsync(
        int take, CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(take, 1, 200);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, profile, source, account_key,
                   MIN(captured_at_utc), MAX(captured_at_utc), COUNT(*)
            FROM quota_snapshots
            GROUP BY provider, profile, source, account_key
            ORDER BY MAX(captured_at_utc) DESC, provider, profile, source, account_key
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit + 1);

        var rows = new List<QuotaSourceAccountObservation>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == limit) return new(rows, true);
            rows.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3) is { Length: > 0 } account ? account : null,
                ParseUtc(reader.GetString(4)), ParseUtc(reader.GetString(5)), reader.GetInt64(6)));
        }
        return new(rows, false);
    }
}
