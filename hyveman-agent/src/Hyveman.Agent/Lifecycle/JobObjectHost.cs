using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Lifecycle;

/// <summary>
/// Job Object containment (AGENT.md §4.2, H4/H5): process memory cap → OS
/// kills the agent on exceed; CPU rate cap (hard cap, Win8+) → a runaway loop
/// cannot starve the host; priority class Below Normal. Applied at startup
/// BEFORE large allocations so the cap is real from the start.
/// </summary>
public static class JobObjectHost
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectCpuRateControlInformation = 15;

    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitPriorityClass = 0x00000020;

    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;

    private const uint BelowNormalPriorityClass = 0x00004000;

    private static IntPtr _job;

    /// <summary>
    /// Places the current process in a Job Object with the configured caps.
    /// Best-effort: if the process is already in a job we cannot join, log and
    /// continue (dev/CI environments) — the caps are load-bearing in service
    /// deployment and failing startup would be worse than running uncapped
    /// where the caps are unenforceable anyway.
    /// </summary>
    public static void Apply(long processMemoryBytes, int cpuRatePercent, ILogger log)
    {
        try
        {
            _job = CreateJobObjectW(IntPtr.Zero, "hyveman-agent");
            if (_job == IntPtr.Zero)
            {
                log.LogWarning("CreateJobObject failed (win32 {err}); running without job-object caps", Marshal.GetLastWin32Error());
                return;
            }

            // Extended limits: process memory cap + priority class.
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            extended.BasicLimitInformation.LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitPriorityClass;
            extended.BasicLimitInformation.PriorityClass = BelowNormalPriorityClass;
            extended.ProcessMemoryLimit = new UIntPtr((ulong)processMemoryBytes);

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(extended, buf, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, buf, (uint)size))
                    log.LogWarning("SetInformationJobObject(extended) failed (win32 {err}); memory cap not applied", Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            // CPU rate cap: percent of one logical processor, hard cap.
            var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
                CpuRate = (uint)(cpuRatePercent * 100) // hundredths of a percent
            };
            var cpuSize = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
            IntPtr cpuBuf = Marshal.AllocHGlobal(cpuSize);
            try
            {
                Marshal.StructureToPtr(cpu, cpuBuf, false);
                if (!SetInformationJobObject(_job, JobObjectCpuRateControlInformation, cpuBuf, (uint)cpuSize))
                    log.LogWarning("SetInformationJobObject(cpu-rate) failed (win32 {err}); CPU cap not applied", Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(cpuBuf);
            }

            if (!AssignProcessToJobObject(_job, GetCurrentProcess()))
                log.LogWarning("AssignProcessToJobObject failed (win32 {err}); process already in a job? Running without job-object caps", Marshal.GetLastWin32Error());
            else
                log.LogInformation("Job Object applied: process memory cap {mem} bytes, CPU cap {cpu}% of one core, priority Below Normal",
                    processMemoryBytes, cpuRatePercent);
        }
        catch (Exception ex)
        {
            // Never take the agent down over containment setup; log loudly.
            log.LogError(ex, "Job Object setup failed; running without job-object caps");
        }
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
