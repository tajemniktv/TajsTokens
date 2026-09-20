using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

/// <summary>Single product projection over existing evidence and runtime owners. Never starts a collector.</summary>
public sealed class CodexIntelligenceEngine(string databasePath, IIntelligenceService intelligence,
    Func<TelemetrySnapshot> current, Func<IReadOnlyList<RolloutAccountAssociation>> associations) : ICodexIntelligence
{
    public CodexIntelligenceSnapshot Current => CodexIntelligenceProjection.Live(current());
    public Task<ScenarioEstimate> SimulateAsync(ScenarioRequest request, DateTimeOffset historyFromUtc, CancellationToken token) =>
        intelligence.EstimateScenarioAsync(request, historyFromUtc, token);

    public Task<CodexIntelligenceSnapshot> QueryAsync(CodexSelection selection, CancellationToken token) =>
        Task.Run(() => ReadAsync(selection, token), token);

    public Task<CodexIntelligenceSnapshot> AnalyzeAsync(CodexIntelligenceSnapshot snapshot, CancellationToken token) => Task.Run(async () =>
    {
        if (snapshot.Selection.HasWorkFilter) throw new InvalidOperationException("Clear model/project/chat filters for compatible account-wide accounting analysis; quota is not attributable to filtered work.");
        var data = await new SqliteForecastDatasetReader(databasePath, associations()).ReadAsync("codex", "default",
            snapshot.Selection.FromUtc, snapshot.Selection.ToUtc, token, snapshot.Selection.AccountKey);
        var cost = QuotaCostEvaluation.Evaluate(data, token);
        var regime = CodexRegimeModel.Analyze(cost, token);
        return snapshot with { Accounting = cost, Regime = regime,
            Manifest = CodexIntelligenceProjection.Manifest(snapshot.Selection, Fingerprint(new { snapshot.Manifest.Id, cost, regime })) };
    }, token);

    private static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private async Task<CodexIntelligenceSnapshot> ReadAsync(CodexSelection selection, CancellationToken token)
    {
        if (selection.FromUtc >= selection.ToUtc || selection.ToUtc - selection.FromUtc > TimeSpan.FromDays(366))
            throw new ArgumentException("Select an ordered range of at most 366 days.", nameof(selection));
        var asOf = DateTimeOffset.UtcNow;
        var claims = associations().Where(x => x.IsValid && x.AssertedAtUtc <= asOf).ToArray();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(token);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.source_event_id,
              (SELECT CASE WHEN COUNT(*)=1 THEN MAX(f.source_identity) END FROM rollout_files f WHERE f.file_path=e.source_file),
              e.session_id,COALESCE(s.repository,'(unknown)'),e.observed_at_utc,e.captured_at_utc,
              e.model,e.reasoning_effort,e.uncached_input_tokens,e.cache_read_tokens,e.cache_write_tokens,
              e.non_reasoning_output_tokens,e.reasoning_output_tokens,e.reported_total_tokens,COALESCE(s.thread_id,e.session_id)
            FROM codex_native_token_events e LEFT JOIN sessions s ON s.session_id=e.session_id
            WHERE e.observed_at_utc >= $from AND e.observed_at_utc < $to
            ORDER BY e.observed_at_utc,e.source_event_id LIMIT 500001;
            """;
        command.Parameters.AddWithValue("$from", selection.FromUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$to", selection.ToUtc.ToUniversalTime().ToString("O"));
        var ledger = new List<CodexLedgerEntry>();
        DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var at = Time(reader.GetString(4));
                var identity = reader.IsDBNull(1) ? null : reader.GetString(1);
                var session = reader.GetString(2);
                var matching = claims.Where(x => x.SourceIdentity == identity && x.SessionId == session &&
                    x.Profile == "default" && at >= x.FromUtc && at <= x.ThroughUtc).Select(x => x.AccountKey).Distinct().ToArray();
                var captured = reader.IsDBNull(5) ? (DateTimeOffset?)null : Time(reader.GetString(5));
                var work = new CodexPredictiveTokenEvent(session, at, captured,
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13));
                ledger.Add(new(reader.GetString(0), identity, reader.GetString(14), reader.GetString(3), at, captured,
                    matching.Length == 1 ? matching[0] : null,
                    matching.Length == 1 ? AccountEvidenceClass.UserDeclaredSingleAccount :
                    matching.Length > 1 ? AccountEvidenceClass.Conflicting : AccountEvidenceClass.Unattributed, work));
            }
        }
        if (ledger.Count > 500000) throw new InvalidOperationException("Ledger exceeds 500,000 events; narrow the range. No partial total is displayed.");
        var selected = CodexIntelligenceProjection.Select(ledger, selection);
        command.CommandText = """
            SELECT evidence_json,activity_bucket_set_id FROM codex_server_evidence
            WHERE collected_at_utc >= $from AND collected_at_utc <= $asof AND surface<>'QuotaMetadata'
              AND ($account IS NULL OR correlated_account_key=$account OR correlated_account_key IS NULL)
            ORDER BY collected_at_utc DESC,observation_id DESC LIMIT 1001;
            """;
        command.Parameters.AddWithValue("$asof", asOf.ToString("O"));
        command.Parameters.AddWithValue("$account", selection.AccountKey ?? (object)DBNull.Value);
        var evidence = new List<CodexServerObservation>();
        long evidenceBytes = 0;
        var evidenceTruncated = false;
        var invalidEvidence = 0;
        var bucketSets = new Dictionary<string, long>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var json = reader.GetString(0);
                evidenceBytes += json.Length * 2L;
                if (evidence.Count >= 1000 || evidenceBytes > 16 * 1024 * 1024) { evidenceTruncated = true; break; }
                try
                {
                    var row = JsonSerializer.Deserialize<CodexServerObservation>(json);
                    if (row is not null)
                    {
                        evidence.Add(row);
                        if (!reader.IsDBNull(1)) bucketSets[row.Id] = reader.GetInt64(1);
                    }
                    else invalidEvidence++;
                }
                catch (JsonException) { invalidEvidence++; }
            }
        }
        var dayCache = new Dictionary<long, IReadOnlyList<CodexAccountDay>>();
        for (var i = 0; i < evidence.Count; i++)
        {
            if (evidence[i].Activity is not { } activity || !bucketSets.TryGetValue(evidence[i].Id, out var set)) continue;
            if (!dayCache.TryGetValue(set, out var days))
            {
                days = await CodexServerEvidenceStorage.ReadDaysAsync(connection, transaction, set, token);
                evidenceBytes += days.Count * 64L;
                if (evidenceBytes > 16 * 1024 * 1024) { evidenceTruncated = true; break; }
                dayCache.Add(set, days);
            }
            evidence[i] = evidence[i] with { Activity = activity with { DailyUsageBuckets = days } };
        }
        transaction.Commit();
        // Forecast dataset is a separately captured read; the snapshot records that distinction.
        var dataset = await new SqliteForecastDatasetReader(databasePath, claims).ReadAsync("codex", "default",
            selection.FromUtc, selection.ToUtc, token, selection.AccountKey);
        var quota = selection.HasWorkFilter ? [] : dataset.Quota.Where(x =>
            x.CapturedAtUtc < selection.ToUtc && (selection.AccountKey is null || x.AccountKey == selection.AccountKey)).ToArray();
        var live = Current;
        var liveScope = !selection.HasWorkFilter && selection.FromUtc <= asOf && asOf - selection.ToUtc < TimeSpan.FromMinutes(5);
        var forecasts = liveScope ? live.Current.Where(x => selection.AccountKey is null || x.Current.AccountKey == selection.AccountKey).ToArray() : [];
        var health = new List<CodexEvidenceHealth>
        {
            new("Local workload", selected.Count == 0 ? "Empty" : "Observed",
                $"{selected.Count:N0} canonical increments; {selected.Count(x => x.Ownership == AccountEvidenceClass.Unattributed):N0} unattributed, {selected.Count(x => x.Ownership == AccountEvidenceClass.UserDeclaredSingleAccount):N0} user-associated. Association is not native account identity."),
            new("Quota", selection.HasWorkFilter ? "Scope unavailable" : "Reported",
                selection.HasWorkFilter ? "Account quota cannot be attributed to a model, project or chat filter. It is withheld, not prorated by tokens." : "Source/account lanes remain separate. Reset drops are not negative consumption; polls are not additive."),
            new("Provider analytics", evidenceTruncated ? "Partial" : "Shadow",
                $"At most 1,000 captures / 16 MiB are inspected; {invalidEvidence} malformed captures unavailable. Reports may revise after the selected event range. They are not production calibration labels."),
            new("Snapshot", "Multiple source captures", $"Ledger request started at {asOf:O}; forecast dataset captured at {dataset.CapturedAtUtc:O}. Not an atomic cross-source snapshot."),
            new("TT", "Research only", "No runtime TT basis has been promoted. Missing TT is not zero workload."),
            new("API equivalent", "Derived", ApiPriceWorkload.Assumptions)
        };
        return new CodexIntelligenceSnapshot(asOf, selection, selected, CodexIntelligenceProjection.Buckets(selected, (selection.ToUtc - selection.FromUtc).TotalDays <= 2),
            CodexIntelligenceProjection.Groups(selected), quota, forecasts,
            liveScope && selection.AccountKey is null ? live.Workload : null,
            ApiPriceWorkload.Calculate(selected.Select(x => x.Workload).Select(x =>
                (decimal)x.UncachedInputTokens + x.CacheReadTokens + x.CacheWriteTokens + x.NonReasoningOutputTokens + x.ReasoningOutputTokens == x.ReportedTotalTokens
                    ? x : x with { Model = null })),
            evidence.Where(x => (selection.AccountKey is null || x.CorrelatedAccountKey == selection.AccountKey || x.CorrelatedAccountKey is null) &&
                (!selection.HasWorkFilter || selection.ThreadId is not null && x.ThreadId == selection.ThreadId && selection.Model is null && selection.Project is null)).ToArray(),
            health, CodexIntelligenceProjection.Manifest(selection, Fingerprint(new { selected, quota, forecasts,
                evidenceIds = evidence.Select(x => x.Id).ToArray() })))
        {
            RuntimeEvidence = live.RuntimeEvidence,
            Chats = CodexProviderReconciliation.Tasks(evidence, selected, selection),
            HistoricalPeriods = selection.HasWorkFilter ? [] : CodexProviderReconciliation.Analyze(evidence, selected, quota, selection, asOf)
        };
    }
}
