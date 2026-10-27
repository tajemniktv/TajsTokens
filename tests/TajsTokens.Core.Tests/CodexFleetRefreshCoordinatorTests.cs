using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexFleetRefreshCoordinatorTests
{
    [Fact]
    public async Task RepeatedGenerationAndQueuedIntermediateRequestsDoNotRepeatReads()
    {
        var clock = new TestClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, publications = 0;
        var refresh = new CodexFleetRefreshCoordinator(async token =>
        {
            int call = Interlocked.Increment(ref reads);
            if (call == 1) { started.SetResult(); await release.Task.WaitAsync(token); }
            return Snapshot(clock.GetUtcNow());
        }, CancellationToken.None, clock);
        refresh.Updated += () => publications++;
        Task first = refresh.RequestAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task duplicate = refresh.RequestAsync(1);
        Task second = refresh.RequestAsync(2);
        Task third = refresh.RequestAsync(3);
        release.SetResult();
        await Task.WhenAll(first, duplicate, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        await refresh.RequestAsync(3);
        Assert.Equal(2, reads);
        Assert.Equal(2, refresh.ReadsStarted);
        Assert.Equal(1, publications);
    }

    [Fact]
    public async Task FailureRetriesOnlyOnNewCompletedGeneration()
    {
        var clock = new TestClock();
        int reads = 0;
        var refresh = new CodexFleetRefreshCoordinator(_ =>
            ++reads == 1 ? Task.FromException<CodexLiveFleetSnapshot>(new IOException())
                : Task.FromResult(Snapshot(clock.GetUtcNow())), CancellationToken.None, clock);
        await refresh.RequestAsync(1);
        Assert.Null(refresh.RecentCount);
        await refresh.RequestAsync(1);
        Assert.Equal(1, reads);
        await refresh.RequestAsync(2);
        Assert.Equal(2, reads);
        Assert.Equal(0, refresh.RecentCount);
    }

    [Fact]
    public async Task CachedFleetAgesWithoutSourceChangeOrAdditionalDatabaseRead()
    {
        var clock = new TestClock();
        // The catalog row is not used by the count projection; retain only status/time in this fixture.
        var thread = new CodexLiveFleetThread(null!, CodexFleetState.RecentlyWorking, "turn", "inProgress",
            null, clock.GetUtcNow().AddSeconds(-110), null, null);
        var refresh = new CodexFleetRefreshCoordinator(_ => Task.FromResult(Snapshot(clock.GetUtcNow()) with
            { Threads = [thread] }), CancellationToken.None, clock);
        await refresh.RequestAsync(1);
        Assert.Equal(1, refresh.RecentCount);
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(0, refresh.RecentCount);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(refresh.RecentCount);
        Assert.Equal(1, refresh.ReadsStarted);
    }

    [Fact]
    public async Task ShutdownCancelsActiveReadAndDoesNotStartPendingGeneration()
    {
        using var lifetime = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = new CodexFleetRefreshCoordinator(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Snapshot(DateTimeOffset.UtcNow);
        }, lifetime.Token);
        Task active = refresh.RequestAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = refresh.RequestAsync(2);
        lifetime.Cancel();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await refresh.RequestAsync(3);
        Assert.Equal(1, refresh.ReadsStarted);
        Assert.Null(refresh.RecentCount);
    }

    private static CodexLiveFleetSnapshot Snapshot(DateTimeOffset at) => new(at, [], [], true, false);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
