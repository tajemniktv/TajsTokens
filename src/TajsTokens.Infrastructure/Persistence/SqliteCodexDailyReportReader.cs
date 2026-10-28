// Taj's Tokens | SqliteCodexDailyReportReader.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Read-only daily projection of the existing canonical native token-event ledger.</summary>
public sealed class SqliteCodexDailyReportReader(string databasePath)
{
    public async Task<CodexDailyTokenReport> ReadAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(366))
            throw new ArgumentOutOfRangeException(nameof(fromUtc), "Choose a non-empty range of at most 366 days.");
        if (!File.Exists(databasePath)) throw new FileNotFoundException("TajsTokens telemetry database not found.", databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM observatory_schema WHERE component='codex-observatory';";
        int version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version < 9 || version > SqliteCodexObservatoryStore.ObservatorySchemaVersion)
            throw new InvalidOperationException($"Daily reporting requires supported source-time metadata (observatory schema 9–{SqliteCodexObservatoryStore.ObservatorySchemaVersion}); found {version}. Use an app supporting this schema before reporting.");
        command.CommandText = """
            SELECT substr(source_event_at_utc, 1, 10) AS day_utc, COUNT(*),
                   SUM(uncached_input_tokens), SUM(cache_read_tokens), SUM(cache_write_tokens),
                   SUM(non_reasoning_output_tokens), SUM(reasoning_output_tokens),
                   SUM(reported_total_tokens)
            FROM codex_native_token_events
            WHERE source_timestamp_quality='Valid' AND source_event_at_utc >= $from AND source_event_at_utc < $to
            GROUP BY day_utc ORDER BY day_utc;
            """;
        command.Parameters.AddWithValue("$from", fromUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toUtc.ToString("O", CultureInfo.InvariantCulture));

        var days = new List<CodexDailyTokenRow>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                days.Add(new(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
        }
        command.Parameters.Clear();
        command.CommandText = $"SELECT {SqliteCodexNativeTokenCoverage.Columns} FROM codex_native_token_events;";
        CodexNativeTokenCoverage coverage;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            coverage = SqliteCodexNativeTokenCoverage.Read(reader, 0);
        }
        transaction.Commit();
        return new("tajstokens.daily-tokens.v2", DateTimeOffset.UtcNow, fromUtc, toUtc,
            "selected-local-native-increments; verified-source-time-only; missing days are unobserved, not confirmed zero; allTimeCoverage is outside the dated range",
            "unknown; not attributed to the current Codex login", days) { AllTimeCoverage = coverage };
    }
}
