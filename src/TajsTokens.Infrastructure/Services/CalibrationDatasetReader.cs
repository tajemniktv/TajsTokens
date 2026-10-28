using System.Globalization;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

/// <summary>One worker's overlapping numeric feature reads. The original SQLite reader
/// still owns quota/account eligibility, row bounds and the uncached fallback.</summary>
internal sealed class CalibrationDatasetReader(string databasePath, IReadOnlyList<RolloutAccountAssociation> associations,
    DateTimeOffset sharedFromUtc, DateTimeOffset sharedToUtc, ICodexForecastDatasetReader? featureReader = null)
    : ICodexForecastDatasetReader
{
    private readonly SqliteForecastDatasetReader _reader = new(databasePath, associations);
    private readonly SqliteCalibrationEvidence _evidence = new(databasePath);
    private CodexForecastDataset? _shared;
    private CalibrationEvidenceStamp? _stamp;
    private string? _provider, _profile;
    private bool _wideReadUnavailable;
    internal int SharedFeatureReads { get; private set; }
    internal int ReusedReads { get; private set; }

    public async Task<CodexForecastDataset> ReadAsync(string provider, string profile, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken token, string? accountKey = null, bool includeQuota = true, bool includeLedger = false)
    {
        if (includeLedger || fromUtc < sharedFromUtc || toUtc > sharedToUtc || fromUtc >= toUtc || _wideReadUnavailable)
            return await _reader.ReadAsync(provider, profile, fromUtc, toUtc, token, accountKey, includeQuota, includeLedger);
        CalibrationEvidenceStamp? before = await _evidence.ReadAsync(DateTimeOffset.UtcNow, token, includeTimeBoundary: false);
        if (before is null) return await _reader.ReadAsync(provider, profile, fromUtc, toUtc, token, accountKey, includeQuota);
        if (_stamp != before || _provider != provider || _profile != profile)
        { _shared = null; _stamp = null; }
        if (_shared is null)
        {
            try
            {
                // Quota is deliberately not widened across accounts. A broad feature read
                // that reaches its supported bound must not disable a narrower answer.
                _shared = await (featureReader ?? _reader).ReadAsync(provider, profile, sharedFromUtc, sharedToUtc,
                    token, includeQuota: false);
                SharedFeatureReads++;
            }
            catch (ForecastDatasetReadLimitException)
            {
                _wideReadUnavailable = true;
                return await _reader.ReadAsync(provider, profile, fromUtc, toUtc, token, accountKey, includeQuota);
            }
            _stamp = before; _provider = provider; _profile = profile;
        }
        using var work = WorkDiagnostics.Measure(WorkPhase.DatasetReuse);
        DateTimeOffset lookback = fromUtc.AddHours(-2);
        // Keep the exact source reader's active-session query. It intentionally sees the
        // whole metadata table before source-membership filtering, including retired rows.
        // Deriving this set from the already-filtered shared dataset would lose old prefixes.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id FROM codex_native_token_events
            WHERE source_timestamp_quality='Valid' AND source_event_at_utc>$from AND source_event_at_utc<=$to
            UNION SELECT session_id FROM codex_workload_observations WHERE observed_at_utc>$from AND observed_at_utc<=$to
            LIMIT 100001;
            """;
        command.Parameters.AddWithValue("$from", lookback.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", toUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) sessions.Add(reader.GetString(0));
        if (sessions.Count > 100000)
        {
            _shared = null; _stamp = null;
            return await _reader.ReadAsync(provider, profile, fromUtc, toUtc, token, accountKey, includeQuota);
        }
        IReadOnlyList<QuotaSnapshot> quota = includeQuota
            ? (await _reader.ReadQuotaOnlyAsync(provider, profile, fromUtc, toUtc, token, accountKey)).Quota : [];
        CalibrationEvidenceStamp? after = await _evidence.ReadAsync(DateTimeOffset.UtcNow, token, includeTimeBoundary: false);
        if (before != after)
        {
            _shared = null; _stamp = null;
            return await _reader.ReadAsync(provider, profile, fromUtc, toUtc, token, accountKey, includeQuota);
        }
        CodexForecastDataset result = _shared with
        {
            Quota = quota,
            Tokens = _shared.Tokens.Where(x => x.ObservedAtUtc > lookback && x.ObservedAtUtc <= toUtc).ToArray(),
            Context = _shared.Context.Where(x => x.ObservedAtUtc > lookback && x.ObservedAtUtc <= toUtc).ToArray(),
            Workload = _shared.Workload.Where(x => x.ObservedAtUtc <= toUtc && sessions.Contains(x.SessionId)).ToArray(),
        };
        ReusedReads++;
        work?.Complete(rows: result.Quota.Count + result.Tokens.Count + result.Workload.Count + result.Context.Count, cacheHits: 1);
        return result;
    }
}
