using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace Olymp.Runner.Windows;

[SupportedOSPlatform("windows5.1.2600")]
internal class JobObject : IDisposable
{
    private readonly SafeFileHandle _jobHandle;

    public uint ActiveProcesses => BasicAccountingInformation.ActiveProcesses;
    public TimeSpan TotalUserTime => TimeSpan.FromTicks(BasicAccountingInformation.TotalUserTime);
    public nuint PeakJobMemoryUsed => ExtendedLimitInformation.PeakJobMemoryUsed;

    public JOBOBJECT_BASIC_ACCOUNTING_INFORMATION BasicAccountingInformation
        => QueryInfo<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation);

    public JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION BasicAndIOAccountingInformation
        => QueryInfo<JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION>(JOBOBJECTINFOCLASS.JobObjectBasicAndIoAccountingInformation);

    public JOBOBJECT_EXTENDED_LIMIT_INFORMATION ExtendedLimitInformation
        => QueryInfo<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation);

    public JobObject(string name)
    {
        _jobHandle = PInvoke.CreateJobObject(null, name);
        if (_jobHandle.IsInvalid)
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
    }

    public void Dispose() => _jobHandle.Dispose();

    public unsafe void SetInfo<T>(JOBOBJECTINFOCLASS infoClass, T info)
        where T : unmanaged
    {
        if (!PInvoke.SetInformationJobObject(_jobHandle, infoClass, &info, (uint)Marshal.SizeOf<T>()))
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
    }

    public unsafe T QueryInfo<T>(JOBOBJECTINFOCLASS infoClass)
        where T : unmanaged
    {
        var info = new T();
        if (!PInvoke.QueryInformationJobObject(_jobHandle, infoClass, &info, (uint)Marshal.SizeOf<T>(), null))
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        return info;
    }

    public void SetLimits(uint activeProcessLimit, ProcessPriorityClass priorityClass, TimeSpan userTimeLimit, nuint memoryLimit)
    {
        var extLimitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                    | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PRIORITY_CLASS
                    | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_TIME
                    | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY,
                ActiveProcessLimit = activeProcessLimit,
                PriorityClass = (uint)priorityClass,
                PerJobUserTimeLimit = userTimeLimit.Ticks
            },
            JobMemoryLimit = memoryLimit
        };

        SetInfo(JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, extLimitInfo);
    }

    public void SetAllUIRestrictions()
    {
        var basicUiRestrictions = new JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            UIRestrictionsClass = JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_DESKTOP
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_DISPLAYSETTINGS
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_EXITWINDOWS
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_GLOBALATOMS
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_HANDLES
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_READCLIPBOARD
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_SYSTEMPARAMETERS
                | JOB_OBJECT_UILIMIT.JOB_OBJECT_UILIMIT_WRITECLIPBOARD
        };

        SetInfo(JOBOBJECTINFOCLASS.JobObjectBasicUIRestrictions, basicUiRestrictions);
    }

    public void SetNetRateControl(ulong maxBandwith)
    {
        var netRateControlInformation = new JOBOBJECT_NET_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE
                | JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH,
            MaxBandwidth = maxBandwith
        };

        SetInfo(JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation, netRateControlInformation);
    }

    

    public void Assign(Process process)
    {
        if (!PInvoke.AssignProcessToJobObject(_jobHandle, process.SafeHandle))
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
    }

    public void Terminate(int exitCode = 0)
    {
        if (!PInvoke.TerminateJobObject(_jobHandle, unchecked((uint)exitCode)))
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
    }
}
