using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TokscalePartialFailure_PreservesWholePreviousGeneration()
    {
        var firstUsage = Usage("first-model", 100);
        var secondUsage = Usage("second-model", 999);
        var firstBucket = Bucket("first-hour", 100);

        var tokens = new SequencedTokscaleProvider(
            usageResponses:
            [
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([firstUsage]),
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([secondUsage])
            ],
            hourlyResponses:
            [
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([firstBucket]),
                _ => Task.FromException<IReadOnlyList<TokenTimeBucket>>(new InvalidOperationException("hourly failed"))
            ]);
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]),
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([])
        ]);
        var coordinator = CreateCoordinator(tokens, quota);

        var first = await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        var second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

        Assert.True(first.TokenDataFresh);
        Assert.False(second.TokenDataFresh);
        Assert.Equal(first.TokenUsages, second.TokenUsages);
        Assert.Equal(first.HourlyBuckets, second.HourlyBuckets);
        Assert.DoesNotContain(second.TokenUsages, item => item.Model == "second-model");
        Assert.Equal(TelemetryHealthState.Stale, second.Sources.Single(source => source.Provider == "Tokscale").State);
    }

    [Fact]
    public async Task PartialQuotaResponse_RetainsOmittedLaneAndMarksCombinedStateStale()
    {
        var firstCaptured = DateTimeOffset.UnixEpoch.AddHours(1);
        var secondCaptured = firstCaptured.AddMinutes(1);
        var originalFiveHour = Quota(QuotaWindowKind.FiveHour, firstCaptured, 40, firstCaptured.AddHours(5));
        var originalWeekly = Quota(QuotaWindowKind.Weekly, firstCaptured, 60, firstCaptured.AddDays(7));
        var updatedFiveHour = Quota(QuotaWindowKind.FiveHour, secondCaptured, 45, secondCaptured.AddHours(5));

        var tokens = new SequencedTokscaleProvider(
            usageResponses:
            [
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([]),
                _ => Task.FromResult<IReadOnlyList<TokenUsage>>([])
            ],
            hourlyResponses:
            [
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]),
                _ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([])
            ]);
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([originalFiveHour, originalWeekly]),
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([updatedFiveHour])
        ]);
        var coordinator = CreateCoordinator(tokens, quota);

        var first = await coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        var second = await coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);

        Assert.True(first.QuotaDataFresh);
        Assert.False(second.QuotaDataFresh);
        Assert.Equal(2, second.QuotaSnapshots.Count);
        Assert.Equal(secondCaptured, second.QuotaSnapshots.Single(item => item.Kind == QuotaWindowKind.FiveHour).CapturedAtUtc);
        Assert.Equal(firstCaptured, second.QuotaSnapshots.Single(item => item.Kind == QuotaWindowKind.Weekly).CapturedAtUtc);
        Assert.Equal(TelemetryHealthState.Stale, second.Sources.Single(source => source.Provider == "Codex app-server").State);
    }

    [Fact]
    public async Task SlowIntelligenceRefresh_DoesNotHoldSharedTelemetryRefreshCompletion()
    {
        var tokens = new SequencedTokscaleProvider(
            usageResponses: [_ => Task.FromResult<IReadOnlyList<TokenUsage>>([])],
            hourlyResponses: [_ => Task.FromResult<IReadOnlyList<TokenTimeBucket>>([])]);
        var quota = new SequencedQuotaProvider(
        [
            _ => Task.FromResult<IReadOnlyList<QuotaSnapshot>>([])
        ]);
        var intelligence = new BlockingIntelligenceService();
        var coordinator = CreateCoordinator(tokens, quota, intelligence);

        var refreshTask = coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
        await intelligence.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var completed = await Task.WhenAny(refreshTask, Task.Delay(TimeSpan.FromSeconds(2)));
        intelligence.Release.TrySetResult(true);

        Assert.Same(refreshTask, completed);
        await refreshTask;
    }

    private TelemetryCoordinator CreateCoordinator(
        ITokscaleProvider tokens,
        ICodexQuotaProvider quota,
        IIntelligenceService? intelligenceService = null)
    {
        Directory.CreateDirectory(_directory);
        return new TelemetryCoordinator(
            tokens,
            quota,
            new SqliteTelemetryRepository(Path.Combine(_directory, "telemetry.db")),
            intelligenceService: intelligenceService);
    }

    private static TokenUsage Usage(string model, long tokens) => new(
        "tokscale",
        "codex",
        model,
        DateTimeOffset.UnixEpoch,
        new TokenBreakdown(tokens, 0, 0, 0, 0));

    private static TokenTimeBucket Bucket(string label, long tokens) => new(
        "tokscale",
        label,
        DateTimeOffset.UnixEpoch,
        new TokenBreakdown(tokens, 0, 0, 0, 0));

    private static QuotaSnapshot Quota(
        QuotaWindowKind kind,
        DateTimeOffset captured,
        double usedPercent,
        DateTimeOffset reset) => new(
        kind,
        captured,
        usedPercent,
        kind == QuotaWindowKind.FiveHour ? 300 : 10_080,
        reset,
        "codex",
        "default",
        "test");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup; a locked temp file should not mask the assertion result.
        }
    }

    private sealed class SequencedTokscaleProvider(
        IEnumerable<Func<CancellationToken, Task<IReadOnlyList<TokenUsage>>>> usageResponses,
        IEnumerable<Func<CancellationToken, Task<IReadOnlyList<TokenTimeBucket>>>> hourlyResponses) : ITokscaleProvider
    {
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<TokenUsage>>>> _usage = new(usageResponses);
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<TokenTimeBucket>>>> _hourly = new(hourlyResponses);

        public Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken) =>
            _usage.Dequeue()(cancellationToken);

        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken) =>
            _hourly.Dequeue()(cancellationToken);
    }

    private sealed class SequencedQuotaProvider(
        IEnumerable<Func<CancellationToken, Task<IReadOnlyList<QuotaSnapshot>>>> responses) : ICodexQuotaProvider
    {
        private readonly Queue<Func<CancellationToken, Task<IReadOnlyList<QuotaSnapshot>>>> _responses = new(responses);

        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken) =>
            _responses.Dequeue()(cancellationToken);
    }

    private sealed class BlockingIntelligenceService : IIntelligenceService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IntelligenceRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new IntelligenceRefreshResult(0, 0);
        }

        public Task<IntelligenceDashboard> QueryAsync(IntelligenceQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuotaBurnDetail> GetQuotaBurnDetailAsync(
            QuotaBurnInterval interval,
            int take,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ScenarioEstimate> EstimateScenarioAsync(
            ScenarioRequest request,
            DateTimeOffset historyFromUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}