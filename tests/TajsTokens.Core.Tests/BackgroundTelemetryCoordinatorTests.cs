using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class BackgroundTelemetryCoordinatorTests
{
    [Fact]
    public async Task QuotaPollingContinuesDuringForecastAndOlderResultsStayStale()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-background-").FullName;
        using var lifetime = new CancellationTokenSource();
        var intelligence = new ControlledIntelligence();
        var clock = new TestClock();
        try
        {
            var coordinator = new TelemetryCoordinator(new EmptyTokens(), new ChangingQuota(),
                new SqliteTelemetryRepository(Path.Combine(directory, "test.db")),
                intelligenceService: intelligence, isBackground: () => true, timeProvider: clock);
            TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Startup, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, first.CollectionCompletion?.Generation);
            await intelligence.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task firstForecast = coordinator.BackgroundForecastCompletion;
            TelemetrySnapshot second = await coordinator.RefreshAsync(RefreshTrigger.Interval, lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(20, Assert.Single(second.QuotaSnapshots).UsedPercent);
            Assert.Equal(2, second.CollectionCompletion?.Generation);
            Assert.Equal(1, intelligence.Calls);
            intelligence.Release.TrySetResult();
            await firstForecast.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(second.CollectionCompletion, coordinator.Latest.CollectionCompletion);
            Assert.Equal(20, Assert.Single(coordinator.Latest.QuotaSnapshots).UsedPercent);
            CurrentQuotaForecast retained = Assert.Single(coordinator.Latest.CurrentForecasts);
            Assert.Equal(10, retained.Current.UsedPercent);
            Assert.Equal(TelemetryHealthState.Stale, retained.State);
            Assert.Null(coordinator.Latest.TokenForecast);

            await coordinator.RefreshAsync(RefreshTrigger.Interval, lifetime.Token);
            await coordinator.BackgroundForecastCompletion;
            Assert.Equal(2, intelligence.Calls);
        }
        finally
        {
            lifetime.Cancel();
            intelligence.Release.TrySetResult();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ManualRefreshCancelsDeferredForecastBeforeComputingForegroundGeneration()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-background-").FullName;
        using var lifetime = new CancellationTokenSource();
        var intelligence = new ControlledIntelligence();
        bool background = true;
        try
        {
            var coordinator = new TelemetryCoordinator(new EmptyTokens(), new ChangingQuota(),
                new SqliteTelemetryRepository(Path.Combine(directory, "test.db")),
                intelligenceService: intelligence, isBackground: () => background);
            await coordinator.RefreshAsync(RefreshTrigger.Startup, lifetime.Token);
            await intelligence.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task backgroundTask = coordinator.BackgroundForecastCompletion;
            background = false;
            TelemetrySnapshot manual = await coordinator.RefreshAsync(RefreshTrigger.Manual, lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await backgroundTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(intelligence.Cancelled);
            Assert.Equal(2, intelligence.Calls);
            Assert.Equal(20, Assert.Single(manual.CurrentForecasts).Current.UsedPercent);
            Assert.Equal(TelemetryHealthState.Live, manual.CurrentForecasts[0].State);
        }
        finally
        {
            lifetime.Cancel();
            intelligence.Release.TrySetResult();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task HidingDashboardCancelsDetailedPassAlreadyInProgress()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-background-").FullName;
        using var lifetime = new CancellationTokenSource();
        var intelligence = new ControlledIntelligence();
        bool background = false;
        try
        {
            var coordinator = new TelemetryCoordinator(new EmptyTokens(), new ChangingQuota(),
                new SqliteTelemetryRepository(Path.Combine(directory, "test.db")),
                intelligenceService: intelligence, isBackground: () => background);
            Task<TelemetrySnapshot> foreground = coordinator.RefreshAsync(RefreshTrigger.Startup, lifetime.Token);
            await intelligence.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            background = true;
            coordinator.SuspendDetailedForecasts();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(intelligence.Cancelled);
            Assert.False(coordinator.IsForecastRefreshRunning);
        }
        finally
        {
            lifetime.Cancel();
            intelligence.Release.TrySetResult();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsolatedCalibrationRunsInEitherModeWithoutBlockingOrReplacingNewerQuota(bool background)
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-worker-").FullName;
        using var lifetime = new CancellationTokenSource();
        var intelligence = new ControlledIntelligence();
        intelligence.Release.TrySetResult();
        var calibration = new ControlledCalibration();
        try
        {
            var coordinator = new TelemetryCoordinator(new EmptyTokens(), new ChangingQuota(),
                new SqliteTelemetryRepository(Path.Combine(directory, "test.db")),
                intelligenceService: intelligence, isBackground: () => background, calibrationService: calibration);
            TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Startup, lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await calibration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task pending = coordinator.CalibrationCompletion;
            await coordinator.BackgroundForecastCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.RefreshAsync(RefreshTrigger.Interval, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.BackgroundForecastCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calibration.Calls);
            QuotaSnapshot old = Assert.Single(first.QuotaSnapshots);
            calibration.Result.TrySetResult(new CodexCalibrationResult(
                [new CurrentQuotaForecast(old, TelemetryHealthState.Live,
                    TajsTokens.Core.Services.QuotaPredictionService.BuildResetOutlook([old], old.CapturedAtUtc), "fixture")], null));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(20, Assert.Single(coordinator.Latest.QuotaSnapshots).UsedPercent);
            Assert.Equal(20, Assert.Single(coordinator.Latest.CurrentForecasts).Current.UsedPercent);
            Assert.Contains(coordinator.Latest.Events, x => x.Type == "Calibration completed");
        }
        finally
        {
            lifetime.Cancel();
            calibration.Result.TrySetResult(null);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DelayedCompactOutlookDoesNotDowngradeCompletedCalibration()
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-worker-").FullName;
        using var lifetime = new CancellationTokenSource();
        var intelligence = new ControlledIntelligence();
        var calibration = new ControlledCalibration();
        try
        {
            var coordinator = new TelemetryCoordinator(new EmptyTokens(), new ChangingQuota(),
                new SqliteTelemetryRepository(Path.Combine(directory, "test.db")),
                intelligenceService: intelligence, isBackground: () => true, calibrationService: calibration);
            TelemetrySnapshot first = await coordinator.RefreshAsync(RefreshTrigger.Startup, lifetime.Token);
            await intelligence.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await calibration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            QuotaSnapshot quota = Assert.Single(first.QuotaSnapshots);
            var detailed = new CurrentQuotaForecast(quota, TelemetryHealthState.Live,
                TajsTokens.Core.Services.QuotaPredictionService.BuildResetOutlook([quota], quota.CapturedAtUtc), "detailed fixture");
            calibration.Result.TrySetResult(new CodexCalibrationResult([detailed], null));
            await coordinator.CalibrationCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            intelligence.Release.TrySetResult();
            await coordinator.BackgroundForecastCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("detailed fixture", Assert.Single(coordinator.Latest.CurrentForecasts).HistoryPolicy);
        }
        finally
        {
            lifetime.Cancel();
            intelligence.Release.TrySetResult();
            calibration.Result.TrySetResult(null);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CalibrationFreshnessIncludesStorageReplacementEvenWhenRevisionIsEqual(bool replace)
    {
        string directory = Directory.CreateTempSubdirectory("tajstokens-worker-generation-").FullName;
        using var lifetime = new CancellationTokenSource();
        var intelligence = new ControlledIntelligence();
        intelligence.Release.TrySetResult();
        var calibration = new ControlledCalibration();
        var tokens = new GenerationTokens();
        try
        {
            var coordinator = new TelemetryCoordinator(tokens, new ChangingQuota(),
                new SqliteTelemetryRepository(Path.Combine(directory, "test.db")), intelligenceService: intelligence,
                calibrationService: calibration);
            await coordinator.RefreshAsync(RefreshTrigger.Startup, lifetime.Token);
            await calibration.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task pending = coordinator.CalibrationCompletion;
            if (replace) tokens.Generation = "replacement";
            await coordinator.RefreshAsync(RefreshTrigger.Interval, lifetime.Token);
            Assert.Equal(7, coordinator.Latest.TokenGeneration!.Revision);
            Assert.Equal(tokens.Generation, coordinator.Latest.TokenGeneration.StorageGeneration);
            calibration.Result.TrySetResult(new CodexCalibrationResult([], new TokenWorkloadForecast(
                DateTimeOffset.UtcNow, null, 0, 0, [], "fixture")));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(replace, coordinator.Latest.TokenForecast!.IsStale);
            await coordinator.BackgroundForecastCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lifetime.Cancel();
            calibration.Result.TrySetResult(null);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private sealed class GenerationTokens : ICodexTokenAccountingProvider
    {
        public string Generation { get; set; } = "original";
        public Task<CodexTokenAccountingSnapshot> GetSnapshotAsync(CancellationToken token) =>
            Task.FromResult(new CodexTokenAccountingSnapshot("fixture", "fixture", [], [], Revision: 7)
            { StorageGeneration = Generation });
    }

    private sealed class ControlledCalibration : ICodexCalibrationService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CodexCalibrationResult?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<CodexCalibrationResult?> TryCalibrateAsync(IReadOnlyList<QuotaLaneState> lanes, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            return await Result.Task.WaitAsync(token);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EmptyTokens : ITokscaleProvider
    {
        public Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<TokenUsage>>([]);
        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]);
    }

    private sealed class ChangingQuota : ICodexQuotaProvider
    {
        private int _reads;
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken token)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<QuotaSnapshot>>([new(QuotaWindowKind.Weekly, now,
                Interlocked.Increment(ref _reads) * 10, 10080, now.AddDays(2), "codex", "default", "codex-app-server:test")]);
        }
    }

    private sealed class ControlledIntelligence : IIntelligenceService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Cancelled;
        public async Task<IReadOnlyList<CurrentQuotaForecast>> BuildAndPersistCurrentForecastsAsync(
            IReadOnlyList<QuotaLaneState> lanes, DateTimeOffset now, CancellationToken token)
        {
            if (Interlocked.Increment(ref Calls) == 1)
            {
                Started.TrySetResult();
                try { await Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return lanes.Where(x => x.Snapshot is not null).Select(x =>
                new CurrentQuotaForecast(x.Snapshot!, TelemetryHealthState.Live, null, "fixture")).ToArray();
        }
        public Task<TokenWorkloadForecast> ForecastTokenWorkloadAsync(DateTimeOffset now, CancellationToken token) =>
            Task.FromResult(new TokenWorkloadForecast(now, null, 0, 0, [], "fixture"));
        public Task<IntelligenceRefreshResult> RefreshAsync(CancellationToken token) => Task.FromResult(new IntelligenceRefreshResult(0, 0));
        public Task<IntelligenceDashboard> QueryAsync(IntelligenceQuery query, CancellationToken token) => throw new NotSupportedException();
        public Task<QuotaBurnDetail> GetQuotaBurnDetailAsync(QuotaBurnInterval interval, int take, CancellationToken token) => throw new NotSupportedException();
        public Task<ScenarioEstimate> EstimateScenarioAsync(ScenarioRequest request, DateTimeOffset from, CancellationToken token) => throw new NotSupportedException();
        public Task<ForecastEvaluationReport> EvaluateForecastsAsync(string provider, string profile, DateTimeOffset from, DateTimeOffset to, CancellationToken token) => throw new NotSupportedException();
        public Task<RecentScenarioPattern> GetRecentScenarioPatternAsync(DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<TtEvaluationArchiveEntry>> GetTtEvaluationHistoryAsync(string provider, string profile, int take, CancellationToken token) => throw new NotSupportedException();
    }
}
