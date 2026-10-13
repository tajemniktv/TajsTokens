// Taj's Tokens | SqliteTelemetryRepository.RemoteFleet.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    private const string RemoteFleetSourceContract = "ssh-codex-thread-history/v1";

    /// <summary>
    ///     Retains only validated status metadata from one successful, explicitly requested SSH
    ///     check. The local collection time is part of the stable ID: retrying the same result is
    ///     idempotent, while a later check with unchanged remote state is a separate observation.
    /// </summary>
    public async Task<bool> SaveRemoteFleetSnapshotAsync(
        CodexRemoteFleetHostResult source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Error is not null || !CodexRemoteFleetHostPolicy.IsValid(source.Host) ||
            source.CollectedAtUtc is null || source.Threads.Count > 150 ||
            source.SourceErrors is < 0 or > 10_000 ||
            source.Threads.Any(thread =>
                !string.Equals(thread.Host, source.Host, StringComparison.OrdinalIgnoreCase) ||
                !CodexRemoteFleetHostPolicy.IsSafeThreadId(thread.ThreadId) ||
                thread.NativeStatus != CodexRemoteFleetHostPolicy.NormalizeStatus(thread.NativeStatus)) ||
            source.Threads.Select(thread => thread.ThreadId).Distinct(StringComparer.Ordinal).Count() != source.Threads.Count)
            throw new ArgumentException("Remote fleet snapshot contains unsupported or content-bearing metadata.", nameof(source));

        DateTimeOffset collected = source.CollectedAtUtc.Value.ToUniversalTime();
        CodexRemoteFleetThread[] threads = source.Threads.OrderBy(thread => thread.ThreadId, StringComparer.Ordinal).ToArray();
        var identity = new StringBuilder().Append(source.Host.ToLowerInvariant()).Append('\u001f')
            .Append(SerializeUtc(source.CapturedAtUtc)).Append('\u001f').Append(SerializeUtc(collected))
            .Append('\u001f').Append(source.HistoryCapabilityAvailable).Append('\u001f')
            .Append(source.Truncated).Append('\u001f').Append(source.SourceErrors);
        foreach (CodexRemoteFleetThread thread in threads)
            identity.Append('\u001f').Append(thread.ThreadId).Append('\u001f').Append(thread.NativeStatus)
                .Append('\u001f').Append(thread.StartedAtUtc is { } started ? SerializeUtc(started) : "")
                .Append('\u001f').Append(thread.LastObservedAtUtc is { } observed ? SerializeUtc(observed) : "");
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO remote_fleet_snapshots
                (snapshot_id, host_alias, provider, source_contract, remote_captured_at_utc,
                 collected_at_utc, history_available, truncated, source_errors, thread_count)
            VALUES($id, $host, 'codex', $source, $captured, $collected, $history, $truncated, $errors, $count);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$host", source.Host);
        command.Parameters.AddWithValue("$source", RemoteFleetSourceContract);
        command.Parameters.AddWithValue("$captured", SerializeUtc(source.CapturedAtUtc));
        command.Parameters.AddWithValue("$collected", SerializeUtc(collected));
        command.Parameters.AddWithValue("$history", source.HistoryCapabilityAvailable ? 1 : 0);
        command.Parameters.AddWithValue("$truncated", source.Truncated ? 1 : 0);
        command.Parameters.AddWithValue("$errors", source.SourceErrors);
        command.Parameters.AddWithValue("$count", threads.Length);
        bool inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (inserted)
        {
            foreach (CodexRemoteFleetThread thread in threads)
            {
                await using SqliteCommand item = connection.CreateCommand();
                item.Transaction = transaction;
                item.CommandText = """
                    INSERT INTO remote_fleet_threads(snapshot_id, thread_id, native_status, started_at_utc, observed_at_utc)
                    VALUES($snapshot, $thread, $status, $started, $observed);
                    """;
                item.Parameters.AddWithValue("$snapshot", id);
                item.Parameters.AddWithValue("$thread", thread.ThreadId);
                item.Parameters.AddWithValue("$status", thread.NativeStatus);
                item.Parameters.AddWithValue("$started", thread.StartedAtUtc is { } started
                    ? SerializeUtc(started) : DBNull.Value);
                item.Parameters.AddWithValue("$observed", thread.LastObservedAtUtc is { } observed
                    ? SerializeUtc(observed) : DBNull.Value);
                await item.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        transaction.Commit();
        return inserted;
    }

    public async Task<CodexRemoteFleetHostResult?> ReadLatestRemoteFleetSnapshotAsync(
        string host, CancellationToken cancellationToken)
    {
        if (!CodexRemoteFleetHostPolicy.IsValid(host)) throw new ArgumentException("Invalid SSH host alias.", nameof(host));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_id, host_alias, remote_captured_at_utc, collected_at_utc,
                   history_available, truncated, source_errors, thread_count
            FROM remote_fleet_snapshots
            WHERE host_alias = $host COLLATE NOCASE AND provider = 'codex' AND source_contract = $source
            ORDER BY collected_at_utc DESC, snapshot_id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$source", RemoteFleetSourceContract);
        string id, savedHost;
        DateTimeOffset captured, collected;
        bool history, truncated;
        int errors, count;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            id = reader.GetString(0);
            savedHost = reader.GetString(1);
            captured = ParseUtc(reader.GetString(2));
            collected = ParseUtc(reader.GetString(3));
            history = reader.GetInt32(4) != 0;
            truncated = reader.GetInt32(5) != 0;
            errors = reader.GetInt32(6);
            count = reader.GetInt32(7);
        }
        await using SqliteCommand items = connection.CreateCommand();
        items.Transaction = transaction;
        items.CommandText = """
            SELECT thread_id, native_status, started_at_utc, observed_at_utc
            FROM remote_fleet_threads WHERE snapshot_id = $id ORDER BY thread_id;
            """;
        items.Parameters.AddWithValue("$id", id);
        var threads = new List<CodexRemoteFleetThread>();
        await using (SqliteDataReader reader = await items.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                threads.Add(new(savedHost, reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3))));
        }
        if (threads.Count != count) throw new InvalidDataException("Remote fleet snapshot is incomplete.");
        transaction.Commit();
        return new(savedHost, captured, threads, null, truncated, errors, history)
            { CollectedAtUtc = collected };
    }

    public async Task<IReadOnlyList<CodexRemoteFleetSnapshotSummary>> ReadRemoteFleetHistoryAsync(
        string host, int take, CancellationToken cancellationToken)
    {
        if (!CodexRemoteFleetHostPolicy.IsValid(host)) throw new ArgumentException("Invalid SSH host alias.", nameof(host));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT host_alias, remote_captured_at_utc, collected_at_utc,
                   thread_count, history_available, truncated, source_errors
            FROM remote_fleet_snapshots
            WHERE host_alias = $host COLLATE NOCASE AND provider = 'codex' AND source_contract = $source
            ORDER BY collected_at_utc DESC, snapshot_id DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$source", RemoteFleetSourceContract);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 100));
        var results = new List<CodexRemoteFleetSnapshotSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new(reader.GetString(0), ParseUtc(reader.GetString(1)), ParseUtc(reader.GetString(2)),
                reader.GetInt32(3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, reader.GetInt32(6)));
        return results;
    }
}
