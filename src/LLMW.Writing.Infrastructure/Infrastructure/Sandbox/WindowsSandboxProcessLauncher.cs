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
        ISandboxFaultInjector faultInjector,
        bool grantInternetClient)
    {
        try
        {
            using var live = LaunchLive(request, identity, workDirectory, faultInjector, grantInternetClient);
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
        ISandboxFaultInjector faultInjector)
    {
        try
        {
            using var live = LaunchLiveCore(request, identity, workDirectory, faultInjector, grantInternetClient: false, enableLpac: false, useRestrictedToken: true, useAppContainer: false);
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
        ISandboxFaultInjector faultInjector)
    {
        try
        {
            using var live = LaunchLiveCore(request, identity, workDirectory, faultInjector, grantInternetClient: false, enableLpac: false, useRestrictedToken: false, useAppContainer: true);
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
        ISandboxFaultInjector faultInjector,
        bool grantInternetClient)
    {
        return LaunchLiveCore(request, identity, workDirectory, faultInjector, grantInternetClient, enableLpac: false);
    }

    private static LiveSandboxLaunch LaunchLiveCore(
        SandboxExecutionRequest request,
        SandboxIdentity identity,
        string workDirectory,
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

        var job = JobObjectController.CreateConfigured(request.EffectiveLimits, faultInjector);
        SafeAccessTokenHandle? restricted = null;
        SafeAccessTokenHandle? appContainerToken = null;
        SafeSidHandle? sidHandle = null;
        GCHandle capabilitiesHandle = default;
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
            GrantLaunchSurfaces(request.ExecutablePath, workDirectory, identity.AppContainerSid, faultInjector);
            AppContainerAclManager.GrantInteractiveDesktop(identity.AppContainerSid, faultInjector);
            AppContainerNetworkIsolation.EnsureLoopbackNotExempt(identity.AppContainerSid, faultInjector);
            if (!NativeMethods.ConvertStringSidToSidW(identity.AppContainerSid, out var sid) || sid == IntPtr.Zero)
            {
                throw new SandboxLayerException(SandboxError.SecurityCapabilitiesFailed, "AppContainer SID conversion failed.");
            }

            sidHandle = new SafeSidHandle(sid, true, SidReleaseKind.LocalFree);
            var capabilities = new SECURITY_CAPABILITIES
            {
                AppContainerSid = sidHandle.DangerousGetHandle(),
                Capabilities = IntPtr.Zero,
                CapabilityCount = 0
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
            var executable = Path.GetFullPath(request.ExecutablePath);
            var flags = NativeConstants.CREATE_SUSPENDED |
                        NativeConstants.CREATE_UNICODE_ENVIRONMENT |
                        NativeConstants.CREATE_NO_WINDOW |
                        NativeConstants.EXTENDED_STARTUPINFO_PRESENT;
            var desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
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
            attributes?.Dispose();
            if (handleList.IsAllocated)
            {
                handleList.Free();
            }

            if (capabilitiesHandle.IsAllocated)
            {
                capabilitiesHandle.Free();
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

            errors.Add("RestrictedOnly CreateProcessAsUserW=" + restrictedOnlyAsUser);
            if (TryCreateProcessWhileImpersonating(restricted, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var restrictedOnlyImpersonate))
            {
                LastSuccessfulLaunchPath = "RestrictedOnly Impersonate+CreateProcessW";
                launchError = "";
                return true;
            }

            launchError = string.Join(" | ", errors) + " | RestrictedOnly Impersonate+CreateProcessW=" + restrictedOnlyImpersonate;
            attributes.Dispose();
            attributes = null;
            return false;
        }

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
            if (TryCreateProcessWithToken(appContainerToken, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var withTokenError))
            {
                LastSuccessfulLaunchPath = "CreateAppContainerToken+CreateProcessWithTokenW";
                launchError = "";
                return true;
            }

            errors.Add("CreateProcessWithTokenW=" + withTokenError);
            attributes.Dispose();
            attributes = null;
        }
        else
        {
            errors.Add("CreateAppContainerToken=" + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        attributes = CreateAttributeList(capabilitiesPointer, handleListPointer, inheritHandles.Length, lpacPolicy, includeSecurityCapabilities: true);
        if (TryCreateProcessAsUser(restricted, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var restrictedError))
        {
            LastSuccessfulLaunchPath = "Restricted+SECURITY_CAPABILITIES CreateProcessAsUserW";
            launchError = "";
            return true;
        }

        errors.Add("Restricted+SECURITY_CAPABILITIES CreateProcessAsUserW=" + restrictedError);
        if (TryCreateProcessWithToken(restricted, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var restrictedWithTokenError))
        {
            LastSuccessfulLaunchPath = "Restricted+SECURITY_CAPABILITIES CreateProcessWithTokenW";
            launchError = "";
            return true;
        }

        errors.Add("Restricted+SECURITY_CAPABILITIES CreateProcessWithTokenW=" + restrictedWithTokenError);
        if (TryCreateProcessWhileImpersonating(restricted, executable, commandLineText, flags, environment, workDirectory, inheritHandles, attributes, desktop, out information, out var impersonateError))
        {
            LastSuccessfulLaunchPath = "Impersonate+CreateProcessW";
            launchError = "";
            return true;
        }

        errors.Add("Impersonate+CreateProcessW=" + impersonateError);
        attributes.Dispose();
        attributes = null;
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

    private static bool TryCreateProcessWithToken(
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
        if (NativeMethods.CreateProcessWithTokenW(
                token,
                0,
                executable,
                commandLine,
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

    private static bool TryCreateProcessWhileImpersonating(
        SafeAccessTokenHandle restricted,
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
        information = default;
        error = "";
        if (!NativeMethods.DuplicateTokenEx(
                restricted,
                NativeConstants.TOKEN_ALL_ACCESS_WIN8,
                IntPtr.Zero,
                NativeConstants.SecurityImpersonation,
                NativeConstants.TokenImpersonation,
                out var impersonation))
        {
            error = "DuplicateTokenEx=" + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }

        using (impersonation)
        {
            if (!NativeMethods.ImpersonateLoggedOnUser(impersonation))
            {
                error = "ImpersonateLoggedOnUser=" + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture);
                return false;
            }

            try
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
                    return true;
                }

                error = Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture);
                information = default;
                return false;
            }
            finally
            {
                NativeMethods.RevertToSelf();
            }
        }
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

    private static void GrantLaunchSurfaces(string executable, string workDirectory, string sid, ISandboxFaultInjector faultInjector)
    {
        AppContainerAclManager.GrantMinimum(workDirectory, sid, NativeConstants.SandboxWorkAccess, inherit: true, faultInjector);
        var exe = Path.GetFullPath(executable);
        if (SandboxPathPolicy.IsWindowsSystemLocation(exe))
        {
            return;
        }

        AppContainerAclManager.GrantMinimum(exe, sid, NativeConstants.SandboxExecuteAccess, inherit: false, faultInjector);
        var directory = Path.GetDirectoryName(exe);
        if (!string.IsNullOrWhiteSpace(directory) && !SandboxPathPolicy.IsWindowsSystemLocation(directory))
        {
            AppContainerAclManager.GrantMinimumRecursive(directory, sid, NativeConstants.SandboxExecuteAccess, faultInjector);
            var hostfxr = Path.Combine(directory, "hostfxr.dll");
            if (File.Exists(hostfxr))
            {
                var snapshot = AppContainerAclManager.Read(hostfxr);
                if (!snapshot.Grants(sid, NativeConstants.FILE_READ_DATA))
                {
                    throw new SandboxLayerException(
                        SandboxError.AppContainerAclFailed,
                        "AppContainer SID was not granted read access to hostfxr.dll.");
                }
            }
        }
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
        if (request.ExtraEnvironment is not null)
        {
            foreach (var pair in request.ExtraEnvironment)
            {
                if (SandboxEnvironmentPolicy.IsSecretBearingName(pair.Key))
                {
                    continue;
                }

                sanitized[pair.Key] = pair.Value;
            }
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
