namespace TajsTokens.App.Services;

/// <summary>PID-scoped, current-user-only lifecycle handshake for local deployment.</summary>
internal sealed class DogfoodLifetime : IDisposable
{
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _ready;
    private readonly RegisteredWaitHandle _registration;

    public DogfoodLifetime(Action requestExit)
    {
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset,
            $"TajsTokens.Stop.{Environment.ProcessId}", options);
        _ready = new EventWaitHandle(false, EventResetMode.ManualReset,
            $"TajsTokens.Ready.{Environment.ProcessId}", options);
        _registration = ThreadPool.RegisterWaitForSingleObject(_stop, (_, _) => requestExit(),
            null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void MarkReady() => _ready.Set();

    public void Dispose()
    {
        _ready.Reset();
        _registration.Unregister(null);
        _ready.Dispose();
        _stop.Dispose();
    }
}
