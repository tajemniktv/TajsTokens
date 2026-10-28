// Taj's Tokens | SqliteCodexTokenCounterEvidence.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>
/// Original, content-free reducer inputs. This journal is independent of the active delta ledger:
/// even incomplete/invalid counters and observations producing no delta retain their original shape.
/// It deliberately has no source JSON, arbitrary metadata or extension-data field.
/// </summary>
internal static class SqliteCodexTokenCounterEvidence
{
    public const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS codex_token_counter_observations (
            source_event_id TEXT NOT NULL,
            interpretation_sha256 TEXT NOT NULL,
            source_identity TEXT,
            source_file TEXT NOT NULL,
            session_id TEXT NOT NULL,
            observed_at_utc TEXT NOT NULL,
            occurrence_first_captured_at_utc TEXT NOT NULL,
            input_first_captured_at_utc TEXT NOT NULL,
            contract_version INTEGER NOT NULL,
            evidence_json TEXT NOT NULL,
            PRIMARY KEY(source_event_id, interpretation_sha256)
        );
        CREATE INDEX IF NOT EXISTS idx_token_counter_observations_session
            ON codex_token_counter_observations(session_id, input_first_captured_at_utc);
        CREATE INDEX IF NOT EXISTS idx_token_counter_observations_generation
            ON codex_token_counter_observations(source_identity);
        """;

    // Interpretation changes are variants of the same occurrence, never another additive amount.
    // Capturing an identical variant again does not change either of its first-capture times.
    private sealed record Counters(long? InputTokens, long? CachedInputTokens, long? CacheWriteInputTokens,
        long? OutputTokens, long? ReasoningOutputTokens, long? TotalTokens)
    {
        public static Counters? From(CodexTokenUsageSnapshot? snapshot) => snapshot is null ? null : new(
            snapshot.InputTokens, snapshot.CachedInputTokens, snapshot.CacheWriteInputTokens,
            snapshot.OutputTokens, snapshot.ReasoningOutputTokens, snapshot.TotalTokens);
    }

    private sealed record Input(
        Counters? TotalTokenUsage,
        Counters? LastTokenUsage,
        DateTimeOffset? SourceEventAtUtc,
        CodexSourceTimestampQuality SourceTimestampQuality,
        string? AgentId,
        string? Model,
        string? ReasoningEffort);

    public static async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction,
        CodexTokenCountObservation observation, string safeFileLabel, CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(new Input(
            Counters.From(observation.TotalTokenUsage), Counters.From(observation.LastTokenUsage),
            observation.SourceEventAtUtc?.ToUniversalTime(), observation.SourceTimestampQuality,
            observation.AgentId, observation.Model, observation.ReasoningEffort));
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO codex_token_counter_observations(
                source_event_id, interpretation_sha256, source_identity, source_file, session_id,
                observed_at_utc, occurrence_first_captured_at_utc, input_first_captured_at_utc,
                contract_version, evidence_json)
            SELECT $id, $hash,
                (SELECT source_identity FROM rollout_records WHERE source_record_id = $id),
                $file, $session,
                COALESCE((SELECT observed_at_utc FROM codex_native_token_events WHERE source_event_id = $id),
                         (SELECT observed_at_utc FROM codex_token_counter_observations WHERE source_event_id = $id LIMIT 1),
                         $observed),
                COALESCE((SELECT captured_at_utc FROM codex_native_token_events WHERE source_event_id = $id),
                         (SELECT occurrence_first_captured_at_utc FROM codex_token_counter_observations WHERE source_event_id = $id LIMIT 1),
                         $captured),
                $captured, 1, $json
            ON CONFLICT(source_event_id, interpretation_sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", observation.SourceEventId);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$file", safeFileLabel);
        command.Parameters.AddWithValue("$session", observation.SessionId);
        command.Parameters.AddWithValue("$observed", Utc(observation.ObservedAtUtc));
        command.Parameters.AddWithValue("$captured", Utc(observation.CapturedAtUtc));
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
