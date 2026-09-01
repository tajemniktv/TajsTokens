using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Phase 4 intelligence boundary. Heavy historical aggregation stays in SQLite/background code and
/// the App receives bounded normalized models rather than raw SQL/result sets.
/// </summary>
public sealed class SqliteIntelligenceService : IIntelligenceService
{
    private const int MaxBurnIntervals = 80;
    private readonly string _connectionString;
    private readonly SqliteTelemetryRepository _telemetryRepository;
    private readonly ForecastingService _forecasting = new();
    private readonly QuotaResetDetector _resetDetector = new();
    private readonly ScenarioPlannerService _scenarioPlanner = new();
    private readonly Func<string, CancellationToken, Task>? _queryStageObserver;

    public SqliteIntelligenceService(string databasePath, SqliteTelemetryRepository telemetryRepository)
        : this(databasePath, telemetryRepository, null)
    {
    }

    internal SqliteIntelligenceService(
        string databasePath,
        SqliteTelemetryRepository telemetryRepository,
        Func<string, CancellationToken, Task>? queryStageObserver)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        _telemetryRepository = telemetryRepository;
        _queryStageObserver = queryStageObserver;
    }

    public async Task<IntelligenceRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var groups = await LoadRecentQuotaGroupsAsync(512, cancellationToken);
        var resetEventsDetected = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(snapshot => snapshot.CapturedAtUtc).ToArray();
            foreach (var resetEvent in _resetDetector.Detect(ordered))
            {
                resetEventsDetected += await _telemetryRepository.UpsertQuotaResetEventAsync(resetEvent, cancellationToken);
            }
        }

        // Current forecasts are owned by BuildAndPersistCurrentForecastsAsync and anchored to the
        // coordinator's provider-authoritative current lanes. Historical refresh must not recompute
        // a competing "current" generation from whichever persisted sample happens to be newest.
        return new IntelligenceRefreshResult(0, resetEventsDetected);
    }

    public async Task<IReadOnlyList<CurrentQuotaForecast>> BuildAndPersistCurrentForecastsAsync(
        IReadOnlyList<QuotaLaneState> quotaLanes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var results = new List<CurrentQuotaForecast>();
        foreach (var lane in quotaLanes.Where(lane => lane.Snapshot is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = lane.Snapshot!;
            if (!lane.IsFresh)
            {
                results.Add(new CurrentQuotaForecast(
                    current, TelemetryHealthState.Stale, null,
                    "Forecasting is paused until this quota lane is provider-fresh.",
                    "Last-known-good quota is displayed without borrowing a forecast from another generation."));
                continue;
            }

            if (current.Authority != QuotaObservationAuthority.ProviderAuthoritative)
            {
                results.Add(new CurrentQuotaForecast(
                    current, TelemetryHealthState.Unavailable, null,
                    "Current forecasts require a provider-authoritative quota anchor.",
                    $"Current source '{current.Source}' is not classified as provider-authoritative."));
                continue;
            }

            if (current.CapturedAtUtc > nowUtc)
            {
                results.Add(new CurrentQuotaForecast(
                    current,
                    TelemetryHealthState.Live,
                    null,
                    "Forecasting is paused for a future-dated quota anchor.",
                    "The provider-authoritative anchor is newer than the forecast evaluation time."));
                continue;
            }

            var history = await _telemetryRepository.GetRecentQuotaSnapshotsAsync(
                current.Kind,
                current.Provider,
                current.Profile,
                512,
                cancellationToken,
                current.CapturedAtUtc);
            var anchored = history
                .Where(item => item.CapturedAtUtc <= current.CapturedAtUtc)
                .GroupBy(item => item.CapturedAtUtc)
                .Select(group => group
                    .OrderByDescending(item => item.Authority)
                    .First())
                .Where(item => item.CapturedAtUtc != current.CapturedAtUtc)
                .Append(current)
                .OrderBy(item => item.CapturedAtUtc)
                .ToArray();

            try
            {
                var forecast = _forecasting.BuildForecast(anchored, nowUtc);
                var persisted = new ForecastSnapshot(
                    current.Provider, current.Profile, forecast,
                    current.Source,
                    current.Authority,
                    current.CapturedAtUtc,
                    current.WindowMinutes,
                    current.ResetsAtUtc);
                await _telemetryRepository.UpsertForecastSnapshotAsync(persisted, cancellationToken);
                results.Add(new CurrentQuotaForecast(
                    current, TelemetryHealthState.Live, forecast,
                    "Provider-authoritative current quota plus compatible persisted history no newer than the current anchor."));
            }
            catch (ArgumentException exception)
            {
                results.Add(new CurrentQuotaForecast(
                    current, TelemetryHealthState.Live, null,
                    "Provider-authoritative current quota; more compatible history is required.",
                    exception.Message));
            }
        }

        return results;
    }

    public async Task<IntelligenceDashboard> QueryAsync(
        IntelligenceQuery query,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var effective = NormalizeQuery(query);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Keep every constituent SELECT on the same SQLite read snapshot. QueryAsync returns one
        // dashboard generation, not a collage assembled across commits that happened mid-query.
        await BeginReadSnapshotAsync(connection, cancellationToken);
        try
        {
            var hasNativeEvents = await TableExistsAsync(connection, "codex_native_token_events", cancellationToken);
            var hasContext = await TableExistsAsync(connection, "context_observations", cancellationToken);
            var usage = hasNativeEvents
                ? await LoadUsageHistoryAsync(connection, effective, hasContext, cancellationToken)
                : [];
            if (_queryStageObserver is not null)
            {
                await _queryStageObserver("usage-loaded", cancellationToken);
            }
            var dimensions = hasNativeEvents
                ? await LoadDimensionsAsync(connection, effective, cancellationToken)
                : [];
            var heatmap = hasNativeEvents
                ? await LoadHeatmapAsync(connection, effective, cancellationToken)
                : [];
            var burnIntervals = await LoadQuotaBurnIntervalsAsync(
                connection,
                effective.FromUtc,
                effective.ToUtc,
                MaxBurnIntervals,
                hasNativeEvents,
                hasContext,
                cancellationToken);
            var resets = await LoadResetEventsAsync(connection, effective.FromUtc, effective.ToUtc, 200, cancellationToken);
            var fiveHourForecasts = await LoadForecastHistoryAsync(
                connection,
                QuotaWindowKind.FiveHour,
                effective.FromUtc,
                effective.ToUtc,
                500,
                cancellationToken);
            var weeklyForecasts = await LoadForecastHistoryAsync(
                connection,
                QuotaWindowKind.Weekly,
                effective.FromUtc,
                effective.ToUtc,
                500,
                cancellationToken);

            var dashboard = new IntelligenceDashboard(
                effective,
                usage,
                dimensions,
                heatmap,
                burnIntervals,
                resets,
                fiveHourForecasts,
                weeklyForecasts);
            await CommitReadSnapshotAsync(connection, cancellationToken);
            return dashboard;
        }
        catch
        {
            await TryRollbackReadTransactionAsync(connection);
            throw;
        }
    }

    public async Task<QuotaBurnDetail> GetQuotaBurnDetailAsync(
        QuotaBurnInterval interval,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interval);
        await EnsureInitializedAsync(cancellationToken);
        take = Math.Clamp(take, 1, 50);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var currentIntervals = await LoadQuotaBurnIntervalsAsync(
            connection,
            interval.StartUtc.AddSeconds(-1),
            interval.EndUtc.AddSeconds(1),
            400,
            false,
            false,
            cancellationToken,
            interval.Kind);
        var currentInterval = currentIntervals.FirstOrDefault(candidate =>
            string.Equals(candidate.IntervalId, interval.IntervalId, StringComparison.Ordinal) &&
            string.Equals(candidate.Provider, interval.Provider, StringComparison.Ordinal) &&
            string.Equals(candidate.Profile, interval.Profile, StringComparison.Ordinal) &&
            candidate.StartUtc == interval.StartUtc &&
            candidate.EndUtc == interval.EndUtc);
        if (currentInterval is null)
        {
            return new QuotaBurnDetail(
                interval,
                [],
                "This quota-burn interval is no longer present in the current provider history. Refresh analytics before requesting contributor attribution.");
        }

        if (!await TableExistsAsync(connection, "codex_native_token_events", cancellationToken))
        {
            return new QuotaBurnDetail(currentInterval, [], "No native Codex token events are available for this interval yet.");
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.session_id,
                   COALESCE(MAX(a.name), e.session_id),
                   COALESCE(MAX(s.repository), 'unknown'),
                   MAX(COALESCE(a.model, e.model)),
                   MAX(e.reasoning_effort),
                   CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id) THEN 1 ELSE 0 END,
                   COALESCE(SUM(e.reported_total_tokens), 0),
                   COALESCE(SUM(e.uncached_input_tokens), 0),
                   COALESCE(SUM(e.cache_read_tokens), 0),
                   COALESCE((SELECT COUNT(*)
                             FROM context_observations c
                             WHERE c.session_id = e.session_id
                               AND c.is_compaction = 1
                               AND c.observed_at_utc > $from
                               AND c.observed_at_utc <= $to), 0)
            FROM codex_native_token_events e
            LEFT JOIN sessions s ON s.session_id = e.session_id
            LEFT JOIN agents a ON a.agent_id = e.session_id
            WHERE e.observed_at_utc > $from AND e.observed_at_utc <= $to
            GROUP BY e.session_id
            ORDER BY SUM(e.reported_total_tokens) DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(currentInterval.StartUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(currentInterval.EndUtc));
        command.Parameters.AddWithValue("$take", take);

        var raw = new List<(string SessionId, string Name, string Repository, string? Model, string? Reasoning, bool IsSubagent, long Tokens, long Uncached, long CacheRead, int Compactions)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                raw.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5) != 0,
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt32(9)));
            }
        }

        var totalTokens = Math.Max(1L, raw.Sum(row => row.Tokens));
        var totalUncached = Math.Max(1L, raw.Sum(row => row.Uncached));
        var totalCacheRead = Math.Max(1L, raw.Sum(row => row.CacheRead));
        var totalCompactions = Math.Max(1, raw.Sum(row => row.Compactions));
        var contributors = raw.Select(row =>
        {
            var tokenShare = row.Tokens / (double)totalTokens;
            var score = (0.55 * tokenShare) +
                        (0.25 * row.Uncached / totalUncached) +
                        (0.15 * row.CacheRead / totalCacheRead) +
                        (0.05 * row.Compactions / totalCompactions);
            return new QuotaContributor(
                row.SessionId,
                row.Name,
                row.Repository,
                row.Model,
                row.Reasoning,
                row.IsSubagent,
                row.Tokens,
                row.Uncached,
                row.CacheRead,
                row.Compactions,
                tokenShare,
                Math.Clamp(score, 0, 1));
        }).ToArray();

        return new QuotaBurnDetail(
            currentInterval,
            contributors,
            "Estimated attribution only. Score = 55% native token share + 25% uncached-input share + 15% cache-read share + 5% compaction share inside the provider-observed quota-change interval. The quota meter can be rounded/delayed, so no single event is claimed to have directly caused the change.");
    }

    public async Task<ScenarioEstimate> EstimateScenarioAsync(
        ScenarioRequest request,
        DateTimeOffset historyFromUtc,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (historyFromUtc >= now)
        {
            historyFromUtc = now.AddDays(-30);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var hasNativeEvents = await TableExistsAsync(connection, "codex_native_token_events", cancellationToken);
        var hasContext = await TableExistsAsync(connection, "context_observations", cancellationToken);
        var fiveHourIntervals = await LoadQuotaBurnIntervalsAsync(
            connection,
            historyFromUtc,
            now,
            400,
            hasNativeEvents,
            hasContext,
            cancellationToken,
            QuotaWindowKind.FiveHour);
        var weeklyIntervals = await LoadQuotaBurnIntervalsAsync(
            connection,
            historyFromUtc,
            now,
            400,
            hasNativeEvents,
            hasContext,
            cancellationToken,
            QuotaWindowKind.Weekly);
        var samples = fiveHourIntervals.Concat(weeklyIntervals).Select(interval => new ScenarioHistorySample(
            interval.Kind,
            interval.StartUtc,
            interval.EndUtc,
            interval.DeltaUsedPercent,
            interval.RootSessions,
            interval.SubagentSessions,
            interval.DominantModel,
            interval.DominantReasoningEffort)).ToArray();
        return _scenarioPlanner.Estimate(request, samples, now);
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
        _telemetryRepository.InitializeIntelligenceAsync(cancellationToken);

    private async Task<IReadOnlyList<IReadOnlyList<QuotaSnapshot>>> LoadRecentQuotaGroupsAsync(
        int perGroup,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            WITH ranked AS (
                SELECT kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, provider, profile, source,
                       ROW_NUMBER() OVER (
                           PARTITION BY provider, profile, kind
                           ORDER BY captured_at_utc DESC) AS rn
                FROM quota_snapshots
                WHERE kind IN ('FiveHour', 'Weekly')
            )
            SELECT kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, provider, profile, source
            FROM ranked
            WHERE rn <= $take
            ORDER BY provider, profile, kind, captured_at_utc;
            """;
        command.Parameters.AddWithValue("$take", Math.Clamp(perGroup, 2, 2048));

        var rows = new List<QuotaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadQuotaSnapshot(reader));
        }

        return rows
            .GroupBy(row => (row.Provider, row.Profile, row.Kind))
            .Select(group => (IReadOnlyList<QuotaSnapshot>)CanonicalizeQuotaSnapshots(group).ToArray())
            .ToArray();
    }

    private static IntelligenceQuery NormalizeQuery(IntelligenceQuery query)
    {
        var from = query.FromUtc.ToUniversalTime();
        var to = query.ToUtc.ToUniversalTime();
        if (to <= from)
        {
            throw new ArgumentException("Analytics end time must be after the start time.", nameof(query));
        }

        var maxBuckets = Math.Clamp(query.MaxBuckets, 24, 2000);
        var size = query.BucketSize;
        var span = to - from;
        if (size == AnalyticsBucketSize.Minute && span.TotalMinutes > maxBuckets)
        {
            size = AnalyticsBucketSize.Hour;
        }
        if (size == AnalyticsBucketSize.Hour && span.TotalHours > maxBuckets)
        {
            size = AnalyticsBucketSize.Day;
        }
        if (size == AnalyticsBucketSize.Day && span.TotalDays > maxBuckets)
        {
            from = to.AddDays(-maxBuckets);
        }

        return new IntelligenceQuery(from, to, size, maxBuckets);
    }

    private static async Task<IReadOnlyList<UsageHistoryBucket>> LoadUsageHistoryAsync(
        SqliteConnection connection,
        IntelligenceQuery query,
        bool hasContext,
        CancellationToken cancellationToken)
    {
        var bucketExpression = BucketSql(query.BucketSize, "e.observed_at_utc");
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {bucketExpression} AS bucket,
                   COALESCE(SUM(e.reported_total_tokens), 0),
                   COALESCE(SUM(e.uncached_input_tokens), 0),
                   COALESCE(SUM(e.cache_read_tokens), 0),
                   COALESCE(SUM(e.cache_write_tokens), 0),
                   COALESCE(SUM(e.non_reasoning_output_tokens), 0),
                   COALESCE(SUM(e.reasoning_output_tokens), 0),
                   COALESCE(SUM(CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id)
                                     THEN 0 ELSE e.reported_total_tokens END), 0),
                   COALESCE(SUM(CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id)
                                     THEN e.reported_total_tokens ELSE 0 END), 0),
                   COUNT(DISTINCT e.session_id)
            FROM codex_native_token_events e
            WHERE e.observed_at_utc >= $from AND e.observed_at_utc < $to
            GROUP BY bucket
            ORDER BY bucket DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(query.FromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(query.ToUtc));
        command.Parameters.AddWithValue("$take", query.MaxBuckets);

        var buckets = new Dictionary<DateTimeOffset, MutableBucket>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var start = ParseUtc(reader.GetString(0));
                buckets[start] = new MutableBucket
                {
                    NativeTokens = reader.GetInt64(1),
                    Uncached = reader.GetInt64(2),
                    CacheRead = reader.GetInt64(3),
                    CacheWrite = reader.GetInt64(4),
                    NonReasoningOutput = reader.GetInt64(5),
                    ReasoningOutput = reader.GetInt64(6),
                    RootTokens = reader.GetInt64(7),
                    SubagentTokens = reader.GetInt64(8),
                    ActiveSessions = reader.GetInt32(9)
                };
            }
        }

        if (hasContext)
        {
            var compactions = connection.CreateCommand();
            compactions.CommandText = $"""
                SELECT {BucketSql(query.BucketSize, "c.observed_at_utc")} AS bucket, COUNT(*)
                FROM context_observations c
                WHERE c.is_compaction = 1 AND c.observed_at_utc >= $from AND c.observed_at_utc < $to
                GROUP BY bucket;
                """;
            compactions.Parameters.AddWithValue("$from", SerializeUtc(query.FromUtc));
            compactions.Parameters.AddWithValue("$to", SerializeUtc(query.ToUtc));
            await using var reader = await compactions.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var start = ParseUtc(reader.GetString(0));
                if (!buckets.TryGetValue(start, out var bucket))
                {
                    bucket = new MutableBucket();
                    buckets[start] = bucket;
                }
                bucket.Compactions = reader.GetInt32(1);
            }
        }

        var quota = await LoadQuotaSnapshotsAsync(connection, query.FromUtc.AddDays(-7), query.ToUtc, cancellationToken);
        foreach (var group in quota.GroupBy(snapshot => (snapshot.Provider, snapshot.Profile, snapshot.Kind)))
        {
            var ordered = group.OrderBy(snapshot => snapshot.CapturedAtUtc).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (current.CapturedAtUtc < query.FromUtc || current.CapturedAtUtc >= query.ToUtc ||
                    previous.UsedPercent is null || current.UsedPercent is null ||
                    previous.WindowMinutes != current.WindowMinutes ||
                    previous.ResetsAtUtc is null || current.ResetsAtUtc is null ||
                    previous.ResetsAtUtc != current.ResetsAtUtc)
                {
                    continue;
                }

                var delta = current.UsedPercent.Value - previous.UsedPercent.Value;
                if (delta <= 0)
                {
                    continue;
                }

                var start = BucketStart(current.CapturedAtUtc, query.BucketSize);
                if (!buckets.TryGetValue(start, out var bucket))
                {
                    bucket = new MutableBucket();
                    buckets[start] = bucket;
                }
                if (current.Kind == QuotaWindowKind.FiveHour)
                {
                    bucket.FiveHourQuotaDelta = (bucket.FiveHourQuotaDelta ?? 0) + delta;
                }
                else if (current.Kind == QuotaWindowKind.Weekly)
                {
                    bucket.WeeklyQuotaDelta = (bucket.WeeklyQuotaDelta ?? 0) + delta;
                }
            }
        }

        return buckets
            .Where(pair => pair.Key >= query.FromUtc && pair.Key < query.ToUtc)
            .OrderByDescending(pair => pair.Key)
            .Take(query.MaxBuckets)
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.ToModel(pair.Key, BucketEnd(pair.Key, query.BucketSize)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<UsageDimensionTotal>> LoadDimensionsAsync(
        SqliteConnection connection,
        IntelligenceQuery query,
        CancellationToken cancellationToken)
    {
        var results = new List<UsageDimensionTotal>();
        await AppendDimensionAsync(
            connection,
            results,
            "Session repository",
            "COALESCE(NULLIF(s.repository, ''), '(unknown)')",
            "LEFT JOIN sessions s ON s.session_id = e.session_id",
            query,
            cancellationToken);
        await AppendDimensionAsync(
            connection,
            results,
            "Model",
            "COALESCE(NULLIF(e.model, ''), '(unknown)')",
            string.Empty,
            query,
            cancellationToken);
        await AppendDimensionAsync(
            connection,
            results,
            "Agent role",
            "CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id) THEN 'Subagent' ELSE 'Root' END",
            string.Empty,
            query,
            cancellationToken);
        return results;
    }

    private static async Task AppendDimensionAsync(
        SqliteConnection connection,
        ICollection<UsageDimensionTotal> results,
        string dimension,
        string keyExpression,
        string join,
        IntelligenceQuery query,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {keyExpression} AS dimension_key,
                   COALESCE(SUM(e.reported_total_tokens), 0),
                   COALESCE(SUM(e.uncached_input_tokens), 0),
                   COALESCE(SUM(e.cache_read_tokens), 0),
                   COALESCE(SUM(e.cache_write_tokens), 0),
                   COALESCE(SUM(e.non_reasoning_output_tokens), 0),
                   COALESCE(SUM(e.reasoning_output_tokens), 0),
                   COUNT(DISTINCT e.session_id)
            FROM codex_native_token_events e
            {join}
            WHERE e.observed_at_utc >= $from AND e.observed_at_utc < $to
            GROUP BY dimension_key
            ORDER BY SUM(e.reported_total_tokens) DESC
            LIMIT 20;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(query.FromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(query.ToUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsageDimensionTotal(
                dimension,
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7)));
        }
    }

    private static async Task<IReadOnlyList<UsageHeatmapCell>> LoadHeatmapAsync(
        SqliteConnection connection,
        IntelligenceQuery query,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(strftime('%w', e.observed_at_utc) AS INTEGER),
                   CAST(strftime('%H', e.observed_at_utc) AS INTEGER),
                   COALESCE(SUM(e.reported_total_tokens), 0),
                   COUNT(DISTINCT strftime('%Y-%m-%dT%H', e.observed_at_utc))
            FROM codex_native_token_events e
            WHERE e.observed_at_utc >= $from AND e.observed_at_utc < $to
            GROUP BY 1, 2
            ORDER BY 1, 2;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(query.FromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(query.ToUtc));

        var results = new List<UsageHeatmapCell>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UsageHeatmapCell(
                (DayOfWeek)reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt32(3)));
        }
        return results;
    }

    private static async Task<IReadOnlyList<QuotaBurnInterval>> LoadQuotaBurnIntervalsAsync(
        SqliteConnection connection,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        bool hasNativeEvents,
        bool hasContext,
        CancellationToken cancellationToken,
        QuotaWindowKind? kindFilter = null)
    {
        var snapshots = await LoadQuotaSnapshotsAsync(connection, fromUtc.AddDays(-7), toUtc, cancellationToken);
        var candidates = new List<QuotaBurnIntervalSeed>();
        foreach (var group in snapshots.GroupBy(snapshot => (snapshot.Provider, snapshot.Profile, snapshot.Kind)))
        {
            if (kindFilter is QuotaWindowKind requestedKind && group.Key.Kind != requestedKind)
            {
                continue;
            }

            var ordered = group.OrderBy(snapshot => snapshot.CapturedAtUtc).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (current.CapturedAtUtc < fromUtc || current.CapturedAtUtc > toUtc ||
                    previous.UsedPercent is null || current.UsedPercent is null ||
                    previous.WindowMinutes != current.WindowMinutes ||
                    previous.ResetsAtUtc is null || current.ResetsAtUtc is null ||
                    previous.ResetsAtUtc != current.ResetsAtUtc)
                {
                    continue;
                }

                var delta = current.UsedPercent.Value - previous.UsedPercent.Value;
                if (delta <= 0)
                {
                    continue;
                }

                candidates.Add(new QuotaBurnIntervalSeed(previous, current, delta));
            }
        }

        var selected = candidates
            .OrderByDescending(seed => seed.Current.CapturedAtUtc)
            .Take(Math.Clamp(take, 1, 400))
            .ToArray();
        var results = new List<QuotaBurnInterval>(selected.Length);
        foreach (var seed in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var aggregate = hasNativeEvents
                ? await LoadIntervalAggregateAsync(connection, seed.Previous.CapturedAtUtc, seed.Current.CapturedAtUtc, hasContext, cancellationToken)
                : IntervalAggregate.Empty;
            var intervalMinutes = Math.Max(0.01, (seed.Current.CapturedAtUtc - seed.Previous.CapturedAtUtc).TotalMinutes);
            var sameSource = string.Equals(seed.Previous.Source, seed.Current.Source, StringComparison.OrdinalIgnoreCase);
            var confidence = 0.55 +
                             (sameSource ? 0.12 : 0) +
                             (intervalMinutes <= 30 ? 0.13 : intervalMinutes <= 90 ? 0.07 : 0) +
                             (aggregate.NativeTokens > 0 ? 0.1 : 0);

            results.Add(new QuotaBurnInterval(
                BuildIntervalId(seed.Previous, seed.Current),
                seed.Current.Kind,
                seed.Current.Provider,
                seed.Current.Profile,
                seed.Previous.CapturedAtUtc,
                seed.Current.CapturedAtUtc,
                seed.Previous.UsedPercent!.Value,
                seed.Current.UsedPercent!.Value,
                seed.Delta,
                seed.Current.ResetsAtUtc,
                seed.Previous.Source,
                seed.Current.Source,
                aggregate.NativeTokens,
                aggregate.RootTokens,
                aggregate.SubagentTokens,
                aggregate.RootSessions,
                aggregate.SubagentSessions,
                aggregate.Compactions,
                aggregate.DominantModel,
                aggregate.DominantReasoning,
                Math.Clamp(confidence, 0.35, 0.95)));
        }

        return results;
    }

    private static async Task<IntervalAggregate> LoadIntervalAggregateAsync(
        SqliteConnection connection,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool hasContext,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(e.reported_total_tokens), 0),
                   COALESCE(SUM(CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id)
                                     THEN 0 ELSE e.reported_total_tokens END), 0),
                   COALESCE(SUM(CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id)
                                     THEN e.reported_total_tokens ELSE 0 END), 0),
                   COUNT(DISTINCT CASE WHEN NOT EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id)
                                       THEN e.session_id END),
                   COUNT(DISTINCT CASE WHEN EXISTS(SELECT 1 FROM agent_relationships ar WHERE ar.child_agent_id = e.session_id)
                                       THEN e.session_id END)
            FROM codex_native_token_events e
            WHERE e.observed_at_utc > $from AND e.observed_at_utc <= $to;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(fromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var nativeTokens = reader.GetInt64(0);
        var rootTokens = reader.GetInt64(1);
        var subagentTokens = reader.GetInt64(2);
        var rootSessions = reader.GetInt32(3);
        var subagentSessions = reader.GetInt32(4);
        await reader.DisposeAsync();

        var compactions = 0;
        if (hasContext)
        {
            var compactionCommand = connection.CreateCommand();
            compactionCommand.CommandText = """
                SELECT COUNT(*) FROM context_observations
                WHERE is_compaction = 1 AND observed_at_utc > $from AND observed_at_utc <= $to;
                """;
            compactionCommand.Parameters.AddWithValue("$from", SerializeUtc(fromUtc));
            compactionCommand.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
            compactions = Convert.ToInt32(await compactionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        string? model = null;
        string? reasoning = null;
        if (nativeTokens > 0)
        {
            var dominant = connection.CreateCommand();
            dominant.CommandText = """
                SELECT e.model, e.reasoning_effort, SUM(e.reported_total_tokens) AS tokens
                FROM codex_native_token_events e
                WHERE e.observed_at_utc > $from AND e.observed_at_utc <= $to
                GROUP BY e.model, e.reasoning_effort
                ORDER BY tokens DESC
                LIMIT 1;
                """;
            dominant.Parameters.AddWithValue("$from", SerializeUtc(fromUtc));
            dominant.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
            await using var dominantReader = await dominant.ExecuteReaderAsync(cancellationToken);
            if (await dominantReader.ReadAsync(cancellationToken))
            {
                model = dominantReader.IsDBNull(0) ? null : dominantReader.GetString(0);
                reasoning = dominantReader.IsDBNull(1) ? null : dominantReader.GetString(1);
            }
        }

        return new IntervalAggregate(nativeTokens, rootTokens, subagentTokens, rootSessions, subagentSessions, compactions, model, reasoning);
    }

    private static async Task<IReadOnlyList<QuotaResetEvent>> LoadResetEventsAsync(
        SqliteConnection connection,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, kind, provider, profile, detected_at_utc, effective_at_utc,
                   before_used_percent, after_used_percent, previous_reset_at_utc, current_reset_at_utc,
                   classification, confidence, source, explanation
            FROM quota_reset_events
            WHERE detected_at_utc >= $from AND detected_at_utc <= $to
            ORDER BY detected_at_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(fromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 1000));
        var results = new List<QuotaResetEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new QuotaResetEvent(
                reader.GetString(0),
                Enum.Parse<QuotaWindowKind>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                ParseUtc(reader.GetString(4)),
                ParseUtc(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : ParseUtc(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
                Enum.Parse<QuotaResetClassification>(reader.GetString(10)),
                reader.GetDouble(11),
                reader.GetString(12),
                reader.GetString(13)));
        }
        return results;
    }

    private static async Task<IReadOnlyList<ForecastSnapshot>> LoadForecastHistoryAsync(
        SqliteConnection connection,
        QuotaWindowKind kind,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, profile, kind, generated_at_utc, burn_rate_percent_per_hour,
                   estimated_exhaustion_at_utc, survives_until_reset, sustainable_percent_per_hour, confidence,
                   state, burn_pressure, projected_remaining_at_reset_percent, trend, is_quantized_flat,
                   quota_source, quota_authority, quota_captured_at_utc, quota_window_minutes, quota_resets_at_utc
            FROM forecast_snapshots
            WHERE kind = $kind AND generated_at_utc >= $from AND generated_at_utc <= $to
            ORDER BY generated_at_utc DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$from", SerializeUtc(fromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 2000));
        var results = new List<ForecastSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = Enum.TryParse<ForecastState>(reader.GetString(9), out var parsedState)
                ? parsedState
                : ForecastState.Learning;
            results.Add(new ForecastSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                new Forecast(
                    Enum.Parse<QuotaWindowKind>(reader.GetString(2)),
                    ParseUtc(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6) != 0,
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.GetDouble(8),
                    state,
                    reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    !reader.IsDBNull(13) && reader.GetInt32(13) != 0),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) || !Enum.TryParse<QuotaObservationAuthority>(reader.GetString(15), out var authority) ? QuotaObservationAuthority.Unknown : authority,
                reader.IsDBNull(16) ? null : ParseUtc(reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.IsDBNull(18) ? null : ParseUtc(reader.GetString(18))));
        }
        return results;
    }

    private static async Task<IReadOnlyList<QuotaSnapshot>> LoadQuotaSnapshotsAsync(
        SqliteConnection connection,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, captured_at_utc, used_percent, window_minutes, resets_at_utc, provider, profile, source
            FROM quota_snapshots
            WHERE captured_at_utc >= $from AND captured_at_utc <= $to
              AND kind IN ('FiveHour', 'Weekly')
            ORDER BY provider, profile, kind, captured_at_utc
            LIMIT 50000;
            """;
        command.Parameters.AddWithValue("$from", SerializeUtc(fromUtc));
        command.Parameters.AddWithValue("$to", SerializeUtc(toUtc));
        var results = new List<QuotaSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadQuotaSnapshot(reader));
        }
        return CanonicalizeQuotaSnapshots(results);
    }

    private static IReadOnlyList<QuotaSnapshot> CanonicalizeQuotaSnapshots(
        IEnumerable<QuotaSnapshot> snapshots) =>
        snapshots
            .GroupBy(snapshot => (
                snapshot.Provider,
                snapshot.Profile,
                snapshot.Kind,
                snapshot.CapturedAtUtc))
            .Select(group => group
                .OrderByDescending(snapshot => snapshot.Authority)
                .ThenByDescending(snapshot => snapshot.Source, StringComparer.Ordinal)
                .First())
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ToArray();

    private static QuotaSnapshot ReadQuotaSnapshot(SqliteDataReader reader) =>
        new(
            Enum.Parse<QuotaWindowKind>(reader.GetString(0)),
            ParseUtc(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7));

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string BucketSql(AnalyticsBucketSize size, string column) => size switch
    {
        AnalyticsBucketSize.Minute => $"strftime('%Y-%m-%dT%H:%M:00Z', {column})",
        AnalyticsBucketSize.Hour => $"strftime('%Y-%m-%dT%H:00:00Z', {column})",
        _ => $"strftime('%Y-%m-%dT00:00:00Z', {column})"
    };

    private static DateTimeOffset BucketStart(DateTimeOffset value, AnalyticsBucketSize size)
    {
        value = value.ToUniversalTime();
        return size switch
        {
            AnalyticsBucketSize.Minute => new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, TimeSpan.Zero),
            AnalyticsBucketSize.Hour => new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero),
            _ => new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private static DateTimeOffset BucketEnd(DateTimeOffset value, AnalyticsBucketSize size) => size switch
    {
        AnalyticsBucketSize.Minute => value.AddMinutes(1),
        AnalyticsBucketSize.Hour => value.AddHours(1),
        _ => value.AddDays(1)
    };

    private static string BuildIntervalId(QuotaSnapshot previous, QuotaSnapshot current)
    {
        var material = string.Join('|',
            current.Provider,
            current.Profile,
            current.Kind,
            previous.CapturedAtUtc.ToUniversalTime().ToString("O"),
            current.CapturedAtUtc.ToUniversalTime().ToString("O"),
            current.ResetsAtUtc?.ToUniversalTime().ToString("O") ?? "none");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"quota-burn-{hash[..24]}";
    }

    private static async Task BeginReadSnapshotAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "BEGIN DEFERRED;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CommitReadSnapshotAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TryRollbackReadTransactionAsync(SqliteConnection connection)
    {
        try
        {
            var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqliteException)
        {
            // Preserve the query failure if SQLite already ended the transaction.
        }
    }

    private static string SerializeUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed class MutableBucket
    {
        public long NativeTokens { get; set; }
        public long Uncached { get; set; }
        public long CacheRead { get; set; }
        public long CacheWrite { get; set; }
        public long NonReasoningOutput { get; set; }
        public long ReasoningOutput { get; set; }
        public long RootTokens { get; set; }
        public long SubagentTokens { get; set; }
        public int ActiveSessions { get; set; }
        public int Compactions { get; set; }
        public double? FiveHourQuotaDelta { get; set; }
        public double? WeeklyQuotaDelta { get; set; }

        public UsageHistoryBucket ToModel(DateTimeOffset start, DateTimeOffset end) =>
            new(
                start,
                end,
                NativeTokens,
                Uncached,
                CacheRead,
                CacheWrite,
                NonReasoningOutput,
                ReasoningOutput,
                RootTokens,
                SubagentTokens,
                ActiveSessions,
                Compactions,
                FiveHourQuotaDelta,
                WeeklyQuotaDelta);
    }

    private sealed record QuotaBurnIntervalSeed(QuotaSnapshot Previous, QuotaSnapshot Current, double Delta);

    private sealed record IntervalAggregate(
        long NativeTokens,
        long RootTokens,
        long SubagentTokens,
        int RootSessions,
        int SubagentSessions,
        int Compactions,
        string? DominantModel,
        string? DominantReasoning)
    {
        public static IntervalAggregate Empty { get; } = new(0, 0, 0, 0, 0, 0, null, null);
    }
}
