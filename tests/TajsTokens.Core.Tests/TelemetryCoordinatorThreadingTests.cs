using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorThreadingTests
{
    [Fact]
    public async Task RefreshAsync_DoesNotRunProviderCoreOnCallerSynchronizationContext()
    {
        var directory = CreateTempDirectory();
        var previousContext = SynchronizationContext.Current;
        var callerContext = new SynchronizationContext();
        try
        {
            var tokscale = new CapturingTokscaleProvider();
            var coordinator = new TelemetryCoordinator(
                tokscale,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory, "telemetry.db")));

            SynchronizationContext.SetSynchronizationContext(callerContext);
            await coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);

            Assert.NotSame(callerContext, tokscale.ObservedContext);
            Assert.Null(tokscale.ObservedContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAsync_PublishesProviderSnapshotBeforeObservatoryCompletes()
    {
        var directory = CreateTempDirectory();
        try
        {
            var observatory = new BlockingObservatoryService();
            var coordinator = new TelemetryCoordinator(
                new CapturingTokscaleProvider(),
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory, "telemetry.db")),
                observatory);
            var interim = new TaskCompletionSource<TelemetrySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            coordinator.SnapshotUpdated += snapshot =>
            {
                if (snapshot.Sources.Any(source =>
                        source.Provider == "Codex rollouts" &&
                        source.Detail.Contains("Scanning local Codex rollout history", StringComparison.Ordinal)))
                {
                    interim.TrySetResult(snapshot);
                }
            };

            var refreshTask = coordinator.RefreshAsync(RefreshTrigger.Startup, CancellationToken.None);
            var published = await interim.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(refreshTask.IsCompleted);
            Assert.Contains(published.Sources, source => source.Provider == "Codex rollouts");

            observatory.Complete();
            var final = await refreshTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(final.Sources, source => source.Provider == "Codex rollouts" && source.State == TelemetryHealthState.Live);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TajsTokens.Coordinator.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class CapturingTokscaleProvider : ITokscaleProvider
    {
        public SynchronizationContext? ObservedContext { get; private set; }

        public Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
        {
            ObservedContext = SynchronizationContext.Current;
            return Task.FromResult<IReadOnlyList<TokenUsage>>([]);
        }

        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]);
    }

    private sealed class EmptyQuotaProvider : ICodexQuotaProvider
    {
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
    }

    private sealed class BlockingObservatoryService : ICodexObservatoryService
    {
        private readonly TaskCompletionSource<CodexObservatoryRefreshResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CodexObservatoryRefreshResult> RefreshAsync(CancellationToken cancellationToken) =>
            _completion.Task.WaitAsync(cancellationToken);

        public void Complete() => _completion.TrySetResult(CodexObservatoryRefreshResult.Empty);
    }
}
