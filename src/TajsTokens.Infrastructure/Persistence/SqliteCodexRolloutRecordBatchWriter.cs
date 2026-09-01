using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>
/// High-volume storage-metadata writer for rollout ingestion. The broader Observatory store owns
/// canonical rollout-file identity/label/upsert semantics; this path batches only per-record metadata
/// so the two persistence paths cannot drift while still avoiding a transaction per JSONL record.
/// </summary>
internal sealed class SqliteCodexRolloutRecordBatchWriter(
    string databasePath,
    ICodexObservatoryStore observatoryStore) : ICodexRolloutRecordBatchWriter
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    private readonly ICodexObservatoryStore _observatoryStore = observatoryStore;
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
            var sessionId = records
                .Select(record => record.SessionId)
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var lastSeen = records.Max(record => record.ObservedAtUtc);

            // Canonical file-label hashing, path-replacement cleanup and rollout_files upsert live in
            // one place: SqliteCodexObservatoryStore. If the following record transaction fails, the
            // checkpoint is not advanced; replay safely repeats this idempotent file upsert before
            // retrying ON CONFLICT-safe record inserts.
            await _observatoryStore.UpsertRolloutFileAsync(
                sourceIdentity,
                filePath,
                sessionId,
                fileSizeBytes,
                lastSeen,
                cancellationToken);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var labelCommand = connection.CreateCommand();
            labelCommand.CommandText = "SELECT file_path FROM rollout_files WHERE source_identity = $identity LIMIT 1;";
            labelCommand.Parameters.AddWithValue("$identity", sourceIdentity);
            var safeFileLabel = await labelCommand.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new InvalidOperationException("Canonical rollout file metadata was not available after upsert.");

            using var transaction = connection.BeginTransaction();
            try
            {
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

    private static string SerializeUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
