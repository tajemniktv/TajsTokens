// Taj's Tokens | TelemetryCoordinatorTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }
        catch
        {
            // Best-effort test cleanup; a locked temp file should not mask the assertion result.
        }
    }

    [Fact]
    public async Task TokenForecastUsesPersistedHistoryWhenAccountingFailsAndRetainsStaleResultOnQueryFailure()
    {
        var tokens = new SequencedTokscaleProvider(
            [
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([]),
                _ => Task.FromException<IReadOnlyList<TokenUsage>>(new IOException("offline")),
                _ => Task.FromException<IReadOnlyList<TokenUsage>>(new IOException("offline")),
            ],
            [
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]),
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]),
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]),
            ]);
        var quota = new SequencedQuotaProvider(
            Enumerable.Range(0, 3).Select(_ =>
                (Func<CancellationToken, Task<IReadOnlyList<QuotaSnapshot>>>)(_ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]))));
        var intelligence = new BlockingIntelligenceService();
        intelligence.Release.TrySetResult(true);
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, quota, intelligence);
        TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Manual, default);
        Assert.False(first.TokenForecast!.IsStale);
        intelligence.FailTokenForecast = true;
        TelemetrySnapshot failed = await coordinator.RefreshAsync(RefreshTrigger.Manual, default);
        Assert.True(failed.TokenForecast!.IsStale);
        Assert.Equal(first.TokenForecast.GeneratedAtUtc, failed.TokenForecast.GeneratedAtUtc);
        intelligence.FailTokenForecast = false;
        TelemetrySnapshot recovered = await coordinator.RefreshAsync(RefreshTrigger.Manual, default);
        Assert.False(recovered.TokenForecast!.IsStale);
        Assert.Equal(3, intelligence.TokenForecastCalls);
    }

    [Fact]
    public async Task RolloutCoverageAppearsInDiagnosticsWithoutCallingAlternativesMissingTasks()
    {
        var tokens = new SequencedTokscaleProvider(
            [_ => Task.FromResult<IReadOnlyList<TokenUsage>>([])],
            [_ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([])]);
        var quota = new SequencedQuotaProvider([_ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([])]);
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, quota, observatoryService: new CoverageObservatory());
        TelemetrySnapshot result = await coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);
        ProviderHealthSnapshot source = Assert.Single(result.Sources, item => item.Provider == "Codex rollouts");
        Assert.Equal(TelemetryHealthState.Live, source.State);
        Assert.Contains("2/2 indexed paths accessible", source.Detail);
        Assert.Contains("3 files in configured discovery roots, 1 not indexed", source.Detail);
        Assert.Contains("not missing tasks", source.Detail);
        Assert.Contains("do not measure unique work or durable collection completeness", source.Detail);
    }

    [Fact]
    public async Task TokscalePartialFailure_PreservesWholePreviousGeneration()
    {
        TokenUsage firstUsage = Usage("first-model", 100);
        TokenUsage secondUsage = Usage("second-model", 999);
        TokenTimeBucket firstBucket = Bucket("first-hour", 100);

        var tokens = new SequencedTokscaleProvider(
            [
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([firstUsage]),
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([secondUsage]),
            ],
            [
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([firstBucket]),
                _ => Task.FromException<IReadOnlyList<TokenTimeBucket>>(new InvalidOperationException("hourly failed")),
            ]);
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]),
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]),
        ]);
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, quota);

        TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        TelemetrySnapshot second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

        Assert.True(first.TokenDataFresh);
        Assert.False(second.TokenDataFresh);
        Assert.Equal(first.TokenUsages, second.TokenUsages);
        Assert.Equal(first.HourlyBuckets, second.HourlyBuckets);
        Assert.DoesNotContain(second.TokenUsages, item => item.Model == "second-model");
        Assert.Equal(TelemetryHealthState.Stale, second.Sources.Single(source => source.Provider == "Tokscale").State);
    }

    [Fact]
    public async Task PartialQuotaResponse_RetainsOmittedHistoryWithoutCallingSuccessfulResponseStale()
    {
        DateTimeOffset firstCaptured = DateTimeOffset.UnixEpoch.AddHours(1);
        DateTimeOffset secondCaptured = firstCaptured.AddMinutes(1);
        QuotaSnapshot originalFiveHour = Quota(QuotaWindowKind.FiveHour, firstCaptured, 40, firstCaptured.AddHours(5));
        QuotaSnapshot originalWeekly = Quota(QuotaWindowKind.Weekly, firstCaptured, 60, firstCaptured.AddDays(7));
        QuotaSnapshot updatedFiveHour = Quota(QuotaWindowKind.FiveHour, secondCaptured, 45, secondCaptured.AddHours(5));

        var tokens = new SequencedTokscaleProvider(
            [
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([]),
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([]),
            ],
            [
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]),
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]),
            ]);
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([originalFiveHour, originalWeekly]),
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([updatedFiveHour]),
        ]);
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, quota);

        TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        TelemetrySnapshot second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

        Assert.True(first.QuotaDataFresh);
        Assert.True(second.QuotaDataFresh);
        Assert.Equal(2, second.QuotaSnapshots.Count);
        QuotaSnapshot fiveHour = second.QuotaSnapshots.Single(item => item.Kind == QuotaWindowKind.FiveHour);
        QuotaSnapshot weekly = second.QuotaSnapshots.Single(item => item.Kind == QuotaWindowKind.Weekly);
        Assert.Equal(secondCaptured, fiveHour.CapturedAtUtc);
        Assert.Equal(firstCaptured, weekly.CapturedAtUtc);
        Assert.True(second.IsQuotaSnapshotFresh(fiveHour));
        Assert.False(second.IsQuotaSnapshotFresh(weekly));
        Assert.Equal(TelemetryHealthState.Live, second.FindQuotaLane(fiveHour)!.State);
        Assert.Equal(TelemetryHealthState.Stale, second.FindQuotaLane(weekly)!.State);
        Assert.True(second.FindQuotaLane(weekly)!.NotReportedByProvider);
        Assert.False(second.FindQuotaLane(fiveHour)!.NotReportedByProvider);
        Assert.Equal(TelemetryHealthState.Live, second.Sources.Single(source => source.Provider == "Codex app-server").State);
    }

    [Fact]
    public async Task WeeklyOnlyResponse_DistinguishesOmissionFromFailureAndRecoversFiveHour()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuotaSnapshot weekly = Quota(QuotaWindowKind.Weekly, now, 3, now.AddDays(7));
        QuotaSnapshot fiveHour = Quota(QuotaWindowKind.FiveHour, now, 1, now.AddHours(5));
        var tokens = new SequencedTokscaleProvider(
            Enumerable.Range(0, 3).Select(_ =>
                new Func<CancellationToken, Task<IReadOnlyList<TokenUsage>>>(_ => Task.FromResult<IReadOnlyList<TokenUsage>>([]))),
            Enumerable.Range(0, 3).Select(_ =>
                new Func<CancellationToken, Task<IReadOnlyList<TokenTimeBucket>>>(_ =>
                    Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]))));
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([weekly]),
            _ => throw new IOException("fixture unavailable"),
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([weekly, fiveHour]),
        ]);
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, quota);
        TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        Assert.True(first.QuotaDataFresh);
        Assert.True(first.QuotaLanes.Single(x => x.Kind == QuotaWindowKind.FiveHour).NotReportedByProvider);
        Assert.Null(first.QuotaLanes.Single(x => x.Kind == QuotaWindowKind.FiveHour).Snapshot);
        TelemetrySnapshot failed = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);
        Assert.False(failed.QuotaDataFresh);
        Assert.All(failed.QuotaLanes, x => Assert.False(x.NotReportedByProvider));
        TelemetrySnapshot recovered = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);
        Assert.All(recovered.QuotaLanes, x => Assert.True(x.IsFresh));
        Assert.All(recovered.QuotaLanes, x => Assert.False(x.NotReportedByProvider));
    }

    [Fact]
    public async Task SlowIntelligenceRefresh_DoesNotHoldSharedTelemetryRefreshCompletion()
    {
        var tokens = new SequencedTokscaleProvider(
            [_ => Task.FromResult<IReadOnlyList<TokenUsage>>([])],
            [_ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([])]);
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]),
        ]);
        var intelligence = new BlockingIntelligenceService();
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, quota, intelligence);

        Task<TelemetrySnapshot> refreshTask = coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        await intelligence.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task completed = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(2)));
        intelligence.Release.TrySetResult(true);

        Assert.Same(refreshTask, completed);
        await refreshTask;
    }

    private TelemetryCoordinator CreateCoordinator(
        ITokscaleProvider tokens,
        ICodexQuotaProvider quota,
        IIntelligenceService? intelligenceService = null,
        ICodexObservatoryService? observatoryService = null)
    {
        Directory.CreateDirectory(_directory);
        return new TelemetryCoordinator(
            tokens,
            quota,
            new SqliteTelemetryRepository(Path.Combine(_directory, "telemetry.db")),
            observatoryService,
            intelligenceService);
    }

    [Theory]
    [InlineData("B", true)]
    [InlineData("B", false)]
    [InlineData(null, true)]
    public async Task SuccessfulAccountSwitchDropsOtherAccountsOmittedLanes(string? nextAccount, bool reportsWeekly)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuotaSnapshot fiveHour = Quota(QuotaWindowKind.FiveHour, now, 10, now.AddHours(5)) with { AccountKey = "A" };
        QuotaSnapshot weekly = Quota(QuotaWindowKind.Weekly, now, 20, now.AddDays(7)) with { AccountKey = "A" };
        var tokens = new SequencedTokscaleProvider(
            Enumerable.Range(0, 3).Select(_ =>
                new Func<CancellationToken, Task<IReadOnlyList<TokenUsage>>>(_ => Task.FromResult<IReadOnlyList<TokenUsage>>([]))),
            Enumerable.Range(0, 3).Select(_ =>
                new Func<CancellationToken, Task<IReadOnlyList<TokenTimeBucket>>>(_ =>
                    Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]))));
        var provider = new ScopedQuotaProvider(
            new CodexQuotaResponse([fiveHour, weekly], "A"),
            new CodexQuotaResponse(reportsWeekly ? [weekly with { AccountKey = nextAccount }] : [], nextAccount));
        TelemetryCoordinator coordinator = CreateCoordinator(tokens, provider);
        await coordinator.RefreshAsync(RefreshTrigger.Startup, default);
        TelemetrySnapshot changed = await coordinator.RefreshAsync(RefreshTrigger.Interval, default);
        Assert.Null(changed.QuotaLanes.Single(x => x.Kind == QuotaWindowKind.FiveHour).Snapshot);
        Assert.All(changed.QuotaSnapshots, x => Assert.Equal(nextAccount, x.AccountKey));
        Assert.Equal(reportsWeekly ? 1 : 0, changed.QuotaSnapshots.Count);
        TelemetrySnapshot failed = await coordinator.RefreshAsync(RefreshTrigger.Interval, default);
        Assert.False(failed.QuotaDataFresh);
        Assert.Equal(changed.QuotaSnapshots, failed.QuotaSnapshots);
        Assert.All(failed.QuotaLanes, x => Assert.False(x.IsFresh));
    }

    private static TokenUsage Usage(string model, long tokens)
    {
        return new TokenUsage(
            "tokscale",
            "codex",
            model,
            DateTimeOffset.UnixEpoch,
            new TokenBreakdown(tokens, 0, 0, 0, 0));
    }

    private static TokenTimeBucket Bucket(string label, long tokens)
    {
        return new TokenTimeBucket(
            "tokscale",
            label,
            DateTimeOffset.UnixEpoch,
            new TokenBreakdown(tokens, 0, 0, 0, 0));
    }

    private static QuotaSnapshot Quota(
        QuotaWindowKind kind,
        DateTimeOffset captured,
        double usedPercent,
        DateTimeOffset reset)
    {
        return new QuotaSnapshot(
            kind,
            captured,
            usedPercent,
            kind == QuotaWindowKind.FiveHour ? 300 : 10_080,
            reset,
            "codex",
            "default",
            "test");
    }

    private sealed class CoverageObservatory : ICodexObservatoryService
    {
        public Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new CodexObservatoryRefreshResult(2, 0, 0, 0, 0, 0, 0)
                {
                    Coverage = new CodexCollectionCoverage(DateTimeOffset.UnixEpoch, 2, 2, 3, 1, 0),
                });
        }
    }

    private sealed class ScopedQuotaProvider(params CodexQuotaResponse[] responses) : ICodexQuotaProvider
    {
        private readonly Queue<CodexQuotaResponse> _responses = new(responses);

        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CodexQuotaResponse> GetQuotaResponseAsync(CancellationToken cancellationToken)
        {
            return _responses.Count > 0
                ? Task.FromResult(_responses.Dequeue())
                : throw new IOException("Sanitized provider failure");
        }
    }

    private sealed class SequencedTokscaleProvider(
        IEnumerable<Func<CancellationToken, Task<IReadOnlyList<TokenUsage>>>> usageResponses,
        IEnumerable<Func<CancellationToken, Task<IReadOnlyList<TokenTimeBucket>>>> hourlyResponses) : ITokscaleProvider
    {
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<TokenTimeBucket>>>> _hourly = new(hourlyResponses);
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<TokenUsage>>>> _usage = new(usageResponses);

        public Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
        {
            return _usage.Dequeue()(cancellationToken);
        }

        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
        {
            return _hourly.Dequeue()(cancellationToken);
        }
    }

    private sealed class SequencedQuotaProvider(
        IEnumerable<Func<CancellationToken, Task<IReadOnlyList<QuotaSnapshot>>>> responses) : ICodexQuotaProvider
    {
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<QuotaSnapshot>>>> _responses = new(responses);

        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken)
        {
            return _responses.Dequeue()(cancellationToken);
        }
    }

    private sealed class BlockingIntelligenceService : IIntelligenceService
    {

        public bool FailTokenForecast { get; set; }
        public int TokenForecastCalls { get; private set; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TtEvaluationArchiveEntry>> GetTtEvaluationHistoryAsync(
            string provider,
            string profile,
            int take,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<TokenWorkloadForecast> ForecastTokenWorkloadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            TokenForecastCalls++;
            return FailTokenForecast
                ? Task.FromException<TokenWorkloadForecast>(new IOException("query failed"))
                : Task.FromResult(new TokenWorkloadForecast(nowUtc, null, 0, 0, [], "test fixture"));
        }

        public Task<ForecastEvaluationReport> EvaluateForecastsAsync(
            string provider,
            string profile,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("This coordinator test double does not run historical evaluation.");
        }

        public async Task<IntelligenceRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new IntelligenceRefreshResult(0, 0);
        }

        public Task<IReadOnlyList<CurrentQuotaForecast>> BuildAndPersistCurrentForecastsAsync(
            IReadOnlyList<QuotaLaneState> quotaLanes,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IntelligenceDashboard> QueryAsync(IntelligenceQuery query, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<QuotaBurnDetail> GetQuotaBurnDetailAsync(
            QuotaBurnInterval interval,
            int take,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<RecentScenarioPattern> GetRecentScenarioPatternAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ScenarioEstimate> EstimateScenarioAsync(
            ScenarioRequest request,
            DateTimeOffset historyFromUtc,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}