// Taj's Tokens | SqliteTelemetryRepository.TtEvaluation.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    private const int TtSnapshotMaxBytes = 512 * 1024;

    public async Task SaveTtEvaluationAsync(TtEvaluationSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!ValidTtSnapshot(snapshot))
            throw new ArgumentException("Invalid TT research snapshot.");
        string payload = JsonSerializer.Serialize(snapshot);
        if (Encoding.UTF8.GetByteCount(payload) > TtSnapshotMaxBytes)
            throw new InvalidOperationException("TT research snapshot exceeds the storage bound.");
        string hash = TtHash(payload);
        await InitializeIntelligenceAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction();
        SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
                             INSERT INTO tt_evaluation_snapshots VALUES($id,$at,$provider,$profile,1,$hash,$payload)
                             ON CONFLICT(snapshot_id) DO NOTHING;
                             """;
        insert.Parameters.AddWithValue("$id", snapshot.Id);
        insert.Parameters.AddWithValue("$at", snapshot.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$provider", snapshot.Provider);
        insert.Parameters.AddWithValue("$profile", snapshot.Profile);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$payload", payload);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        SqliteCommand existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText =
            "SELECT COUNT(*) FROM tt_evaluation_snapshots WHERE snapshot_id=$id AND format_version=1 AND content_hash=$hash AND payload=$payload AND provider=$provider AND profile=$profile";
        existing.Parameters.AddWithValue("$id", snapshot.Id);
        existing.Parameters.AddWithValue("$hash", hash);
        existing.Parameters.AddWithValue("$payload", payload);
        existing.Parameters.AddWithValue("$provider", snapshot.Provider);
        existing.Parameters.AddWithValue("$profile", snapshot.Profile);
        if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("TT snapshot identity conflict; original evidence was preserved.");
        transaction.Commit();
    }

    public async Task<IReadOnlyList<TtEvaluationArchiveEntry>> GetTtEvaluationHistoryAsync(
        string provider,
        string profile,
        int take,
        CancellationToken cancellationToken)
    {
        await InitializeIntelligenceAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
                              SELECT snapshot_id,recorded_at_utc,format_version,content_hash,
                                  CASE WHEN length(CAST(payload AS BLOB)) <= $bound THEN payload ELSE NULL END
                              FROM tt_evaluation_snapshots WHERE provider=$provider AND profile=$profile
                              ORDER BY recorded_at_utc DESC,snapshot_id DESC LIMIT $take;
                              """;
        command.Parameters.AddWithValue("$bound", TtSnapshotMaxBytes);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 20));
        var results = new List<TtEvaluationArchiveEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string id = reader.GetString(0);
            DateTimeOffset? at = DateTimeOffset.TryParse(
                reader.GetString(1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsed)
                ? parsed
                : null;
            TtEvaluationSnapshot? snapshot = null;
            string? problem = null;
            if (at is null)
            {
                problem = "invalid-save-time";
            }
            else if (reader.GetInt32(2) != 1)
            {
                problem = "unsupported-format";
            }
            else if (reader.IsDBNull(4))
            {
                problem = "oversized-payload";
            }
            else
            {
                string payload = reader.GetString(4);
                if (TtHash(payload) != reader.GetString(3))
                {
                    problem = "content-hash-mismatch";
                }
                else
                {
                    try
                    {
                        snapshot = JsonSerializer.Deserialize<TtEvaluationSnapshot>(payload);
                        if (!ValidTtSnapshot(snapshot) || snapshot!.Id != id || snapshot.RecordedAtUtc != at ||
                            snapshot.Provider != provider || snapshot.Profile != profile)
                        {
                            problem = "invalid-snapshot-metadata";
                        }
                        else
                        {
                            using JsonDocument document = JsonDocument.Parse(payload);
                            JsonElement[] saved = document.RootElement.GetProperty("Report").GetProperty("Scores").EnumerateArray()
                                .ToArray();
                            for (int i = 0; i < saved.Length; i++)
                                if (snapshot.Report.Scores[i].Basis is { } basis &&
                                    (saved[i].GetProperty("Basis").GetProperty("BasisId").GetString() != basis.BasisId ||
                                     saved[i].GetProperty("Basis").GetProperty("InputSemantics").GetString() != basis.InputSemantics))
                                    problem = "unsupported-or-mismatched-basis";
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException
                                                   or NullReferenceException or NotSupportedException)
                    {
                        problem = "invalid-snapshot-payload";
                    }
                }
            }
            results.Add(new TtEvaluationArchiveEntry(id, at, problem is null ? snapshot : null, problem));
        }
        return results;
    }

    private static string TtHash(string payload)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    // A matching checksum proves unchanged bytes, not a renderable research contract.
    // Keep malformed rows as unavailable entries so one record cannot hide valid neighbors.
    private static bool ValidTtSnapshot(TtEvaluationSnapshot? snapshot)
    {
        return snapshot is not null && Guid.TryParseExact(snapshot.Id, "N", out _) && snapshot.FromUtc < snapshot.ToUtc &&
               !string.IsNullOrWhiteSpace(snapshot.Provider) && !string.IsNullOrWhiteSpace(snapshot.Profile) &&
               snapshot.Report is { Scores: { } scores } report && scores.Count <= 512 &&
               !string.IsNullOrWhiteSpace(report.Version) && report.Methodology is not null &&
               scores.All(score => score is { Cohort: { } cohort } && double.IsFinite(score.HorizonHours) && score.HorizonHours > 0 &&
                                   !string.IsNullOrWhiteSpace(score.Status) && !string.IsNullOrWhiteSpace(cohort.Provider) &&
                                   !string.IsNullOrWhiteSpace(cohort.Profile) && !string.IsNullOrWhiteSpace(cohort.Source));
    }
}