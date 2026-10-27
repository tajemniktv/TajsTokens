// Taj's Tokens | DogfoodLifetime.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.App.Services;

/// <summary>PID-scoped, current-user-only lifecycle handshake for local deployment.</summary>
internal sealed class DogfoodLifetime : IDisposable
{
    private readonly EventWaitHandle _ready;
    private readonly RegisteredWaitHandle _registration;
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _open;
    private readonly RegisteredWaitHandle _openRegistration;

    public DogfoodLifetime(Action requestExit, Action requestOpen)
    {
        var options = new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true };
        _stop = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            $"TajsTokens.Stop.{Environment.ProcessId}",
            options);
        _ready = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            $"TajsTokens.Ready.{Environment.ProcessId}",
            options);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _stop,
            (_, _) => requestExit(),
            null,
            Timeout.Infinite,
            true);
        _open = new EventWaitHandle(false, EventResetMode.AutoReset,
            $"TajsTokens.Open.{Environment.ProcessId}", options);
        _openRegistration = ThreadPool.RegisterWaitForSingleObject(_open, (_, _) => requestOpen(),
            null, Timeout.Infinite, false);
    }

    public static bool TryRequestDashboard()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (System.Diagnostics.Process candidate in System.Diagnostics.Process.GetProcessesByName("TajsTokens.App"))
        {
            using (candidate)
            {
                if (candidate.Id == current.Id || candidate.SessionId != current.SessionId) continue;
                try
                {
                    if (!string.Equals(Path.GetDirectoryName(candidate.MainModule?.FileName), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase)) continue;
                    using EventWaitHandle signal = EventWaitHandle.OpenExisting($"Local\\TajsTokens.Open.{candidate.Id}");
                    signal.Set();
                    return true;
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or
                    WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
            }
        }
        return false;
    }

    public void Dispose()
    {
        _ready.Reset();
        _registration.Unregister(null);
        _openRegistration.Unregister(null);
        _open.Dispose();
        _ready.Dispose();
        _stop.Dispose();
    }

    public void MarkReady()
    {
        _ready.Set();
    }
}
