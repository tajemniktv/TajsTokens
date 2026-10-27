using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>One fleet read at a time; queued generations collapse to the newest request.</summary>
public sealed class CodexFleetRefreshCoordinator(
    Func<CancellationToken, Task<CodexLiveFleetSnapshot>> read,
    CancellationToken lifetime,
    TimeProvider? timeProvider = null)
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long _requested;
    private long _started;
    private Task? _running;
    private CodexLiveFleetSnapshot? _latest;

    public event Action? Updated;
    public int ReadsStarted { get; private set; }

    public int? RecentCount
    {
        get
        {
            CodexLiveFleetSnapshot? snapshot = Volatile.Read(ref _latest);
            DateTimeOffset now = _clock.GetUtcNow();
            if (snapshot is null || !snapshot.HistoryCapabilityAvailable ||
                snapshot.CapturedAtUtc > now.AddMinutes(1) || snapshot.CapturedAtUtc < now.AddMinutes(-2)) return null;
            // Freshness changes with clock time even if neither database nor quota changed.
            return snapshot.Threads.Count(x =>
                CodexLiveFleetPolicy.Classify(x.NativeTurnStatus, x.LastObservedAtUtc, now) == CodexFleetState.RecentlyWorking);
        }
    }

    public Task RequestAsync(long completedGeneration)
    {
        lock (_sync)
        {
            if (lifetime.IsCancellationRequested) return Task.CompletedTask;
            if (completedGeneration > _requested) _requested = completedGeneration;
            if (_requested <= _started) return _running ?? Task.CompletedTask;
            return _running ??= Task.Run(ProcessAsync);
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            long generation;
            lock (_sync)
            {
                if (lifetime.IsCancellationRequested || _started == _requested)
                {
                    _running = null;
                    return;
                }
                generation = _started = _requested;
                ReadsStarted++;
            }
            try
            {
                CodexLiveFleetSnapshot result = await read(lifetime).ConfigureAwait(false);
                bool publish;
                lock (_sync)
                {
                    publish = !lifetime.IsCancellationRequested && generation == _requested;
                    if (publish) Volatile.Write(ref _latest, result);
                }
                if (publish) Updated?.Invoke();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception)
            {
                // Fleet status is supplementary. A later completed generation can retry; cached
                // evidence expires rather than being restamped or replacing quota source health.
            }
        }
    }
}
