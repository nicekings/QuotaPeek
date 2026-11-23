using System;
using System.Runtime.InteropServices;

namespace NhipPC
{
internal static class WorkerJob
{
    [StructLayout(LayoutKind.Sequential)]
    struct Limits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public Limits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    internal static bool ContainCurrent()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return false;
        var limits = new ExtendedLimits { Basic = new Limits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))) &&
            AssignProcessToJobObject(job, new IntPtr(-1)))
        {
            // Windows closes this non-inheritable handle on worker exit, even on a hard timeout.
            return true;
        }
        CloseHandle(job);
        return false;
    }
}
}
