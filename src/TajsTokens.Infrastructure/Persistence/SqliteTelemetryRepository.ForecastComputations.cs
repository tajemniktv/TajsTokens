using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed partial class SqliteTelemetryRepository
{
    private const int ForecastComputationMaxBytes = 128 * 1024;

    public async Task<QuotaForecastComputationEntry> RecordForecastComputationAsync(QuotaForecastComputation computation,
        CancellationToken token)
    {
        ValidateComputation(computation);
        // Freeze caller-owned arrays before yielding. Identity excludes the actual recording clock:
        // equal retries preserve first recording; changed outputs/inputs produce separate records.
        string payload = JsonSerializer.Serialize(computation);
        if (Encoding.UTF8.GetByteCount(payload) > ForecastComputationMaxBytes)
            throw new ArgumentException("Forecast computation exceeds its storage bound.");
        string hash = ForecastComputationHash(payload);
        string id = "quota-computation:" + hash;
        await InitializeIntelligenceAsync(token);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        // Take SQLite's writer lock before the clock sample. A queued writer cannot backdate
        // a five-minute output to the beginning of its lock wait.
        using (var acquire = connection.CreateCommand())
        {
            acquire.Transaction = transaction;
            acquire.CommandText = "UPDATE intelligence_schema SET version=version WHERE component='phase4-intelligence';";
            await acquire.ExecuteNonQueryAsync(token);
        }
        DateTimeOffset recorded = _timeProvider.GetUtcNow();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO quota_forecast_computations
                    (computation_id,recorded_at_utc,provider,profile,format_version,content_hash,payload)
                VALUES($id,$at,$provider,$profile,1,$hash,$payload)
                ON CONFLICT(computation_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$at", SerializeUtc(recorded));
            insert.Parameters.AddWithValue("$provider", computation.Cohort.Provider);
            insert.Parameters.AddWithValue("$profile", computation.Cohort.Profile);
            insert.Parameters.AddWithValue("$hash", hash); insert.Parameters.AddWithValue("$payload", payload);
            await insert.ExecuteNonQueryAsync(token);
        }
        using var check = connection.CreateCommand(); check.Transaction = transaction;
        check.CommandText = """
            SELECT recorded_at_utc FROM quota_forecast_computations WHERE computation_id=$id
                AND provider=$provider AND profile=$profile AND format_version=1 AND content_hash=$hash AND payload=$payload;
            """;
        check.Parameters.AddWithValue("$id", id); check.Parameters.AddWithValue("$provider", computation.Cohort.Provider);
        check.Parameters.AddWithValue("$profile", computation.Cohort.Profile);
        check.Parameters.AddWithValue("$hash", hash); check.Parameters.AddWithValue("$payload", payload);
        if (await check.ExecuteScalarAsync(token) is not string first)
            throw new InvalidOperationException("Forecast computation identity conflict; original record preserved.");
        transaction.Commit();
        // Only a clock sampled AFTER the immutable output committed can qualify it prospectively.
        // A crash/cancellation between transactions leaves an explicitly unconfirmed output. A
        // later retry confirms at its actual time, never at the old pre-commit clock.
        token.ThrowIfCancellationRequested();
        DateTimeOffset confirmed = _timeProvider.GetUtcNow();
        using var confirm = connection.CreateCommand();
        confirm.CommandText = """
            UPDATE quota_forecast_computations SET confirmed_at_utc=COALESCE(confirmed_at_utc,$confirmed)
            WHERE computation_id=$id AND content_hash=$hash AND payload=$payload AND confirmed_at_utc IS NULL;
            SELECT confirmed_at_utc FROM quota_forecast_computations
            WHERE computation_id=$id AND content_hash=$hash AND payload=$payload;
            """;
        confirm.Parameters.AddWithValue("$id", id); confirm.Parameters.AddWithValue("$hash", hash);
        confirm.Parameters.AddWithValue("$payload", payload); confirm.Parameters.AddWithValue("$confirmed", SerializeUtc(confirmed));
        if (await confirm.ExecuteScalarAsync(token) is not string confirmation)
            throw new InvalidOperationException("Forecast confirmation conflict; original record preserved.");
        return new(id, ParseUtc(first), JsonSerializer.Deserialize<QuotaForecastComputation>(payload), null, ParseUtc(confirmation));
    }

    /// <summary>Bounded forward pagination by actual recording time and immutable ID.</summary>
    public async Task<IReadOnlyList<QuotaForecastComputationEntry>> ReadForecastComputationsAsync(string provider,
        string profile, DateTimeOffset fromUtc, DateTimeOffset toUtc, int take, CancellationToken token,
        DateTimeOffset? afterRecordedUtc = null, string? afterId = null)
    {
        if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(366) ||
            (afterRecordedUtc is null) != (afterId is null)) throw new ArgumentException("Invalid forecast journal range or cursor.");
        // Read-only callers must never initialize/migrate a supplied database or backfill old snapshots.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(token);
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='quota_forecast_computations';";
            if (await exists.ExecuteScalarAsync(token) is null) return [];
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT computation_id,recorded_at_utc,provider,profile,format_version,content_hash,
                CASE WHEN length(CAST(payload AS BLOB)) <= $bound THEN payload ELSE NULL END,confirmed_at_utc
            FROM quota_forecast_computations WHERE provider=$provider AND profile=$profile
                AND recorded_at_utc >= $from AND recorded_at_utc < $to
                AND ($after IS NULL OR recorded_at_utc > $after OR (recorded_at_utc=$after AND computation_id > $afterId))
            ORDER BY recorded_at_utc,computation_id LIMIT $take;
            """;
        command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$profile", profile);
        command.Parameters.AddWithValue("$from", SerializeUtc(fromUtc)); command.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
        command.Parameters.AddWithValue("$after", afterRecordedUtc is { } at ? SerializeUtc(at) : DBNull.Value);
        command.Parameters.AddWithValue("$afterId", (object?)afterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 1000)); command.Parameters.AddWithValue("$bound", ForecastComputationMaxBytes);
        using var reader = await command.ExecuteReaderAsync(token);
        var results = new List<QuotaForecastComputationEntry>();
        while (await reader.ReadAsync(token))
        {
            string id = reader.GetString(0), hash = reader.GetString(5);
            DateTimeOffset? recorded = DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            string? problem = recorded is null ? "invalid-recording-time" : reader.GetInt32(4) != 1 ? "unsupported-format" : reader.IsDBNull(6) ? "oversized-payload" : null;
            DateTimeOffset? confirmed = null;
            if (!reader.IsDBNull(7))
            {
                if (DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.None, out var confirmation)) confirmed = confirmation;
                else problem = "invalid-confirmation-time";
            }
            QuotaForecastComputation? computation = null;
            if (problem is null)
            {
                try
                {
                    string payload = reader.GetString(6);
                    if (ForecastComputationHash(payload) != hash || id != "quota-computation:" + hash)
                        throw new ArgumentException("Checksum mismatch.");
                    computation = JsonSerializer.Deserialize<QuotaForecastComputation>(payload);
                    ValidateComputation(computation);
                    if (computation!.Cohort.Provider != provider || computation.Cohort.Profile != profile)
                        throw new ArgumentException("Scope mismatch.");
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException or NullReferenceException)
                { computation = null; problem = "invalid-or-mismatched-payload"; }
            }
            results.Add(new(id, recorded, computation, problem, confirmed));
        }
        return results;
    }

    private static string ForecastComputationHash(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static void ValidateComputation(QuotaForecastComputation? c)
    {
        static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
        static bool Percent(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 100;
        static bool Count(int value) => value is >= 0 and <= 10_000_000;
        if (c is null || c.Contract != QuotaForecastComputation.Version || !Enum.IsDefined(c.Phase) || c.Cohort is not { } cohort ||
            !Text(cohort.Provider) || !Text(cohort.Profile) || !Text(cohort.Source) || !Text(cohort.AccountKey) ||
            !Enum.IsDefined(cohort.Kind) || c.AnchorUtc == default || c.GeneratedAtUtc == default || c.ResetUtc <= c.AnchorUtc ||
            !Percent(c.AnchorUsedPercent) || !Text(c.PolicyVersion) || c.InputHash is not { Length: 64 } ||
            !c.InputHash.All(Uri.IsHexDigit) || c.Points is not { Count: > 0 and <= 16 })
            throw new ArgumentException("Invalid closed forecast computation.");
        foreach (var p in c.Points)
        {
            if (p is null || !Enum.IsDefined(p.Role) || !Text(p.Model) || p.TargetUtc <= c.AnchorUtc ||
                !double.IsFinite(p.HorizonHours) || p.HorizonHours <= 0 ||
                Math.Abs((p.TargetUtc - c.AnchorUtc).TotalHours - p.HorizonHours) > 1e-7 ||
                !Percent(p.RemainingPercent) || !Percent(p.ExpectedUsagePercent) ||
                !Percent(p.LowerRemainingPercent) || !Percent(p.UpperRemainingPercent) ||
                (p.LowerRemainingPercent is null) != (p.UpperRemainingPercent is null) || p.LowerRemainingPercent > p.UpperRemainingPercent ||
                !Count(p.TrainingSamples) || !Count(p.ValidationSamples) || !Count(p.IntervalSamples) ||
                p.Inference is { } n && (n.Inputs is not { Count: <= 256 } || n.Weights is null || n.Inputs.Count != n.Weights.Count ||
                    !double.IsFinite(n.Intercept) || !double.IsFinite(n.Minimum) || !double.IsFinite(n.Maximum) ||
                    n.Inputs.Any(x => !double.IsFinite(x)) || n.Weights.Any(x => !double.IsFinite(x))))
                throw new ArgumentException("Invalid closed forecast point.");
        }
    }
}
