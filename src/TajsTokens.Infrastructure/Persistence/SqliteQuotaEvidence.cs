using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Shared write contract for live reads and replayable rollout observations.</summary>
internal static class SqliteQuotaEvidence
{
    internal const string Columns = "observation_id,source_identity,session_id,limit_id,plan_type,lane,collected_at_utc,has_source_timestamp";

    internal static QuotaSnapshot Read(QuotaSnapshot row, SqliteDataReader reader, int offset)
    {
        string? Text(int i) => reader.IsDBNull(offset + i) ? null : reader.GetString(offset + i);
        return row with
        {
            ObservationId = Text(0) is { Length: > 0 } id ? id : null,
            SourceIdentity = Text(1), SessionId = Text(2), LimitId = Text(3), PlanType = Text(4), Lane = Text(5),
            CollectedAtUtc = Text(6) is { } time ? DateTimeOffset.Parse(time, CultureInfo.InvariantCulture) : null,
            HasSourceTimestamp = reader.GetInt32(offset + 7) != 0
        };
    }

    internal const string InsertSql = """
        INSERT INTO quota_snapshots(provider,profile,kind,captured_at_utc,used_percent,window_minutes,
            resets_at_utc,source,account_key,observation_id,source_identity,session_id,limit_id,plan_type,lane,
            collected_at_utc,has_source_timestamp)
        VALUES($provider,$profile,$kind,$captured,$used,$window,$resets,$source,$account,$observation,
            $identity,$session,$limit,$plan,$lane,$collected,$timestamp)
        ON CONFLICT(provider,profile,kind,captured_at_utc,source,account_key,observation_id) DO NOTHING;
        """;

    internal static void Bind(SqliteCommand command, QuotaSnapshot snapshot)
    {
        command.Parameters.Clear();
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        Add("$provider", snapshot.Provider); Add("$profile", snapshot.Profile);
        Add("$kind", snapshot.Kind.ToString()); Add("$captured", Utc(snapshot.CapturedAtUtc));
        Add("$used", snapshot.UsedPercent); Add("$window", snapshot.WindowMinutes);
        Add("$resets", snapshot.ResetsAtUtc is { } reset ? Utc(reset) : null);
        Add("$source", snapshot.Source); Add("$account", snapshot.AccountKey ?? "");
        // Same-time contradictory values remain alternatives; exact retries retain first collection.
        var identity = snapshot.ObservationId ?? "snapshot-sha256/v1:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { snapshot.UsedPercent, snapshot.WindowMinutes,
                snapshot.ResetsAtUtc, snapshot.LimitId, snapshot.PlanType, snapshot.Lane })))).ToLowerInvariant();
        Add("$observation", identity); Add("$identity", snapshot.SourceIdentity);
        Add("$session", snapshot.SessionId); Add("$limit", snapshot.LimitId);
        Add("$plan", snapshot.PlanType); Add("$lane", snapshot.Lane);
        Add("$collected", snapshot.CollectedAtUtc is { } collected ? Utc(collected) : null);
        Add("$timestamp", snapshot.HasSourceTimestamp ? 1 : 0);
    }
}
