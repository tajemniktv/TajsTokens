// Taj's Tokens | WindowsResourcePolicy.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TajsTokens.App.Services;

/// <summary>Process-wide scheduling follows the dashboard, including async scan continuations.</summary>
internal sealed class WindowsResourcePolicy : IDisposable
{
    private const uint BackgroundBegin = 0x00100000;
    private const uint BackgroundEnd = 0x00200000;
    private const uint ExecutionSpeed = 1;
    private bool? _dashboardVisible;
    private bool _backgroundMode;
    private CancellationTokenSource? _reclamation;
    private bool _disposed;
    private SafeFileHandle? _cpuJob;
    private int _background;

    public bool IsBackground => Volatile.Read(ref _background) != 0;
    public Func<bool>? IsBusy { get; set; }

    public void SetDashboardVisible(bool visible)
    {
        if (_disposed || _dashboardVisible == visible) return;
        _dashboardVisible = visible;
        Volatile.Write(ref _background, visible ? 0 : 1);
        _reclamation?.Cancel();
        _reclamation = null;

        // Background mode lowers CPU, I/O and memory scheduling priorities together. Thread-local
        // scopes cannot safely cover async readers because their continuations change OS threads.
        if (!visible && !_backgroundMode)
            _backgroundMode = SetPriorityClass(GetCurrentProcess(), BackgroundBegin);
        else if (visible && _backgroundMode && SetPriorityClass(GetCurrentProcess(), BackgroundEnd))
            _backgroundMode = false;

        var power = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = visible ? 0 : ExecutionSpeed };
        if (!SetProcessInformation(GetCurrentProcess(), 4, ref power, Marshal.SizeOf<PowerThrottlingState>()))
            Debug.WriteLine($"Resource power policy unavailable: {Marshal.GetLastWin32Error()}");
        ApplyCpuBudget(visible);
        if (!visible) ScheduleReclamation();
    }

    private void ApplyCpuBudget(bool visible)
    {
        if (_cpuJob is null && !visible)
        {
            SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
            // Short-lived provider helpers must not compete with history processing for the
            // parent's tiny CPU budget, otherwise quota reads can time out while in the tray.
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x1000 } };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                Debug.WriteLine($"Background CPU budget unavailable: {Marshal.GetLastWin32Error()}");
                job.Dispose();
                return;
            }
            _cpuJob = job;
        }
        if (_cpuJob is null) return;
        // Windows rate units are 1/100 of one percent of total system CPU. Scheduling priority
        // alone does not limit a replay when the machine has spare cores. No kill-on-close policy.
        var rate = new CpuRateControl { ControlFlags = visible ? 0u : 0x5u, CpuRate = 50 };
        if (!SetInformationJobObject(_cpuJob, 15, ref rate, Marshal.SizeOf<CpuRateControl>()))
            Debug.WriteLine($"Background CPU budget unavailable: {Marshal.GetLastWin32Error()}");
    }

    public void ScheduleReclamation()
    {
        if (_disposed || _dashboardVisible != false || _reclamation is not null) return;
        var cancellation = new CancellationTokenSource();
        _reclamation = cancellation;
        _ = ReclaimAsync(cancellation);
    }

    private async Task ReclaimAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // Let Unloaded handlers and the completed collector release their references first.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
            await Task.Run(() =>
            {
                if (cancellation.IsCancellationRequested || IsBusy?.Invoke() == true) return;
                // Reclaim only after large batches or view releases. Do not trim the working set:
                // evicting useful pages would just hide committed memory and cause page faults.
                if (GC.GetTotalMemory(false) < 64L * 1024 * 1024) return;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_reclamation, cancellation)) _reclamation = null;
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _reclamation?.Cancel();
        _reclamation = null;
        _cpuJob?.Dispose();
        _cpuJob = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref CpuRateControl information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref ExtendedLimits information, int size);
}
