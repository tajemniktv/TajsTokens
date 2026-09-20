// Taj's Tokens | CodexServerEvidenceService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

#endregion

namespace TajsTokens.Infrastructure.Services;

/// <summary>One bounded cadence, separate evidence store/read model. Never feeds live quota policy.</summary>
public sealed class CodexServerEvidenceService(
    string databasePath,
    SqliteTelemetryRepository repository,
    ICodexServerEvidenceProvider provider,
    Func<IReadOnlyList<RolloutAccountAssociation>>? associations = null,
    ICodexServerEvidenceProvider? backendProvider = null) : ICodexServerEvidenceReader
{

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _historicalAvailable;
    private DateTimeOffset _nextAttempt;
    public string Status { get; private set; } = "Server evidence has not been collected in this process.";

    public async Task<CodexServerEvidenceHistory> ReadHistoryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset asOf,
        string? accountKey,
        CancellationToken token)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token);
        using SqliteTransaction transaction = connection.BeginTransaction(true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$from", fromUtc.ToUniversalTime().ToString("O"));
        command.CommandText = """
                              SELECT evidence_json,activity_bucket_set_id FROM codex_server_evidence
                              WHERE collected_at_utc >= $from AND collected_at_utc <= $asof AND surface<>'QuotaMetadata'
                                AND ($account IS NULL OR correlated_account_key=$account OR correlated_account_key IS NULL)
                              ORDER BY collected_at_utc DESC,observation_id DESC LIMIT 1001;
                              """;
        command.Parameters.AddWithValue("$asof", asOf.ToString("O"));
        command.Parameters.AddWithValue("$account", accountKey ?? (object)DBNull.Value);
        var evidence = new List<CodexServerObservation>();
        long evidenceBytes = 0;
        bool evidenceTruncated = false;
        int invalidEvidence = 0;
        var bucketSets = new Dictionary<string, long>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                string json = reader.GetString(0);
                evidenceBytes += json.Length * 2L;
                if (evidence.Count >= 1000 || evidenceBytes > 16 * 1024 * 1024)
                {
                    evidenceTruncated = true;
                    break;
                }
                try
                {
                    var row = JsonSerializer.Deserialize<CodexServerObservation>(json);
                    if (row is not null)
                    {
                        evidence.Add(row);
                        if (!reader.IsDBNull(1)) bucketSets[row.Id] = reader.GetInt64(1);
                    }
                    else
                    {
                        invalidEvidence++;
                    }
                }
                catch (JsonException)
                {
                    invalidEvidence++;
                }
            }
        }
        var dayCache = new Dictionary<long, IReadOnlyList<CodexAccountDay>>();
        for (int i = 0; i < evidence.Count; i++)
        {
            if (evidence[i].Activity is not { } activity || !bucketSets.TryGetValue(evidence[i].Id, out long set)) continue;
            if (!dayCache.TryGetValue(set, out IReadOnlyList<CodexAccountDay>? days))
            {
                days = await CodexServerEvidenceStorage.ReadDaysAsync(connection, transaction, set, token);
                evidenceBytes += days.Count * 64L;
                if (evidenceBytes > 16 * 1024 * 1024)
                {
                    evidenceTruncated = true;
                    break;
                }
                dayCache.Add(set, days);
            }
            evidence[i] = evidence[i] with { Activity = activity with { DailyUsageBuckets = days } };
        }
        transaction.Commit();
        return new CodexServerEvidenceHistory(evidence, evidenceTruncated, invalidEvidence, asOf);
    }

    public async Task CollectAsync(bool force, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        try
        {
            if (!force && DateTimeOffset.UtcNow < _nextAttempt) return;
            // Failures also back off. Manual retries are explicit; this is not the quota poll cadence.
            _nextAttempt = DateTimeOffset.UtcNow.AddMinutes(30);
            Status = "Server-evidence collection is in progress; retained results below may be older.";
            await repository.InitializeAsync(token);
            IReadOnlyList<string> threads = await SelectThreadsAsync(token);
            CodexServerCollection collection = await provider.CollectAsync(threads, token);
            await repository.SaveServerEvidenceAsync(collection, token);
            if (backendProvider is not null)
                await repository.SaveServerEvidenceAsync(await backendProvider.CollectAsync([], token), token);
            if (_historicalAvailable && backendProvider is CodexBackendDailyEvidenceProvider historical)
            {
                CodexServerCollection history = await historical.CollectHistoricalAsync(threads, token);
                await repository.SaveServerEvidenceAsync(history, token);
                _historicalAvailable = history.Observations.Count > 0 &&
                                       history.Observations.All(x => x.State is ServerEvidenceState.Available or ServerEvidenceState.Empty);
            }
            Status = $"Last server-evidence fetch {DateTimeOffset.Now:g}; {collection.Observations.Count} surface/thread results retained.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Status =
                "Server-evidence collection/storage failed; prior observations remain historical, not fresh. See capability results and retry.";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Explicit capability check. A denied source is not repeatedly polled in the background.</summary>
    public async Task CollectHistoricalAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (backendProvider is not CodexBackendDailyEvidenceProvider provider) return;
            await repository.InitializeAsync(token);
            CodexServerCollection history = await provider.CollectHistoricalAsync(await SelectThreadsAsync(token), token);
            await repository.SaveServerEvidenceAsync(history, token);
            _historicalAvailable = history.Observations.Count > 0 &&
                                   history.Observations.All(x => x.State is ServerEvidenceState.Available or ServerEvidenceState.Empty);
            Status = history.Observations.Count == 0
                ? "Experimental backend adapter is disabled; no credentials read."
                : "Historical capability check completed. Successful capabilities can refresh on the existing cadence this process; denied capabilities are not automatically retried.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexServerObservation>> ReadDailyReportsAsync(CancellationToken token)
    {
        await repository.InitializeAsync(token);
        await using SqliteConnection connection = await OpenReadOnlyAsync(token);
        using SqliteCommand command = connection.CreateCommand();
        // Latest attempt per surface, never quietly substitute an old successful account/report.
        command.CommandText = """
                              SELECT evidence_json FROM codex_server_evidence e
                              WHERE surface IN ('DailyCounts','DailyRelativeUsage') AND observation_id=(
                                  SELECT observation_id FROM codex_server_evidence latest WHERE latest.surface=e.surface
                                  ORDER BY collected_at_utc DESC,observation_id DESC LIMIT 1);
                              """;
        var rows = new List<CodexServerObservation>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var row = JsonSerializer.Deserialize<CodexServerObservation>(reader.GetString(0));
            if (row?.ContractVersion == CodexServerEvidenceParser.Contract) rows.Add(row);
        }
        return rows;
    }

    public async Task<IReadOnlyList<string>> SelectThreadsAsync(CancellationToken token)
    {
        await using SqliteConnection connection = await OpenReadOnlyAsync(token);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='codex_native_token_events';";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) return [];
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='codex_server_evidence';";
        bool hasEvidence = Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
        string rotation = hasEvidence
            ? "COALESCE((SELECT MAX(e.collected_at_utc) FROM codex_server_evidence e WHERE e.surface='ThreadUsage' AND e.thread_id=n.session_id),'') ASC,"
            : "";
        command.CommandText = $"""
                               SELECT n.session_id FROM codex_native_token_events n
                               WHERE n.observed_at_utc >= $from
                               GROUP BY n.session_id
                               ORDER BY {rotation} MAX(n.observed_at_utc) DESC LIMIT 100;
                               """;
        command.Parameters.AddWithValue("$from", Utc(DateTimeOffset.UtcNow.AddDays(-30)));
        var threads = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (Guid.TryParse(reader.GetString(0), out _) && threads.Count < CodexAppServerEvidenceProvider.MaxThreads)
                threads.Add(reader.GetString(0));
        return threads;
    }

    public async Task<string> ReadNativeMetadataSummaryAsync(CancellationToken token)
    {
        await repository.InitializeAsync(token);
        await using SqliteConnection connection = await OpenReadOnlyAsync(token);
        using SqliteTransaction transaction = connection.BeginTransaction(true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT evidence_json FROM codex_server_evidence WHERE surface='QuotaMetadata' ORDER BY collected_at_utc DESC,observation_id DESC LIMIT 1;";
        var lines = new List<string> { "Native quota metadata (not a workload-credit scale)" };
        if (await command.ExecuteScalarAsync(token) is string json && JsonSerializer.Deserialize<CodexServerObservation>(json) is { } row)
        {
            lines.Add(
                $"Latest attempt: {row.State}, {row.CollectedAtUtc:g}; {row.AccountEvidence}; account {row.CorrelatedAccountKey ?? "unknown"}.");
            foreach (CodexQuotaLimitMetadata limit in row.QuotaMetadata?.Limits ?? [])
            {
                lines.Add(
                    $"{limit.ResponseKey}: {limit.LimitName ?? limit.LimitId ?? "unnamed"}; plan {limit.PlanType ?? "unknown"}; normal model {limit.NormalModelSlug ?? "unknown"}; reached type {limit.RateLimitReachedType ?? "unknown"}; spend-control reached {limit.SpendControlReached?.ToString() ?? "unknown"}.");
                if (limit.Credits is { } credits)
                    lines.Add(
                        $"  Credits: has={credits.HasCredits?.ToString() ?? "unknown"}, unlimited={credits.Unlimited?.ToString() ?? "unknown"}, balance={credits.Balance ?? "unknown"}.");
                if (limit.IndividualLimit is { } individual)
                    lines.Add(
                        $"  Individual spend control: used={individual.Used ?? "unknown"}, limit={individual.Limit ?? "unknown"}, remaining={individual.RemainingPercent?.ToString() ?? "unknown"}%, reset Unix seconds={individual.ResetsAt?.ToString() ?? "unknown"}.");
            }
            lines.Add("Named and legacy buckets are alternative response views, never summed.");
        }
        else
        {
            lines.Add("No retained response metadata yet; refresh telemetry to collect it.");
        }
        var driftHistory = new List<CodexServerObservation>();
        // Independent budgets prevent frequent quota polls from evicting daily report comparisons.
        foreach (string surface in new[] { "QuotaMetadata", "DailyCounts", "DailyRelativeUsage" })
        {
            long driftBytes = 0;
            command.CommandText =
                "SELECT evidence_json FROM codex_server_evidence WHERE surface=$surface ORDER BY collected_at_utc DESC,observation_id DESC LIMIT 512;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$surface", surface);
            await using SqliteDataReader historyReader = await command.ExecuteReaderAsync(token);
            while (await historyReader.ReadAsync(token))
            {
                string payload = historyReader.GetString(0);
                driftBytes += payload.Length * 2L;
                if (driftBytes > 5 * 1024 * 1024) break;
                if (JsonSerializer.Deserialize<CodexServerObservation>(payload) is { } observation) driftHistory.Add(observation);
            }
        }
        command.Parameters.Clear();
        IReadOnlyList<CodexDriftSignal> signals = CodexEvidenceDrift.Analyze(driftHistory);
        lines.Add(
            $"Evidence changes ({CodexEvidenceDrift.Policy}): {signals.Count} signals from a bounded {driftHistory.Count}-fetch history; observations persist, signals are rebuilt. Not confirmed quota-policy changes.");
        CodexDriftSignal[] numerical = signals.Where(x => x.Kind == CodexDriftKind.NumericalVariation).ToArray();
        if (numerical.Length > 0)
            lines.Add(
                $"  {numerical.Length} tiny relative-usage variations (≤1e-12pp diagnostic threshold, not provider precision); exact values are retained. Showing up to three after substantive signals.");
        foreach (CodexDriftSignal signal in signals.Where(x => x.Kind != CodexDriftKind.NumericalVariation).TakeLast(24)
                     .Concat(numerical.TakeLast(3)))
            lines.Add(
                $"  {signal.FirstObservedAtUtc:g} · {QuotaAccountScope.Describe(signal.AccountKey)} · {signal.Surface} · {signal.Kind} · {signal.Field}: {signal.Before ?? "unknown/not reported"} → {signal.After ?? "unknown/not reported"}; effective date unknown.");
        if (signals.Count == 0) lines.Add("  No comparable changes in the selected history; this does not prove policy stability.");
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('codex_workload_observations') WHERE name='service_tier';";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1)
        {
            lines.Add("Local service-tier setting records (physical occurrences, not request counts or confirmed billing tier):");
            command.CommandText = """
                                  SELECT service_tier,COUNT(*),COUNT(DISTINCT source_identity)
                                  FROM codex_workload_observations WHERE event_type='thread_settings_applied'
                                  GROUP BY service_tier ORDER BY COUNT(*) DESC LIMIT 32;
                                  """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
            bool any = false;
            while (await reader.ReadAsync(token))
            {
                any = true;
                lines.Add(
                    $"  {(reader.IsDBNull(0) ? "unknown" : reader.GetString(0))}: {reader.GetInt64(1):N0} records across {reader.GetInt64(2):N0} sources.");
            }
            if (!any) lines.Add("  No setting records retained. Absence does not imply Standard.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<CodexServerComparisonReport> CompareAsync(
        CancellationToken token,
        IReadOnlyList<CodexServerObservation>? transient = null)
    {
        await using SqliteConnection connection = await OpenReadOnlyAsync(token);
        using SqliteTransaction transaction = connection.BeginTransaction(true);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE name IN ('codex_server_evidence','codex_native_token_events');";
        var tables = new HashSet<string>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token)) tables.Add(reader.GetString(0));
        }
        var history = new List<CodexServerObservation>();
        if (tables.Contains("codex_server_evidence"))
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('codex_server_evidence') WHERE name='activity_bucket_set_id';";
            bool normalized = Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
            command.CommandText = "SELECT evidence_json," + (normalized ? "activity_bucket_set_id" : "NULL") +
                                  " FROM codex_server_evidence WHERE surface<>'QuotaMetadata' ORDER BY collected_at_utc DESC, observation_id DESC LIMIT 2000;";
            var stored = new List<(CodexServerObservation Row, long? SetId)>();
            long retainedCharacters = 0L;
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    string json = reader.GetString(0);
                    retainedCharacters += json.Length;
                    if (retainedCharacters > 16 * 1024 * 1024) break;
                    var row = JsonSerializer.Deserialize<CodexServerObservation>(json);
                    if (row?.ContractVersion == CodexServerEvidenceParser.Contract)
                        stored.Add((row, reader.IsDBNull(1) ? null : reader.GetInt64(1)));
                }
            }
            var sets = new Dictionary<long, IReadOnlyList<CodexAccountDay>>();
            foreach ((CodexServerObservation row, long? setId) in stored)
            {
                if (setId is null)
                {
                    history.Add(row);
                    continue;
                }
                if (!sets.TryGetValue(setId.Value, out IReadOnlyList<CodexAccountDay>? days))
                {
                    days = await CodexServerEvidenceStorage.ReadDaysAsync(connection, transaction, setId.Value, token);
                    retainedCharacters += JsonSerializer.Serialize(days).Length;
                    if (retainedCharacters > 16 * 1024 * 1024) break;
                    sets.Add(setId.Value, days);
                }
                history.Add(row with { Activity = row.Activity! with { DailyUsageBuckets = days } });
            }
        }
        history = history.Concat(transient ?? []).OrderByDescending(x => x.CollectedAtUtc).ToList();
        // Correlation is an outcome of an attempt, not part of the request identity.
        CodexServerObservation[] latest = history.GroupBy(x => (x.Surface, x.ThreadId,
            BackendAnalytics: x.ClientVersion == CodexHistoricalAnalyticsParser.Contract)).Select(g => g.First()).ToArray();
        var comparisons = new List<CodexServerComparisonRow>();

        async Task<long?> LocalTotal(string predicate, params (string Name, object Value)[] parameters)
        {
            if (!tables.Contains("codex_native_token_events")) return null;
            command.Parameters.Clear();
            command.CommandText = $"SELECT COUNT(*),SUM(reported_total_tokens) FROM codex_native_token_events WHERE {predicate};";
            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            return reader.GetInt64(0) == 0 ? null : reader.GetInt64(1);
        }

        int accountIndex = 0;
        if (tables.Contains("codex_native_token_events"))
        {
            command.Parameters.Clear();
            command.CommandText = """
                                  SELECT COUNT(*),SUM(extra_tokens) FROM (
                                      SELECT reported_total_tokens * (COUNT(*)-1) AS extra_tokens
                                      FROM codex_native_token_events
                                      GROUP BY session_id,observed_at_utc,model,reasoning_effort,uncached_input_tokens,cache_read_tokens,
                                          cache_write_tokens,non_reasoning_output_tokens,reasoning_output_tokens,reported_total_tokens
                                      HAVING COUNT(DISTINCT source_file)>1);
                                  """;
            await using SqliteDataReader duplicateReader = await command.ExecuteReaderAsync(token);
            await duplicateReader.ReadAsync(token);
            if (duplicateReader.GetInt64(0) > 0)
                comparisons.Add(
                    new CodexServerComparisonRow(
                        "Local repeated-event fingerprint candidates",
                        null,
                        duplicateReader.GetInt64(1),
                        null,
                        AccountEvidenceClass.Unattributed,
                        $"{duplicateReader.GetInt64(0):N0} identical session/time/model/effort/token fingerprints occur in multiple physical sources. Extra retained tokens shown, not automatically deduplicated or proven independent usage."));
        }
        foreach (IGrouping<(string? CorrelatedAccountKey, AccountEvidenceClass AccountEvidence), CodexServerObservation> cohort in history
                     .Where(x => x.Activity is not null && x.State is ServerEvidenceState.Available or ServerEvidenceState.Empty)
                     .GroupBy(x => (x.CorrelatedAccountKey, x.AccountEvidence)))
        {
            CodexServerObservation row = cohort.First();
            string label = $"account-{++accountIndex}";
            comparisons.Add(
                new CodexServerComparisonRow(
                    label + " lifetime / retained local total",
                    row.Activity!.LifetimeTokens,
                    await LocalTotal("1=1"),
                    null,
                    row.AccountEvidence,
                    $"Backend fetched {row.CollectedAtUtc:g}. Local retained history has no guaranteed lifetime coverage; ratio is not account attribution."));
            foreach (CodexAccountDay day in row.Activity.DailyUsageBuckets?.OrderBy(x => x.StartDate).TakeLast(30) ?? [])
            {
                comparisons.Add(
                    new CodexServerComparisonRow(
                        label + " day " + day.StartDate,
                        day.Tokens,
                        await LocalTotal("substr(observed_at_utc,1,10)=$date", ("$date", day.StartDate)),
                        null,
                        row.AccountEvidence,
                        "Date-label comparison using local event UTC dates. Backend bucket timezone is unspecified; not a validated interval coverage fraction."));
            }
            CodexServerObservation? prior = cohort.Skip(1)
                .FirstOrDefault(x => x.Activity?.LifetimeTokens is not null && x.CollectedAtUtc < row.CollectedAtUtc);
            if (row.CorrelatedAccountKey is not null && row.AccountEvidence == AccountEvidenceClass.ServerCorrelated &&
                prior?.Activity?.LifetimeTokens is { } earlier && row.Activity.LifetimeTokens is { } current)
                comparisons.Add(
                    new CodexServerComparisonRow(
                        label + " lifetime change",
                        current >= earlier ? current - earlier : null,
                        await LocalTotal(
                            "observed_at_utc > $from AND observed_at_utc <= $to",
                            ("$from", Utc(prior.CollectedAtUtc)),
                            ("$to", Utc(row.CollectedAtUtc))),
                        null,
                        row.AccountEvidence,
                        current < earlier
                            ? "Backend lifetime counter regressed; no positive coverage ratio inferred."
                            : "Fetch-to-fetch comparison only. Backend accounting lag, window boundaries and late local ingestion remain unknown."));
        }
        int threadIndex = 0;
        foreach (CodexServerObservation row in latest.Where(x => x.ThreadUsage is not null && x.State == ServerEvidenceState.Available)
                     .Take(60))
        {
            CodexThreadUsage usage = row.ThreadUsage!;
            string label = $"thread-{++threadIndex}";
            long? local = await LocalTotal("session_id=$thread", ("$thread", usage.ThreadId));
            long? completeTotal = usage.Groups.Count > 0 && usage.Groups.All(x => x.TotalTokens is not null)
                ? usage.Groups.Sum(x => x.TotalTokens!.Value)
                : null;
            comparisons.Add(
                new CodexServerComparisonRow(
                    label,
                    completeTotal,
                    local,
                    usage.EstimatedUsageCreditsMicros,
                    row.AccountEvidence,
                    $"Exact thread-ID join; fetched {row.CollectedAtUtc:g}. Local coverage can be partial. USD micros: {usage.EstimatedUsageUsdMicros?.ToString() ?? "unavailable"}. Credits are estimates, not allowance %."));
            foreach (CodexThreadUsageGroup group in usage.Groups.Take(64))
            {
                long? groupLocal = await LocalTotal(
                    "session_id=$thread AND model IS $model AND reasoning_effort IS $effort",
                    ("$thread", usage.ThreadId),
                    ("$model", group.Model ?? (object)DBNull.Value),
                    ("$effort", group.ReasoningEffort ?? (object)DBNull.Value));
                string localCategories = "unavailable";
                if (groupLocal is not null)
                {
                    // Reuse the exact scope parameters; keep five local categories disjoint.
                    command.CommandText = """
                                          SELECT SUM(uncached_input_tokens),SUM(cache_read_tokens),SUM(cache_write_tokens),
                                              SUM(non_reasoning_output_tokens),SUM(reasoning_output_tokens)
                                          FROM codex_native_token_events WHERE session_id=$thread AND model IS $model AND reasoning_effort IS $effort;
                                          """;
                    await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
                    await reader.ReadAsync(token);
                    localCategories = string.Join("/", Enumerable.Range(0, 5).Select(i => reader.GetInt64(i)));
                }
                comparisons.Add(
                    new CodexServerComparisonRow(
                        $"{label} · {group.Model ?? "unknown model"}/{group.ReasoningEffort ?? "unknown effort"}/{group.Speed ?? "unknown speed"}",
                        group.TotalTokens,
                        groupLocal,
                        group.EstimatedUsageCreditsMicros,
                        row.AccountEvidence,
                        $"Provider net-new/cache/input/output: {group.NetNewInputTokens}/{group.CachedInputTokens}/{group.InputTokens}/{group.OutputTokens}. " +
                        $"Local uncached/cache-read/cache-write/non-reasoning-output/reasoning: {localCategories}. Local comparison joins model/effort only; speed is unavailable locally. Overlapping group comparisons must not be summed."));
            }
        }
        transaction.Commit();
        return new CodexServerComparisonReport(
            DateTimeOffset.UtcNow,
            latest,
            comparisons,
            associations?.Invoke().Count(x => x.IsValid) ?? 0,
            "Source observations remain separate. No ratios assert identical accounting units, no native rollout account IDs are rewritten, and no association is inferred from numeric similarity. " +
            "Read bounded to the latest 2,000 fetch results / 16 MiB of JSON characters, 60 thread comparisons and 30 daily labels per account. Latest failed/null reports do not become zero or silently reuse a successful thread estimate. " +
            "Plan history/grouped analytics have no supported app-server seam: coverage, completeness, approximation and breakdowns are unavailable, not false/zero. " +
            "Credits→historical allowance mapping and held-out calibration cannot be evaluated without those labels. TT and live promotion policy are unchanged.");
    }

    private async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string Utc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}