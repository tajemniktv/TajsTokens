using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

namespace TajsTokens.Infrastructure.Services;

/// <summary>One bounded cadence, separate evidence store/read model. Never feeds live quota policy.</summary>
public sealed class CodexServerEvidenceService(string databasePath, SqliteTelemetryRepository repository,
    ICodexServerEvidenceProvider provider, Func<IReadOnlyList<RolloutAccountAssociation>>? associations = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAttempt;
    public string Status { get; private set; } = "Server evidence has not been collected in this process.";

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
            var threads = await SelectThreadsAsync(token);
            var collection = await provider.CollectAsync(threads, token);
            await repository.SaveServerEvidenceAsync(collection, token);
            Status = $"Last server-evidence fetch {DateTimeOffset.Now:g}; {collection.Observations.Count} surface/thread results retained.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Status = "Server-evidence collection/storage failed; prior observations remain historical, not fresh. See capability results and retry.";
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> SelectThreadsAsync(CancellationToken token)
    {
        await using var connection = await OpenReadOnlyAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='codex_native_token_events';";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) return [];
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='codex_server_evidence';";
        var hasEvidence = Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
        var rotation = hasEvidence ? "COALESCE((SELECT MAX(e.collected_at_utc) FROM codex_server_evidence e WHERE e.surface='ThreadUsage' AND e.thread_id=n.session_id),'') ASC," : "";
        command.CommandText = $"""
            SELECT n.session_id FROM codex_native_token_events n
            WHERE n.observed_at_utc >= $from
            GROUP BY n.session_id
            ORDER BY {rotation} MAX(n.observed_at_utc) DESC LIMIT 100;
            """;
        command.Parameters.AddWithValue("$from", Utc(DateTimeOffset.UtcNow.AddDays(-30)));
        var threads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            if (Guid.TryParse(reader.GetString(0), out _) && threads.Count < CodexAppServerEvidenceProvider.MaxThreads) threads.Add(reader.GetString(0));
        return threads;
    }

    public async Task<CodexServerComparisonReport> CompareAsync(CancellationToken token, IReadOnlyList<CodexServerObservation>? transient = null)
    {
        await using var connection = await OpenReadOnlyAsync(token);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE name IN ('codex_server_evidence','codex_native_token_events');";
        var tables = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) tables.Add(reader.GetString(0));
        var history = new List<CodexServerObservation>();
        if (tables.Contains("codex_server_evidence"))
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('codex_server_evidence') WHERE name='activity_bucket_set_id';";
            var normalized = Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 0;
            command.CommandText = "SELECT evidence_json," + (normalized ? "activity_bucket_set_id" : "NULL") + " FROM codex_server_evidence ORDER BY collected_at_utc DESC, observation_id DESC LIMIT 2000;";
            var stored = new List<(CodexServerObservation Row, long? SetId)>();
            var retainedCharacters = 0L;
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var json = reader.GetString(0);
                    retainedCharacters += json.Length;
                    if (retainedCharacters > 16 * 1024 * 1024) break;
                    var row = JsonSerializer.Deserialize<CodexServerObservation>(json);
                    if (row?.ContractVersion == CodexServerEvidenceParser.Contract)
                        stored.Add((row, reader.IsDBNull(1) ? null : reader.GetInt64(1)));
                }
            }
            var sets = new Dictionary<long, IReadOnlyList<CodexAccountDay>>();
            foreach (var (row, setId) in stored)
            {
                if (setId is null) { history.Add(row); continue; }
                if (!sets.TryGetValue(setId.Value, out var days))
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
        var latest = history.GroupBy(x => (x.Surface, x.ThreadId)).Select(g => g.First()).ToArray();
        var comparisons = new List<CodexServerComparisonRow>();
        async Task<long?> LocalTotal(string predicate, params (string Name, object Value)[] parameters)
        {
            if (!tables.Contains("codex_native_token_events")) return null;
            command.Parameters.Clear();
            command.CommandText = $"SELECT COUNT(*),SUM(reported_total_tokens) FROM codex_native_token_events WHERE {predicate};";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync(token);
            await reader.ReadAsync(token);
            return reader.GetInt64(0) == 0 ? null : reader.GetInt64(1);
        }
        var accountIndex = 0;
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
            await using var duplicateReader = await command.ExecuteReaderAsync(token);
            await duplicateReader.ReadAsync(token);
            if (duplicateReader.GetInt64(0) > 0)
                comparisons.Add(new("Local repeated-event fingerprint candidates", null, duplicateReader.GetInt64(1), null,
                    AccountEvidenceClass.Unattributed,
                    $"{duplicateReader.GetInt64(0):N0} identical session/time/model/effort/token fingerprints occur in multiple physical sources. Extra retained tokens shown, not automatically deduplicated or proven independent usage."));
        }
        foreach (var cohort in history.Where(x => x.Activity is not null && x.State is ServerEvidenceState.Available or ServerEvidenceState.Empty)
                     .GroupBy(x => (x.CorrelatedAccountKey, x.AccountEvidence)))
        {
            var row = cohort.First();
            var label = $"account-{++accountIndex}";
            comparisons.Add(new(label + " lifetime / retained local total", row.Activity!.LifetimeTokens, await LocalTotal("1=1"), null,
                row.AccountEvidence, $"Backend fetched {row.CollectedAtUtc:g}. Local retained history has no guaranteed lifetime coverage; ratio is not account attribution."));
            foreach (var day in row.Activity.DailyUsageBuckets?.OrderBy(x => x.StartDate).TakeLast(30) ?? [])
                comparisons.Add(new(label + " day " + day.StartDate, day.Tokens,
                    await LocalTotal("substr(observed_at_utc,1,10)=$date", ("$date", day.StartDate)), null, row.AccountEvidence,
                    "Date-label comparison using local event UTC dates. Backend bucket timezone is unspecified; not a validated interval coverage fraction."));
            var prior = cohort.Skip(1).FirstOrDefault(x => x.Activity?.LifetimeTokens is not null && x.CollectedAtUtc < row.CollectedAtUtc);
            if (row.CorrelatedAccountKey is not null && row.AccountEvidence == AccountEvidenceClass.ServerCorrelated &&
                prior?.Activity?.LifetimeTokens is { } earlier && row.Activity.LifetimeTokens is { } current)
                comparisons.Add(new(label + " lifetime change", current >= earlier ? current - earlier : null,
                    await LocalTotal("observed_at_utc > $from AND observed_at_utc <= $to", ("$from", Utc(prior.CollectedAtUtc)), ("$to", Utc(row.CollectedAtUtc))),
                    null, row.AccountEvidence, current < earlier ? "Backend lifetime counter regressed; no positive coverage ratio inferred." :
                    "Fetch-to-fetch comparison only. Backend accounting lag, window boundaries and late local ingestion remain unknown."));
        }
        var threadIndex = 0;
        foreach (var row in latest.Where(x => x.ThreadUsage is not null && x.State == ServerEvidenceState.Available).Take(60))
        {
            var usage = row.ThreadUsage!;
            var label = $"thread-{++threadIndex}";
            var local = await LocalTotal("session_id=$thread", ("$thread", usage.ThreadId));
            long? completeTotal = usage.Groups.Count > 0 && usage.Groups.All(x => x.TotalTokens is not null)
                ? usage.Groups.Sum(x => x.TotalTokens!.Value) : null;
            comparisons.Add(new(label, completeTotal, local, usage.EstimatedUsageCreditsMicros, row.AccountEvidence,
                $"Exact thread-ID join; fetched {row.CollectedAtUtc:g}. Local coverage can be partial. USD micros: {usage.EstimatedUsageUsdMicros?.ToString() ?? "unavailable"}. Credits are estimates, not allowance %."));
            foreach (var group in usage.Groups.Take(64))
            {
                var groupLocal = await LocalTotal("session_id=$thread AND model IS $model AND reasoning_effort IS $effort",
                    ("$thread", usage.ThreadId), ("$model", group.Model ?? (object)DBNull.Value), ("$effort", group.ReasoningEffort ?? (object)DBNull.Value));
                var localCategories = "unavailable";
                if (groupLocal is not null)
                {
                    // Reuse the exact scope parameters; keep five local categories disjoint.
                    command.CommandText = """
                        SELECT SUM(uncached_input_tokens),SUM(cache_read_tokens),SUM(cache_write_tokens),
                            SUM(non_reasoning_output_tokens),SUM(reasoning_output_tokens)
                        FROM codex_native_token_events WHERE session_id=$thread AND model IS $model AND reasoning_effort IS $effort;
                        """;
                    await using var reader = await command.ExecuteReaderAsync(token);
                    await reader.ReadAsync(token);
                    localCategories = string.Join("/", Enumerable.Range(0, 5).Select(i => reader.GetInt64(i)));
                }
                comparisons.Add(new($"{label} · {group.Model ?? "unknown model"}/{group.ReasoningEffort ?? "unknown effort"}/{group.Speed ?? "unknown speed"}",
                    group.TotalTokens, groupLocal, group.EstimatedUsageCreditsMicros, row.AccountEvidence,
                    $"Provider net-new/cache/input/output: {group.NetNewInputTokens}/{group.CachedInputTokens}/{group.InputTokens}/{group.OutputTokens}. " +
                    $"Local uncached/cache-read/cache-write/non-reasoning-output/reasoning: {localCategories}. Local comparison joins model/effort only; speed is unavailable locally. Overlapping group comparisons must not be summed."));
            }
        }
        transaction.Commit();
        return new(DateTimeOffset.UtcNow, latest, comparisons, associations?.Invoke().Count(x => x.IsValid) ?? 0,
            "Source observations remain separate. No ratios assert identical accounting units, no native rollout account IDs are rewritten, and no association is inferred from numeric similarity. " +
            "Read bounded to the latest 2,000 fetch results / 16 MiB of JSON characters, 60 thread comparisons and 30 daily labels per account. Latest failed/null reports do not become zero or silently reuse a successful thread estimate. " +
            "Plan history/grouped analytics have no supported app-server seam: coverage, completeness, approximation and breakdowns are unavailable, not false/zero. " +
            "Credits→historical allowance mapping and held-out calibration cannot be evaluated without those labels. TT and live promotion policy are unchanged.");
    }
    private async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { await connection.OpenAsync(token); return connection; } catch { await connection.DisposeAsync(); throw; }
    }
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
