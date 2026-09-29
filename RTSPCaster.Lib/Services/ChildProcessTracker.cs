using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RTSPCaster.Services;

// Windows uses a Job Object; on other platforms tracked processes are stopped on orderly shutdown.
public sealed class ChildProcessTracker : IDisposable
{
    private readonly SafeFileHandle? _job;
    private readonly HashSet<Process> _processes = new();
    private readonly object _sync = new();
    private bool _disposed;

    public ChildProcessTracker()
    {
        if (!OperatingSystem.IsWindows())
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            return;
        }

        _job = CreateJobObject(IntPtr.Zero, null!);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };
        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extended, ptr, false);
            if (!SetInformationJobObject(_job, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (process.HasExited) return;
            if (_job != null)
            {
                if (!AssignProcessToJobObject(_job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return;
            }

            process.EnableRaisingEvents = true;
            process.Exited += OnChildExited;
            if (process.HasExited)
            {
                process.Exited -= OnChildExited;
                return;
            }
            _processes.Add(process);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_job != null)
        {
            _job.Dispose();
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        lock (_sync)
        {
            foreach (var process in _processes)
            {
                process.Exited -= OnChildExited;
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { } // Process already exited.
                catch (Win32Exception) { } // Process already exited or cannot be signaled.
            }
            _processes.Clear();
        }
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    private void OnChildExited(object? sender, EventArgs args)
    {
        if (sender is not Process process) return;
        lock (_sync)
        {
            _processes.Remove(process);
            process.Exited -= OnChildExited;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    private enum JobObjectInfoType { ExtendedLimitInformation = 9 }

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
