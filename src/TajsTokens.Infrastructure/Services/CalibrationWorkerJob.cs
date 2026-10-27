using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TajsTokens.Infrastructure.Services;

/// <summary>Owns only the calibration child. Closing the parent handle terminates that child.</summary>
internal sealed class CalibrationWorkerJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public CalibrationWorkerJob(IntPtr process, bool background)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits { LimitFlags = 0x2000 | 0x100 | 0x400 },
            ProcessMemoryLimit = (UIntPtr)(384UL * 1024 * 1024),
        };
        if (_handle.IsInvalid || !SetInformationJobObject(_handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()) ||
            !AssignProcessToJobObject(_handle, process))
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "Calibration worker limits could not be applied.");
        }
        try { SetBackground(background); }
        catch { _handle.Dispose(); throw; }
    }

    public void SetBackground(bool background)
    {
        var rate = new CpuRate { Flags = 5, Rate = background ? 50u : 200u };
        if (!SetInformationJobObject(_handle, 15, ref rate, Marshal.SizeOf<CpuRate>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Calibration CPU budget could not be applied.");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct CpuRate { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref CpuRate rate, int size);
}
