using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorConcurrencyTests
{
    [Fact]
    public async Task ManualRefresh_SupersedesInFlightIntervalRefresh()
    {
        var directory = Directory.CreateTempSubdirectory("tajstokens-coordinator-race-");
        try
        {
            var tokens = new SupersedableTokscaleProvider();
            var coordinator = new TelemetryCoordinator(
                tokens,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")));

            var interval = coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);
            await tokens.FirstUsageCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var manual = coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interval);
            var manualSnapshot = await manual.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RefreshTrigger.Manual, manualSnapshot.Trigger);
            Assert.True(tokens.UsageCalls >= 2);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch
            {
                // Cleanup cannot hide the concurrency assertion.
            }
        }
    }

    private sealed class SupersedableTokscaleProvider : ITokscaleProvider
    {
        private int _usageCalls;
        public TaskCompletionSource FirstUsageCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int UsageCalls => Volatile.Read(ref _usageCalls);

        public async Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _usageCalls);
            if (call == 1)
            {
                FirstUsageCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [];
        }

        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]);
    }

    private sealed class EmptyQuotaProvider : ICodexQuotaProvider
    {
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
    }
}
