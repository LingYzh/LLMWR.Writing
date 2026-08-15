using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;
using Microsoft.Win32.SafeHandles;

namespace LLMW.Writing.Infrastructure.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class LiveSandboxLaunch : IDisposable
{
    public required SafeJobHandle Job { get; init; }
    public required SafeProcessHandle Process { get; init; }
    public required int ProcessId { get; init; }
    public required StreamingHeadTail Stdout { get; init; }
    public required StreamingHeadTail Stderr { get; init; }
    public required Task DrainTask { get; init; }

    public void Dispose()
    {
        Process.Dispose();
        Job.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsSandboxProcessLauncher
{
    internal static string LastSuccessfulLaunchPath { get; private set; } = "";
    public static SandboxExecutionResult Launch(
        SandboxExecutionRequest request,
        SandboxIdentity identity,
        string workDirectory,
        string trustedProjectRoot,
        ISandboxFaultInjector faultInjector,
        bool grantInternetClient)
    {
        try
        {
            using var live = LaunchLive(request, identity, workDirectory, trustedProjectRoot, faultInjector, grantInternetClient);
            return WaitFor(live, request, identity);
        }
        catch (SandboxLayerException exception)
        {
            return SandboxExecutionResult.Fail(request, exception.Error, exception.Message, identity.AppContainerSid);
        }
    }

    internal static SandboxExecutionResult LaunchRestrictedOnly(
        SandboxExecutionRequest request,
        SandboxIdentity identity,
        string workDirectory,
        string trustedProjectRoot,
        ISandboxFaultInjector faultInjector)
    {
        try
        {
            using var live = LaunchLiveCore(request, identity, workDirectory, trustedProjectRoot, faultInjector, grantInternetClient: false, enableLpac: false, useRestrictedToken: true, useAppContainer: false);
            return WaitFor(live, request, identity);
        }
        catch (SandboxLayerException exception)
        {
            return SandboxExecutionResult.Fail(request, exception.Error, exception.Message, identity.AppContainerSid);
        }
    }

    internal static SandboxExecutionResult LaunchAppContainerOnly(
        SandboxExecutionRequest request,
        SandboxIdentity identity,
        string workDirectory,
        string trustedProjectRoot,
        ISandboxFaultInjector faultInjector)
    {
        try
        {
            using var live = LaunchLiveCore(request, identity, workDirectory, trustedProjectRoot, faultInjector, grantInternetClient: false, enableLpac: false, useRestrictedToken: false, useAppContainer: true);
            return WaitFor(live, request, identity);
        }
        catch (SandboxLayerException exception)
        {
            return SandboxExecutionResult.Fail(request, exception.Error, exception.Message, identity.AppContainerSid);
        }
    }

    public static LiveSandboxLaunch LaunchLive(
        SandboxExecutionRequest request,
        SandboxIdentity identity,
        string workDirectory,
        string trustedProjectRoot,
        ISandboxFaultInjector faultInjector,
        bool grantInternetClient)
    {
        return LaunchLiveCore(request, identity, workDirectory, trustedProjectRoot, faultInjector, grantInternetClient, enableLpac: false);
    }

    private static LiveSandboxLaunch LaunchLiveCore(
        SandboxExecutionRequest request,
        SandboxIdentity identity,
        string workDirectory,
        string trustedProjectRoot,
        ISandboxFaultInjector faultInjector,
        bool grantInternetClient,
        bool enableLpac,
        bool useRestrictedToken = true,
        bool useAppContainer = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = grantInternetClient;
        Directory.CreateDirectory(workDirectory);

        if (faultInjector.Fault == SandboxFaultPoint.CreateProcess)
        {
            throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, "Injected CreateProcess failure.");
        }

        if (faultInjector.Fault == SandboxFaultPoint.SecurityCapabilities)
        {
            throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, "Injected SECURITY_CAPABILITIES failure.");
        }

        if (faultInjector.Fault == SandboxFaultPoint.PrivilegeScopedEnable)
        {
            using (PrivilegeScope.EnableOnCurrentProcess("SeIncreaseQuotaPrivilege", faultInjector))
            {
            }
        }

        var job = JobObjectController.CreateConfigured(request.EffectiveLimits, faultInjector);
        SafeAccessTokenHandle? restricted = null;
        SafeAccessTokenHandle? appContainerToken = null;
        SafeSidHandle? sidHandle = null;
        RunCapability? runCapability = null;
        GCHandle capabilitiesHandle = default;
        GCHandle capabilityEntriesHandle = default;
        var lpacPolicyMemory = IntPtr.Zero;
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;
        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;
        IntPtr environment = IntPtr.Zero;
        SafeProcThreadAttributeList? attributes = null;
        GCHandle handleList = default;
        SafeProcessHandle? process = null;
        var threadHandle = IntPtr.Zero;
        try
        {
            if (useRestrictedToken)
            {
                restricted = WindowsRestrictedTokenFactory.Create(faultInjector);
            }

            var executable = SandboxToolStager.ResolveLaunchExecutable(
                trustedProjectRoot,
                request.ExecutablePath,
                workDirectory,
                identity.AppContainerSid,
                faultInjector);
            GrantWorkSurface(workDirectory, trustedProjectRoot, identity.AppContainerSid, request.Binding, faultInjector);
            var windowStation = SandboxWindowStation.Ensure();
            windowStation.GrantSandboxIdentity(identity.AppContainerSid, faultInjector);
            AppContainerNetworkIsolation.EnsureLoopbackNotExempt(identity.AppContainerSid, faultInjector);
            if (!NativeMethods.ConvertStringSidToSidW(identity.AppContainerSid, out var sid) || sid == IntPtr.Zero)
            {
                throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, "AppContainer SID conversion failed.");
            }

            sidHandle = new SafeSidHandle(sid, true, SidReleaseKind.LocalFree);
            runCapability = RunCapability.Derive(request.Binding.ProjectScope.ProjectId, request.Binding.RunId);
            GrantWorkCapability(workDirectory, runCapability.SidString, faultInjector);
            var capabilityEntries = new SID_AND_ATTRIBUTES[]
            {
                new()
                {
                    Sid = runCapability.SidPointer,
                    Attributes = NativeConstants.SE_GROUP_ENABLED
                }
            };
            capabilityEntriesHandle = GCHandle.Alloc(capabilityEntries, GCHandleType.Pinned);
            var capabilities = new SECURITY_CAPABILITIES
            {
                AppContainerSid = sidHandle.DangerousGetHandle(),
                Capabilities = capabilityEntriesHandle.AddrOfPinnedObject(),
                CapabilityCount = 1
            };
            capabilitiesHandle = GCHandle.Alloc(capabilities, GCHandleType.Pinned);
            var lpacPolicy = IntPtr.Zero;
            if (enableLpac)
            {
                lpacPolicyMemory = Marshal.AllocHGlobal(sizeof(uint));
                Marshal.WriteInt32(lpacPolicyMemory, (int)NativeConstants.PROCESS_CREATION_ALL_APPLICATION_PACKAGES_OPT_OUT);
                lpacPolicy = lpacPolicyMemory;
            }

            CreatePipes(out stdinRead, out stdinWrite, out stdoutRead, out stdoutWrite, out stderrRead, out stderrWrite);
            var profileDirectory = AppContainerProfileManager.ResolveProfileDirectory(identity);
            environment = BuildEnvironment(request, workDirectory, profileDirectory);
            var inheritHandles = new[]
            {
                stdinRead.DangerousGetHandle(),
                stdoutWrite.DangerousGetHandle(),
                stderrWrite.DangerousGetHandle()
            };
            handleList = GCHandle.Alloc(inheritHandles, GCHandleType.Pinned);
            var flags = NativeConstants.CREATE_SUSPENDED |
                        NativeConstants.CREATE_UNICODE_ENVIRONMENT |
                        NativeConstants.CREATE_NO_WINDOW |
                        NativeConstants.EXTENDED_STARTUPINFO_PRESENT;
            var desktop = Marshal.StringToHGlobalUni(windowStation.DesktopPath);
            PROCESS_INFORMATION information;
            try
            {
                if (!TryCreateSandboxedProcess(
                        restricted,
                        useRestrictedToken,
                        useAppContainer,
                        capabilities,
                        capabilitiesHandle.AddrOfPinnedObject(),
                        handleList.AddrOfPinnedObject(),
                        inheritHandles,
                        lpacPolicy,
                        executable,
                        request.Arguments,
                        flags,
                        environment,
                        workDirectory,
                        desktop,
                        out information,
                        out attributes,
                        out appContainerToken,
                        out var launchError))
                {
                    throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, launchError);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(desktop);
            }

            process = new SafeProcessHandle(information.hProcess, ownsHandle: true);
            threadHandle = information.hThread;
            JobObjectController.AssignOrTerminate(job, process, faultInjector);
            if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue)
            {
                NativeMethods.TerminateProcess(process, 1);
                throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, $"ResumeThread failed: {Marshal.GetLastWin32Error()}.");
            }

            NativeMethods.CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;
            stdoutWrite.Dispose();
            stdoutWrite = null;
            stderrWrite.Dispose();
            stderrWrite = null;
            stdinRead.Dispose();
            stdinRead = null;
            stdinWrite.Dispose();
            stdinWrite = null;
            var stdoutCapture = new StreamingHeadTail();
            var stderrCapture = new StreamingHeadTail();
            var drain = Task.WhenAll(Drain(stdoutRead, stdoutCapture), Drain(stderrRead, stderrCapture));
            stdoutRead = null;
            stderrRead = null;
            var live = new LiveSandboxLaunch
            {
                Job = job,
                Process = process,
                ProcessId = (int)information.dwProcessId,
                Stdout = stdoutCapture,
                Stderr = stderrCapture,
                DrainTask = drain
            };
            job = null!;
            process = null;
            return live;
        }
        catch
        {
            process?.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            if (threadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(threadHandle);
            }

            restricted?.Dispose();
            appContainerToken?.Dispose();
            sidHandle?.Dispose();
            runCapability?.Dispose();
            attributes?.Dispose();
            if (handleList.IsAllocated)
            {
                handleList.Free();
            }

            if (capabilitiesHandle.IsAllocated)
            {
                capabilitiesHandle.Free();
            }

            if (capabilityEntriesHandle.IsAllocated)
            {
                capabilityEntriesHandle.Free();
            }

            if (lpacPolicyMemory != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(lpacPolicyMemory);
            }

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }

            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrRead?.Dispose();
            stderrWrite?.Dispose();
        }
    }

    public static SandboxExecutionResult WaitFor(LiveSandboxLaunch live, SandboxExecutionRequest request, SandboxIdentity identity)
    {
        var timeoutMs = (uint)Math.Clamp(request.EffectiveTimeout.TotalMilliseconds, 1, int.MaxValue);
        var wait = NativeMethods.WaitForSingleObject(live.Process, timeoutMs);
        var timedOut = wait == NativeConstants.WAIT_TIMEOUT;
        if (timedOut)
        {
            NativeMethods.TerminateJobObject(live.Job, 1);
            _ = NativeMethods.WaitForSingleObject(live.Process, 5000);
        }

        live.DrainTask.Wait(TimeSpan.FromSeconds(5));
        NativeMethods.GetExitCodeProcess(live.Process, out var exitCode);
        var error = timedOut
            ? SandboxError.Timeout
            : MapResourceFailure(unchecked((int)exitCode));
        return new SandboxExecutionResult(
            Succeeded: !timedOut && exitCode == 0,
            Error: error,
            ExitCode: unchecked((int)exitCode),
            Stdout: live.Stdout.ToUtf8String(),
            Stderr: live.Stderr.ToUtf8String(),
            StdoutTruncated: live.Stdout.Truncated,
            StderrTruncated: live.Stderr.Truncated,
            TimedOut: timedOut,
            RunId: request.Binding.RunId,
            WorkerInstanceId: request.Binding.WorkerInstanceId,
            SandboxIdentity: identity.AppContainerSid,
            ExecutableIdentity: Path.GetFullPath(request.ExecutablePath),
            Capability: request.Capability,
            DenyReason: error?.ToString(),
            ProcessId: live.ProcessId);
    }

    private static SandboxError? MapResourceFailure(int exitCode)
    {
        if (exitCode == 0)
        {
            return null;
        }

        // STATUS_QUOTA_EXCEEDED, STATUS_NO_MEMORY, STATUS_STACK_OVERFLOW
        if (exitCode is -1073741756 or -1073741801 or -1073741571)
        {
            return SandboxError.MemoryLimitExceeded;
        }

        return null;
    }

    private static char[] CreateWritableCommandLine(string commandLine)
    {
        var buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, buffer, 0, commandLine.Length);
        return buffer;
    }

    private static bool TryCreateSandboxedProcess(
        SafeAccessTokenHandle? restricted,
        bool useRestrictedToken,
        bool useAppContainer,
        SECURITY_CAPABILITIES capabilities,
        IntPtr capabilitiesPointer,
        IntPtr handleListPointer,
        IntPtr[] inheritHandles,
        IntPtr lpacPolicy,
        string executable,
        IReadOnlyList<string> arguments,
        uint flags,
        IntPtr environment,
        string workDirectory,
        IntPtr desktop,
        out PROCESS_INFORMATION information,
        out SafeProcThreadAttributeList? attributes,
        out SafeAccessTokenHandle? appContainerToken,
        out string launchError)
    {
        information = default;
        attributes = null;
        appContainerToken = null;
        List<string> errors = [];
        var commandLineText = WindowsCommandLine.Build(executable, arguments);
        if (!useRestrictedToken)
        {
            attributes = CreateAttributeList(capabilitiesPointer, handleListPointer, inheritHandles.Length, lpacPolicy, includeSecurityCapabilities: true);
            if (TryCreateProcessW(executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var currentError))
            {
                LastSuccessfulLaunchPath = "CreateProcessW+SECURITY_CAPABILITIES";
                launchError = "";
                return true;
            }

            launchError = "CreateProcessW+SECURITY_CAPABILITIES=" + currentError;
            attributes.Dispose();
            attributes = null;
            return false;
        }

        if (restricted is null)
        {
            launchError = "Restricted token was not created.";
            return false;
        }

        if (!useAppContainer)
        {
            attributes = CreateAttributeList(IntPtr.Zero, handleListPointer, inheritHandles.Length, IntPtr.Zero, includeSecurityCapabilities: false);
            if (TryCreateProcessAsUser(restricted, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var restrictedOnlyAsUser))
            {
                LastSuccessfulLaunchPath = "RestrictedOnly CreateProcessAsUserW";
                launchError = "";
                return true;
            }

            launchError = "RestrictedOnly CreateProcessAsUserW=" + restrictedOnlyAsUser;
            attributes.Dispose();
            attributes = null;
            return false;
        }

        attributes = CreateAttributeList(capabilitiesPointer, handleListPointer, inheritHandles.Length, lpacPolicy, includeSecurityCapabilities: true);
        if (TryCreateProcessAsUser(restricted, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var documentedError))
        {
            LastSuccessfulLaunchPath = "Restricted+SECURITY_CAPABILITIES CreateProcessAsUserW";
            launchError = "";
            return true;
        }

        errors.Add("Restricted+SECURITY_CAPABILITIES CreateProcessAsUserW=" + documentedError);
        attributes.Dispose();
        attributes = null;

        if (TryCreateAppContainerToken(restricted, capabilities, out appContainerToken) && appContainerToken is not null)
        {
            attributes = CreateAttributeList(IntPtr.Zero, handleListPointer, inheritHandles.Length, lpacPolicy, includeSecurityCapabilities: false);
            if (TryCreateProcessAsUser(appContainerToken, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var asUserError))
            {
                LastSuccessfulLaunchPath = "CreateAppContainerToken+CreateProcessAsUserW";
                launchError = "";
                return true;
            }

            errors.Add("CreateAppContainerToken+CreateProcessAsUserW=" + asUserError);
            attributes.Dispose();
            attributes = null;
        }
        else
        {
            errors.Add("CreateAppContainerToken=" + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        launchError = string.Join(" | ", errors);
        return false;
    }

    private static bool TryCreateAppContainerToken(
        SafeAccessTokenHandle restricted,
        SECURITY_CAPABILITIES capabilities,
        out SafeAccessTokenHandle? token)
    {
        token = null;
        try
        {
            if (NativeMethods.CreateAppContainerToken(restricted, in capabilities, out var created) && created is not null && !created.IsInvalid)
            {
                token = created;
                return true;
            }

            created?.Dispose();
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryCreateProcessAsUser(
        SafeAccessTokenHandle token,
        string executable,
        string commandLineText,
        uint flags,
        IntPtr environment,
        string workDirectory,
        IntPtr[] inheritHandles,
        SafeProcThreadAttributeList attributes,
        IntPtr desktop,
        out PROCESS_INFORMATION information,
        out string error)
    {
        if (TryCreateProcessAsUserCore(
                token,
                executable,
                commandLineText,
                flags,
                environment,
                workDirectory,
                inheritHandles,
                attributes,
                desktop,
                out information,
                out error))
        {
            return true;
        }

        if (error != NativeConstants.ERROR_PRIVILEGE_NOT_HELD.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return false;
        }

        try
        {
            using (PrivilegeScope.EnableOnCurrentProcess("SeIncreaseQuotaPrivilege", NoSandboxFaultInjector.Instance))
            {
                return TryCreateProcessAsUserCore(
                    token,
                    executable,
                    commandLineText,
                    flags,
                    environment,
                    workDirectory,
                    inheritHandles,
                    attributes,
                    desktop,
                    out information,
                    out error);
            }
        }
        catch (SandboxLayerException exception)
        {
            error = exception.Message;
            information = default;
            return false;
        }
    }

    private static bool TryCreateProcessAsUserCore(
        SafeAccessTokenHandle token,
        string executable,
        string commandLineText,
        uint flags,
        IntPtr environment,
        string workDirectory,
        IntPtr[] inheritHandles,
        SafeProcThreadAttributeList attributes,
        IntPtr desktop,
        out PROCESS_INFORMATION information,
        out string error)
    {
        var startup = CreateStartup(inheritHandles, attributes, desktop);
        var commandLine = CreateWritableCommandLine(commandLineText);
        NativeMethods.SetLastError(0);
        if (NativeMethods.CreateProcessAsUserW(
                token,
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                flags,
                environment,
                workDirectory,
                ref startup,
                out information))
        {
            error = "";
            return true;
        }

        error = Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture);
        information = default;
        return false;
    }

    private static bool TryCreateProcessW(
        string executable,
        string commandLineText,
        uint flags,
        IntPtr environment,
        string workDirectory,
        IntPtr[] inheritHandles,
        SafeProcThreadAttributeList attributes,
        IntPtr desktop,
        out PROCESS_INFORMATION information,
        out string error)
    {
        var startup = CreateStartup(inheritHandles, attributes, desktop);
        var commandLine = CreateWritableCommandLine(commandLineText);
        NativeMethods.SetLastError(0);
        if (NativeMethods.CreateProcessW(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                flags,
                environment,
                workDirectory,
                ref startup,
                out information))
        {
            error = "";
            return true;
        }

        error = Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture);
        information = default;
        return false;
    }

    private static STARTUPINFOEXW CreateStartup(IntPtr[] inheritHandles, SafeProcThreadAttributeList attributes, IntPtr desktop) =>
        new()
        {
            StartupInfo = new STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>(),
                lpDesktop = desktop,
                dwFlags = NativeConstants.STARTF_USESTDHANDLES,
                hStdInput = inheritHandles[0],
                hStdOutput = inheritHandles[1],
                hStdError = inheritHandles[2]
            },
            lpAttributeList = attributes.DangerousGetHandle()
        };

    private static void GrantWorkSurface(
        string workDirectory,
        string trustedProjectRoot,
        string appContainerSid,
        SandboxLaunchBinding binding,
        ISandboxFaultInjector faultInjector)
    {
        var sandboxRoot = SandboxPathPolicy.SandboxRoot(trustedProjectRoot);
        Directory.CreateDirectory(sandboxRoot);
        var runs = Path.Combine(sandboxRoot, "runs");
        Directory.CreateDirectory(runs);
        var runDir = Path.Combine(runs, binding.RunId);
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(workDirectory);
        AppContainerAclManager.GrantMinimum(sandboxRoot, appContainerSid, NativeConstants.FILE_GENERIC_EXECUTE, inherit: false, faultInjector);
        AppContainerAclManager.GrantMinimum(runs, appContainerSid, NativeConstants.FILE_GENERIC_EXECUTE, inherit: false, faultInjector);
        AppContainerAclManager.GrantMinimum(runDir, appContainerSid, NativeConstants.FILE_GENERIC_EXECUTE, inherit: false, faultInjector);
    }

    private static void GrantWorkCapability(string workDirectory, string capabilitySid, ISandboxFaultInjector faultInjector)
    {
        AppContainerAclManager.GrantMinimum(workDirectory, capabilitySid, NativeConstants.SandboxWorkAccess, inherit: true, faultInjector);
    }

    private static SafeProcThreadAttributeList CreateAttributeList(
        IntPtr capabilities,
        IntPtr handleList,
        int handleCount,
        IntPtr lpacPolicy,
        bool includeSecurityCapabilities)
    {
        var attributeCount = 1;
        if (includeSecurityCapabilities)
        {
            attributeCount++;
        }

        if (lpacPolicy != IntPtr.Zero)
        {
            attributeCount++;
        }

        var size = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
        var memory = Marshal.AllocHGlobal((int)size);
        if (!NativeMethods.InitializeProcThreadAttributeList(memory, attributeCount, 0, ref size))
        {
            Marshal.FreeHGlobal(memory);
            throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, $"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}.");
        }

        var list = new SafeProcThreadAttributeList(memory);
        if (includeSecurityCapabilities &&
            !NativeMethods.UpdateProcThreadAttribute(
                memory,
                0,
                new UIntPtr(NativeConstants.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES),
                capabilities,
                (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(),
                IntPtr.Zero,
                IntPtr.Zero))
        {
            list.Dispose();
            throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, $"SECURITY_CAPABILITIES attribute failed: {Marshal.GetLastWin32Error()}.");
        }

        if (!NativeMethods.UpdateProcThreadAttribute(
                memory,
                0,
                new UIntPtr(NativeConstants.PROC_THREAD_ATTRIBUTE_HANDLE_LIST),
                handleList,
                (IntPtr)(handleCount * IntPtr.Size),
                IntPtr.Zero,
                IntPtr.Zero))
        {
            list.Dispose();
            throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, $"HANDLE_LIST attribute failed: {Marshal.GetLastWin32Error()}.");
        }

        if (lpacPolicy != IntPtr.Zero &&
            !NativeMethods.UpdateProcThreadAttribute(
                memory,
                0,
                new UIntPtr(NativeConstants.PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY),
                lpacPolicy,
                (IntPtr)sizeof(uint),
                IntPtr.Zero,
                IntPtr.Zero))
        {
            list.Dispose();
            throw new SandboxLayerException(
                SandboxError.SecurityCapabilitiesFailed,
                $"LPAC ALL_APPLICATION_PACKAGES_POLICY failed: {Marshal.GetLastWin32Error()}.");
        }

        return list;
    }

    private static void CreatePipes(
        out SafeFileHandle stdinRead,
        out SafeFileHandle stdinWrite,
        out SafeFileHandle stdoutRead,
        out SafeFileHandle stdoutWrite,
        out SafeFileHandle stderrRead,
        out SafeFileHandle stderrWrite)
    {
        var security = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1
        };
        if (!NativeMethods.CreatePipe(out stdinRead, out stdinWrite, ref security, 0) ||
            !NativeMethods.CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0) ||
            !NativeMethods.CreatePipe(out stderrRead, out stderrWrite, ref security, 0))
        {
            throw new SandboxLayerException(SandboxError.ProcessLaunchFailed, $"CreatePipe failed: {Marshal.GetLastWin32Error()}.");
        }

        NativeMethods.SetHandleInformation(stdinWrite, NativeConstants.HANDLE_FLAG_INHERIT, 0);
        NativeMethods.SetHandleInformation(stdoutRead, NativeConstants.HANDLE_FLAG_INHERIT, 0);
        NativeMethods.SetHandleInformation(stderrRead, NativeConstants.HANDLE_FLAG_INHERIT, 0);
    }

    private static IntPtr BuildEnvironment(SandboxExecutionRequest request, string workDirectory, string? appContainerProfileDirectory)
    {
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            parent[entry.Key.ToString() ?? ""] = entry.Value?.ToString();
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(systemRoot, "System32");
        var sanitized = new Dictionary<string, string>(
            SandboxEnvironmentPolicy.Sanitize(parent, workDirectory, systemRoot, system32, appContainerProfileDirectory),
            StringComparer.OrdinalIgnoreCase);
        var extraError = SandboxEnvironmentPolicy.ValidateExtraEnvironment(request.ExtraEnvironment);
        if (extraError is not null)
        {
            throw new SandboxLayerException(extraError.Value, "ExtraEnvironment is not on the independent allowlist.");
        }

        var block = string.Join("\0", sanitized.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        var bytes = System.Text.Encoding.Unicode.GetBytes(block);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        return buffer;
    }

    private static Task Drain(SafeFileHandle read, StreamingHeadTail buffer) =>
        Task.Run(() =>
        {
            using (read)
            {
                var chunk = new byte[8192];
                while (NativeMethods.ReadFile(read, chunk, chunk.Length, out var readCount, IntPtr.Zero) && readCount > 0)
                {
                    buffer.Append(chunk.AsSpan(0, readCount));
                }
            }
        });
}
