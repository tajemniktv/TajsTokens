// Taj's Tokens | WindowsResourcePolicy.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Runtime.InteropServices;

namespace TajsTokens.App.Services;

/// <summary>
/// The interactive process keeps normal scheduling and has no hard CPU cap.
/// EcoQoS is a reversible hint; calibration retains its separate job budget.
/// </summary>
internal sealed class WindowsResourcePolicy : IDisposable
{
    private readonly Func<bool, int> _applyEcoQoS;
    private bool? _appliedEcoQoS;
    private int? _lastError;
    private int _background;
    private bool _disposed;

    internal WindowsResourcePolicy(Func<bool, int>? applyEcoQoS = null)
    {
        _applyEcoQoS = applyEcoQoS ?? ApplyEcoQoS;
    }

    public bool IsBackground => Volatile.Read(ref _background) != 0;
    public bool? AppliedEcoQoS => _appliedEcoQoS;
    public int? LastError => _lastError;
    public string Diagnostic => _lastError is { } error
        ? $"Normal scheduling; EcoQoS transition pending (Windows error {error}); will retry."
        : $"Normal scheduling; EcoQoS {(_appliedEcoQoS == true ? "enabled" : "disabled")}; calibration has an independent budget.";

    public void SetDashboardVisible(bool visible)
    {
        if (_disposed) return;
        Volatile.Write(ref _background, visible ? 0 : 1);
        bool requested = !visible;
        if (_appliedEcoQoS == requested) { _lastError = null; return; }
        int error = _applyEcoQoS(requested);
        _lastError = error == 0 ? null : error;
        // Failed native calls never mark the desired state applied. Repeated visibility
        // requests (including the next collection completion) retry the transition.
        if (error == 0) _appliedEcoQoS = requested;
    }

    private static int ApplyEcoQoS(bool enabled)
    {
        var power = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = enabled ? 1u : 0u };
        return SetProcessInformation(GetCurrentProcess(), 4, ref power, Marshal.SizeOf<PowerThrottlingState>())
            ? 0 : Marshal.GetLastWin32Error();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetDashboardVisible(true);
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref PowerThrottlingState information, int size);
}
