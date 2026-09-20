// Taj's Tokens | TelemetryCoordinatorConcurrencyTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class TelemetryCoordinatorConcurrencyTests
{
    [Fact]
    public async Task ManualRefresh_SupersedesInFlightIntervalRefresh()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-coordinator-race-");
        try
        {
            var tokens = new SupersedableTokscaleProvider();
            var coordinator = new TelemetryCoordinator(
                tokens,
                new EmptyQuotaProvider(),
                new SqliteTelemetryRepository(Path.Combine(directory.FullName, "telemetry.db")));

            Task<TelemetrySnapshot> interval = coordinator.RefreshAsync(RefreshTrigger.Interval, CancellationToken.None);
            await tokens.FirstUsageCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task<TelemetrySnapshot> manual = coordinator.RefreshAsync(RefreshTrigger.Manual, CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interval.WaitAsync(TimeSpan.FromSeconds(5)));
            TelemetrySnapshot manualSnapshot = await manual.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(RefreshTrigger.Manual, manualSnapshot.Trigger);
            Assert.True(tokens.UsageCalls >= 2);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                directory.Delete(true);
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
            int call = Interlocked.Increment(ref _usageCalls);
            if (call == 1)
            {
                FirstUsageCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return [];
        }

        public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TokenTimeBucket>>([]);
        }
    }

    private sealed class EmptyQuotaProvider : ICodexQuotaProvider
    {
        public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<QuotaSnapshot>>([]);
        }
    }
}