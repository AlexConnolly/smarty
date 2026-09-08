using System.Runtime.InteropServices;

namespace Smarty.Agents;

/// <summary>
/// Make a child process die with us, however we die.
///
/// <para>
/// Every MCP server is a child process, and every one of them is disposed properly on a graceful shutdown —
/// stdin closed, two seconds to leave, then killed. None of that runs when the host is killed outright: a
/// force-stop, a crash, a closed terminal. The child is reparented and lives forever, holding whatever the
/// next run wants. Measured on this machine after a day of restarts: eighty orphaned server processes, one
/// still holding the port the next one needed.
/// </para>
/// <para>
/// No amount of shutdown code fixes that, because the whole class of failure is "our shutdown code did not
/// run". So the guarantee is moved to the operating system. A Windows job object with KILL_ON_JOB_CLOSE
/// terminates everything in it the moment the last handle to the job goes away — and process death closes
/// handles, whether or not the process cooperated. The kernel does the reaping.
/// </para>
/// <para>
/// Windows only, and deliberately silent everywhere else: on Linux a child gets reparented to init and this
/// would need a different mechanism (prctl PDEATHSIG), which is worth writing when there is a host to run it on.
/// Failure to set it up is never fatal — an orphan is a nuisance, a server that will not start is not.
/// </para>
/// </summary>
public static class ChildProcessReaper
{
    private static readonly object Gate = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _tried;

    /// <summary>
    /// Put a just-started process into the kill-on-close job. Safe to call for every child; safe to call when
    /// the job could not be created, when the process has already exited, and on a non-Windows host.
    /// </summary>
    public static void KillWithUs(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var job = EnsureJob();
            if (job == IntPtr.Zero) return;
            AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // A process that exited between starting and being assigned throws here, and so does a host that
            // refuses job objects. Neither is worth failing a working MCP server over.
        }
    }

    private static IntPtr EnsureJob()
    {
        lock (Gate)
        {
            if (_tried) return _job;
            _tried = true;

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return _job;

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    _job = job;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return _job;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
