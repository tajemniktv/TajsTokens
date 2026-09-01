using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>
/// High-volume storage-metadata writer for rollout ingestion. The broader Observatory store retains
/// semantic writes/accounting; this path removes the previous connection+transaction+file-upsert cost
/// paid for every single JSONL record.
/// </summary>
internal sealed class SqliteCodexRolloutRecordBatchWriter(string databasePath) : ICodexRolloutRecordBatchWriter
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task WriteBatchAsync(
        string sourceIdentity,
        string filePath,
        long fileSizeBytes,
        IReadOnlyList<CodexRolloutRecordMetadata> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            try
            {
                var safeFileLabel = BuildSafeFileLabel(filePath);
                var insertRecord = connection.CreateCommand();
                insertRecord.Transaction = transaction;
                insertRecord.CommandText = """
                    INSERT INTO rollout_records(
                        source_record_id, source_identity, file_path, session_id, event_class, record_bytes, observed_at_utc)
                    VALUES($id, $identity, $file, $session, $class, $bytes, $observed)
                    ON CONFLICT(source_record_id) DO NOTHING;
                    """;

                var idParameter = insertRecord.Parameters.Add("$id", SqliteType.Text);
                var identityParameter = insertRecord.Parameters.Add("$identity", SqliteType.Text);
                var fileParameter = insertRecord.Parameters.Add("$file", SqliteType.Text);
                var sessionParameter = insertRecord.Parameters.Add("$session", SqliteType.Text);
                var classParameter = insertRecord.Parameters.Add("$class", SqliteType.Text);
                var bytesParameter = insertRecord.Parameters.Add("$bytes", SqliteType.Integer);
                var observedParameter = insertRecord.Parameters.Add("$observed", SqliteType.Text);
                insertRecord.Prepare();

                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    idParameter.Value = record.SourceRecordId;
                    identityParameter.Value = sourceIdentity;
                    fileParameter.Value = safeFileLabel;
                    sessionParameter.Value = record.SessionId is null ? DBNull.Value : record.SessionId;
                    classParameter.Value = record.EventClass;
                    bytesParameter.Value = Math.Max(0, record.RecordBytes);
                    observedParameter.Value = SerializeUtc(record.ObservedAtUtc);
                    await insertRecord.ExecuteNonQueryAsync(cancellationToken);
                }

                // A file path can be reused after replacement. Remove the old source-identity alias
                // before upserting this generation, exactly as the Observatory store's scalar path did.
                var removePathAlias = connection.CreateCommand();
                removePathAlias.Transaction = transaction;
                removePathAlias.CommandText =
                    "DELETE FROM rollout_files WHERE file_path = $file AND source_identity <> $identity;";
                removePathAlias.Parameters.AddWithValue("$file", safeFileLabel);
                removePathAlias.Parameters.AddWithValue("$identity", sourceIdentity);
                await removePathAlias.ExecuteNonQueryAsync(cancellationToken);

                var sessionId = records
                    .Select(record => record.SessionId)
                    .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var lastSeen = records.Max(record => record.ObservedAtUtc);

                var upsertFile = connection.CreateCommand();
                upsertFile.Transaction = transaction;
                upsertFile.CommandText = """
                    INSERT INTO rollout_files(source_identity, file_path, session_id, size_bytes, last_seen_at_utc)
                    VALUES($identity, $file, $session, $size, $seen)
                    ON CONFLICT(source_identity) DO UPDATE SET
                      file_path = excluded.file_path,
                      session_id = COALESCE(excluded.session_id, rollout_files.session_id),
                      size_bytes = excluded.size_bytes,
                      last_seen_at_utc = MAX(rollout_files.last_seen_at_utc, excluded.last_seen_at_utc);
                    """;
                upsertFile.Parameters.AddWithValue("$identity", sourceIdentity);
                upsertFile.Parameters.AddWithValue("$file", safeFileLabel);
                upsertFile.Parameters.AddWithValue("$session", sessionId is null ? DBNull.Value : sessionId);
                upsertFile.Parameters.AddWithValue("$size", Math.Max(0, fileSizeBytes));
                upsertFile.Parameters.AddWithValue("$seen", SerializeUtc(lastSeen));
                await upsertFile.ExecuteNonQueryAsync(cancellationToken);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string BuildSafeFileLabel(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "rollout.jsonl";
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            normalized = filePath;
        }

        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..12];
        return $"{fileName} [{hash}]";
    }

    private static string SerializeUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
