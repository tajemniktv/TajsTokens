using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    public async Task SaveTtJointScoreAsync(TtJointScoreSnapshot snapshot, CancellationToken token)
    {
        if (!ValidJointSnapshot(snapshot)) throw new ArgumentException("Invalid joint TT score snapshot.");
        string payload = JsonSerializer.Serialize(snapshot);
        if (Encoding.UTF8.GetByteCount(payload) > TtSnapshotMaxBytes)
            throw new InvalidOperationException("Joint TT snapshot exceeds the storage bound.");
        string hash = TtHash(payload);
        await InitializeIntelligenceAsync(token);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        if (snapshot.RestatesSnapshotId is { } restates)
        {
            using var parent = connection.CreateCommand(); parent.Transaction = transaction;
            parent.CommandText = "SELECT content_hash,payload FROM tt_joint_score_snapshots WHERE snapshot_id=$id AND provider=$provider AND profile=$profile AND format_version=1 AND length(CAST(payload AS BLOB))<=$bound;";
            parent.Parameters.AddWithValue("$id", restates); parent.Parameters.AddWithValue("$provider", snapshot.Provider); parent.Parameters.AddWithValue("$profile", snapshot.Profile);
            parent.Parameters.AddWithValue("$bound", TtSnapshotMaxBytes);
            using var reader = await parent.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || !TryJointSnapshot(reader.GetString(1), reader.GetString(0), out var original) ||
                original!.Id != restates || original.Provider != snapshot.Provider || original.Profile != snapshot.Profile ||
                original.FromUtc != snapshot.FromUtc || original.ToUtc != snapshot.ToUtc || original.DatasetCapturedAtUtc != snapshot.DatasetCapturedAtUtc ||
                original.RecordedAtUtc > snapshot.RecordedAtUtc || original.Selection != snapshot.Selection || !original.Workload.Equals(snapshot.Workload))
                throw new InvalidOperationException("A restatement must link an intact prior score of the same captured workload and scope.");
        }
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tt_joint_score_snapshots VALUES($id,$at,$provider,$profile,1,$hash,$payload)
            ON CONFLICT(snapshot_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$id", snapshot.Id); insert.Parameters.AddWithValue("$at", snapshot.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$provider", snapshot.Provider); insert.Parameters.AddWithValue("$profile", snapshot.Profile);
        insert.Parameters.AddWithValue("$hash", hash); insert.Parameters.AddWithValue("$payload", payload);
        await insert.ExecuteNonQueryAsync(token);
        using var check = connection.CreateCommand(); check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM tt_joint_score_snapshots WHERE snapshot_id=$id AND provider=$provider AND profile=$profile AND format_version=1 AND content_hash=$hash AND payload=$payload;";
        check.Parameters.AddWithValue("$id", snapshot.Id); check.Parameters.AddWithValue("$provider", snapshot.Provider); check.Parameters.AddWithValue("$profile", snapshot.Profile);
        check.Parameters.AddWithValue("$hash", hash); check.Parameters.AddWithValue("$payload", payload);
        if (Convert.ToInt64(await check.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Joint TT snapshot identity conflict; original output was preserved.");
        transaction.Commit();
    }

    public async Task<IReadOnlyList<TtJointScoreArchiveEntry>> GetTtJointScoreHistoryAsync(string provider, string profile, int take, CancellationToken token)
    {
        await InitializeIntelligenceAsync(token);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id,recorded_at_utc,format_version,content_hash,
                CASE WHEN length(CAST(payload AS BLOB)) <= $bound THEN payload ELSE NULL END
            FROM tt_joint_score_snapshots WHERE provider=$provider AND profile=$profile
            ORDER BY recorded_at_utc DESC,snapshot_id DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$bound", TtSnapshotMaxBytes); command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 20));
        using var reader = await command.ExecuteReaderAsync(token);
        var results = new List<TtJointScoreArchiveEntry>();
        while (await reader.ReadAsync(token))
        {
            string id = reader.GetString(0);
            DateTimeOffset? at = DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            TtJointScoreSnapshot? snapshot = null;
            string? problem = at is null ? "invalid-save-time" : reader.GetInt32(2) != 1 ? "unsupported-format" : reader.IsDBNull(4) ? "oversized-payload" : null;
            if (problem is null)
            {
                string payload = reader.GetString(4);
                if (!TryJointSnapshot(payload, reader.GetString(3), out snapshot)) problem = "invalid-or-mismatched-payload";
                else if (snapshot!.Id != id || snapshot.RecordedAtUtc != at || snapshot.Provider != provider || snapshot.Profile != profile)
                    problem = "invalid-snapshot-metadata";
            }
            results.Add(new(id, at, problem is null ? snapshot : null, problem));
        }
        return results;
    }

    private static bool TryJointSnapshot(string payload, string hash, out TtJointScoreSnapshot? snapshot)
    {
        snapshot = null;
        if (Encoding.UTF8.GetByteCount(payload) > TtSnapshotMaxBytes || TtHash(payload) != hash) return false;
        try
        {
            snapshot = JsonSerializer.Deserialize<TtJointScoreSnapshot>(payload);
            if (!ValidJointSnapshot(snapshot)) return false;
            using var document = JsonDocument.Parse(payload);
            // Recomputed identity must match the saved identity, even when an edited payload has a fresh checksum.
            return document.RootElement.GetProperty("Basis").GetProperty("BasisId").GetString() == snapshot!.Basis.BasisId &&
                document.RootElement.GetProperty("Basis").GetProperty("ReferenceScore").GetDecimal() == snapshot.Basis.ReferenceScore;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException or OverflowException or FormatException)
        { snapshot = null; return false; }
    }

    private static bool ValidJointSnapshot(TtJointScoreSnapshot? snapshot)
    {
        try
        {
            return snapshot is { Contract: TtJointScoreSnapshot.Version, Basis: not null, Workload: not null } &&
                !string.IsNullOrWhiteSpace(snapshot.Id) && snapshot.Id.Length <= 128 &&
                !string.IsNullOrWhiteSpace(snapshot.Provider) && snapshot.Provider.Length <= 128 &&
                !string.IsNullOrWhiteSpace(snapshot.Profile) && snapshot.Profile.Length <= 128 &&
                snapshot.RecordedAtUtc != default && snapshot.DatasetCapturedAtUtc != default && snapshot.DatasetCapturedAtUtc <= snapshot.RecordedAtUtc &&
                snapshot.FromUtc < snapshot.ToUtc && snapshot.ToUtc - snapshot.FromUtc <= TimeSpan.FromDays(366) &&
                (snapshot.Selection is null || snapshot.Selection.FromUtc == snapshot.FromUtc && snapshot.Selection.ToUtc == snapshot.ToUtc) &&
                snapshot.Workload.Kind == CodexJointQuantityKind.ObservedIncrements &&
                (snapshot.RestatesSnapshotId is null || !string.IsNullOrWhiteSpace(snapshot.RestatesSnapshotId) && snapshot.RestatesSnapshotId.Length <= 128 && snapshot.RestatesSnapshotId != snapshot.Id) &&
                snapshot.ExactTt == snapshot.Basis.ScoreExact(snapshot.Workload) &&
                (snapshot.Assessment is null || snapshot.Assessment == TtJointScoring.Evaluate(snapshot.Basis, snapshot.Workload));
        }
        catch (OverflowException) { return false; }
    }
}
