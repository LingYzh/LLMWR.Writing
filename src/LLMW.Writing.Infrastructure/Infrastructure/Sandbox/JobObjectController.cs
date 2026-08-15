using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class JobObjectController
{
    private JobObjectController()
    {
    }
    public static SafeJobHandle CreateConfigured(SandboxResourceLimits limits, ISandboxFaultInjector faultInjector)
    {
        if (faultInjector.Fault == SandboxFaultPoint.JobCreation)
        {
            throw new SandboxLayerException(SandboxError.JobCreationFailed, "Injected job creation failure.");
        }

        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new SandboxLayerException(SandboxError.JobCreationFailed, $"CreateJobObjectW failed: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (faultInjector.Fault == SandboxFaultPoint.JobConfiguration)
            {
                throw new SandboxLayerException(SandboxError.JobConfigurationFailed, "Injected job configuration failure.");
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeConstants.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
                                 NativeConstants.JOB_OBJECT_LIMIT_ACTIVE_PROCESS |
                                 NativeConstants.JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                    ActiveProcessLimit = (uint)Math.Max(2, limits.ActiveProcessLimit),
                },
                ProcessMemoryLimit = new UIntPtr((ulong)Math.Max(1024 * 1024, limits.ProcessMemoryBytes))
            };

            if (!NativeMethods.SetInformationJobObject(
                    job,
                    NativeConstants.JobObjectExtendedLimitInformation,
                    in info,
                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw new SandboxLayerException(
                    SandboxError.JobConfigurationFailed,
                    $"SetInformationJobObject(extended) failed: {Marshal.GetLastWin32Error()}.");
            }

            if (limits.CpuRateHundredthsPercent is int cpuRate)
            {
                var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = NativeConstants.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                                   NativeConstants.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    CpuRate = (uint)Math.Clamp(cpuRate, 1, 10000)
                };
                NativeMethods.SetInformationJobObject(
                    job,
                    NativeConstants.JobObjectCpuRateControlInformation,
                    in cpu,
                    Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>());
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public static JOBOBJECT_EXTENDED_LIMIT_INFORMATION Query(SafeJobHandle job)
    {
        if (!NativeMethods.QueryInformationJobObject(
                job,
                NativeConstants.JobObjectExtendedLimitInformation,
                out var info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(),
                out _))
        {
            throw new SandboxLayerException(
                SandboxError.JobConfigurationFailed,
                $"QueryInformationJobObject failed: {Marshal.GetLastWin32Error()}.");
        }

        return info;
    }

    public static void AssignOrTerminate(
        SafeJobHandle job,
        Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        ISandboxFaultInjector faultInjector)
    {
        if (faultInjector.Fault == SandboxFaultPoint.JobAssignment)
        {
            NativeMethods.TerminateProcess(process, 1);
            throw new SandboxLayerException(SandboxError.JobAssignmentFailed, "Injected job assignment failure.");
        }

        if (!NativeMethods.AssignProcessToJobObject(job, process))
        {
            NativeMethods.TerminateProcess(process, 1);
            throw new SandboxLayerException(
                SandboxError.JobAssignmentFailed,
                $"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}.");
        }
    }
}
