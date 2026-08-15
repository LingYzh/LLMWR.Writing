using System.Diagnostics;
using System.Runtime.Versioning;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.Infrastructure.Tests;

internal static partial class Program
{
    private static readonly Guid Wp10ProjectA = Guid.Parse("018f3e78-aaa1-7abc-8def-0123456789ab");
    private static readonly Guid Wp10ProjectB = Guid.Parse("018f3e78-bbb2-7abc-8def-0123456789ab");

    [SupportedOSPlatform("windows")]
    private static void RunWp10InfrastructureTests()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("WP10 Windows sandbox tests cannot be skipped on a non-Windows runner.");
        }

        Run(nameof(AppContainerIdentityIsStableAndDistinctPerProject), AppContainerIdentityIsStableAndDistinctPerProject);
        Run(nameof(RestrictedTokenIsActuallyRestricted), RestrictedTokenIsActuallyRestricted);
        Run(nameof(AppContainerAclGrantIsMinimumAndPreservesOwnerSemantics), AppContainerAclGrantIsMinimumAndPreservesOwnerSemantics);
        Run(nameof(JobObjectLimitsAreConfigured), JobObjectLimitsAreConfigured);
        Run(nameof(CpuJobConfigurationFailureFailsClosed), CpuJobConfigurationFailureFailsClosed);
        Run(nameof(SandboxLaunchOfStagedCmdReportsChildEvidence), SandboxLaunchOfStagedCmdReportsChildEvidence);
        Run(nameof(PathGuardRejectsReparseAndOpenTimeJunctionRace), PathGuardRejectsReparseAndOpenTimeJunctionRace);
        Run(nameof(PathGuardDeniesLlmwAndOutside), PathGuardDeniesLlmwAndOutside);
        Run(nameof(HeadTailCaptureBoundsMemory), HeadTailCaptureBoundsMemory);
        Run(nameof(CorePrivilegesAreUnchangedAfterSandboxLaunch), CorePrivilegesAreUnchangedAfterSandboxLaunch);
        Run(nameof(CorePrivilegesAreRestoredAfterFaultedLaunch), CorePrivilegesAreRestoredAfterFaultedLaunch);
        Run(nameof(DefaultDesktopDaclDoesNotGainAppContainerSid), DefaultDesktopDaclDoesNotGainAppContainerSid);
        Run(nameof(NetworkIsolationQueryFailureFailsClosed), NetworkIsolationQueryFailureFailsClosed);
        Run(nameof(NetworkIsolationSetFailureFailsClosed), NetworkIsolationSetFailureFailsClosed);
        Run(nameof(PrivilegeScopeFaultRestoresPreviousState), PrivilegeScopeFaultRestoresPreviousState);
    }

    [SupportedOSPlatform("windows")]
    private static void AppContainerIdentityIsStableAndDistinctPerProject()
    {
        var first = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var again = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var other = AppContainerProfileManager.CreateOrDerive(Wp10ProjectB, NoSandboxFaultInjector.Instance);
        AssertEqual(first.AppContainerName, again.AppContainerName, "Per-project AppContainer name was not stable.");
        AssertEqual(first.AppContainerSid, again.AppContainerSid, "Per-project AppContainer SID was not stable.");
        AssertFalse(StringComparer.Ordinal.Equals(first.AppContainerSid, other.AppContainerSid),
            "Different projects shared an AppContainer SID.");
        AssertTrue(first.AppContainerName.Contains(Wp10ProjectA.ToString("N"), StringComparison.OrdinalIgnoreCase),
            "AppContainer name is not based on the Project UUID.");
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictedTokenIsActuallyRestricted()
    {
        var token = WindowsRestrictedTokenFactory.Create(NoSandboxFaultInjector.Instance);
        using (token)
        {
            AssertTrue(TokenInspector.HasRestrictions(token), "CreateRestrictedToken did not produce TokenHasRestrictions.");
            AssertFalse(TokenInspector.IsElevated(token), "Restricted token remained elevated.");
        }

        var injected = new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.RestrictedTokenInit };
        AssertThrows<SandboxLayerException>(() => WindowsRestrictedTokenFactory.Create(injected),
            "Restricted token fault injection did not fail closed.");
    }

    [SupportedOSPlatform("windows")]
    private static void AppContainerAclGrantIsMinimumAndPreservesOwnerSemantics()
    {
        using var dir = Wp10TempDir.Create();
        var work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "keep-owner.txt"), "x");
        var before = AppContainerAclManager.Read(work);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        AppContainerAclManager.GrantMinimum(work, identity.AppContainerSid, NativeConstants.SandboxWorkAccess, inherit: true, NoSandboxFaultInjector.Instance);
        var after = AppContainerAclManager.Read(work);
        AssertTrue(after.Grants(identity.AppContainerSid, NativeConstants.FILE_GENERIC_READ),
            "AppContainer SID was not granted the expected work-directory access.");
        AssertFalse(after.GrantsGenericAll(identity.AppContainerSid),
            "AppContainer SID received GENERIC_ALL/FullControl.");
        if (!before.ContainsSid("S-1-1-0"))
        {
            AssertFalse(after.ContainsSid("S-1-1-0"), "ACL grant added an Everyone ACE.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void JobObjectLimitsAreConfigured()
    {
        var limits = new SandboxResourceLimits(8L * 1024 * 1024, 4, CpuRateHundredthsPercent: 1000);
        using var job = JobObjectController.CreateConfigured(limits, NoSandboxFaultInjector.Instance);
        var info = JobObjectController.Query(job);
        AssertTrue((info.BasicLimitInformation.LimitFlags & NativeConstants.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) != 0,
            "Job is missing KILL_ON_JOB_CLOSE.");
        AssertTrue((info.BasicLimitInformation.LimitFlags & NativeConstants.JOB_OBJECT_LIMIT_ACTIVE_PROCESS) != 0,
            "Job is missing ACTIVE_PROCESS.");
        AssertTrue((info.BasicLimitInformation.LimitFlags & NativeConstants.JOB_OBJECT_LIMIT_PROCESS_MEMORY) != 0,
            "Job is missing PROCESS_MEMORY.");
        AssertEqual(4u, info.BasicLimitInformation.ActiveProcessLimit, "Active process limit was not applied.");
        AssertTrue((info.BasicLimitInformation.LimitFlags & NativeConstants.JOB_OBJECT_LIMIT_BREAKAWAY_OK) == 0,
            "Job incorrectly allows breakaway.");
        var injected = new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.JobConfiguration };
        AssertThrows<SandboxLayerException>(() => JobObjectController.CreateConfigured(limits, injected),
            "Job configuration fault injection did not fail closed.");
    }

    [SupportedOSPlatform("windows")]
    private static void CpuJobConfigurationFailureFailsClosed()
    {
        var limits = new SandboxResourceLimits(8L * 1024 * 1024, 4, CpuRateHundredthsPercent: 1000);
        var injected = new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.CpuJobConfiguration };
        AssertThrows<SandboxLayerException>(() => JobObjectController.CreateConfigured(limits, injected),
            "CPU job configuration fault injection did not fail closed.");
    }

    [SupportedOSPlatform("windows")]
    private static void SandboxLaunchOfStagedCmdReportsChildEvidence()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);
        var work = SandboxPathPolicy.RunWorkDirectory(project, "run-1");
        Directory.CreateDirectory(work);
        var cmd = Path.Combine(work, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-1", "w1", new ProjectScope(Wp10ProjectA, "ws")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            cmd,
            ["/c", "echo", "SANDBOX_OK"],
            project,
            TimeSpan.FromSeconds(10));
        var result = WindowsSandboxProcessLauncher.Launch(request, identity, work, project, NoSandboxFaultInjector.Instance, grantInternetClient: false);
        if (!result.Succeeded)
        {
            var appContainerOnly = WindowsSandboxProcessLauncher.LaunchAppContainerOnly(request, identity, work, project, NoSandboxFaultInjector.Instance);
            var restrictedOnly = WindowsSandboxProcessLauncher.LaunchRestrictedOnly(request, identity, work, project, NoSandboxFaultInjector.Instance);
            throw new InvalidOperationException(
                $"staged cmd failed: error={result.Error} deny={result.DenyReason} exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr} | appcontainer-only: error={appContainerOnly.Error} deny={appContainerOnly.DenyReason} exit={appContainerOnly.ExitCode} stdout={appContainerOnly.Stdout} | restricted-only: error={restrictedOnly.Error} deny={restrictedOnly.DenyReason} exit={restrictedOnly.ExitCode} stdout={restrictedOnly.Stdout}");
        }
        AssertTrue(result.Stdout.Contains("SANDBOX_OK", StringComparison.OrdinalIgnoreCase),
            "Staged cmd.exe did not echo SANDBOX_OK from inside the sandbox.");
    }

    [SupportedOSPlatform("windows")]
    private static void PathGuardRejectsReparseAndOpenTimeJunctionRace()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(Path.Combine(project, "safe"));
        Directory.CreateDirectory(outside);
        var safeFile = Path.Combine(project, "safe", "file.txt");
        var outsideFile = Path.Combine(outside, "file.txt");
        File.WriteAllText(safeFile, "inside");
        File.WriteAllText(outsideFile, "outside-original");
        var guard = new WindowsSandboxPathGuard();

        CreateJunction(Path.Combine(project, "link"), outside);
        var throughLink = guard.TryOpenRead(project, "run-1", "link/file.txt", out _);
        AssertTrue(throughLink == SandboxError.ReparsePointRejected, "Broker read through a junction was not denied.");
        AssertEqual("outside-original", File.ReadAllText(outsideFile), "Junction read mutated outside bytes.");

        var allowed = guard.TryOpenRead(project, "run-1", "safe/file.txt", out var bytes);
        AssertTrue(allowed is null, "Safe surface read failed before the race.");
        AssertEqual("inside", System.Text.Encoding.UTF8.GetString(bytes), "Safe surface read returned unexpected bytes.");

        var safeDir = Path.Combine(project, "safe");
        var relocated = Path.Combine(project, "safe.real");
        Directory.Move(safeDir, relocated);
        CreateJunction(safeDir, outside);
        var raced = guard.TryOpenRead(project, "run-1", "safe/file.txt", out _);
        AssertTrue(raced == SandboxError.ReparsePointRejected, "Open-time junction race was not denied.");
        AssertEqual("outside-original", File.ReadAllText(outsideFile), "Junction race wrote outside bytes.");
    }

    [SupportedOSPlatform("windows")]
    private static void PathGuardDeniesLlmwAndOutside()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".llmw"));
        File.WriteAllText(Path.Combine(project, ".llmw", "project.db"), "secret-db");
        var work = SandboxPathPolicy.RunWorkDirectory(project, "run-1");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "ok.txt"), "ok");
        var guard = new WindowsSandboxPathGuard();
        AssertTrue(guard.TryOpenRead(project, "run-1", ".llmw/project.db", out _) == SandboxError.PathOutOfScope,
            ".llmw internals were readable through the path guard.");
        AssertTrue(guard.TryOpenRead(project, "run-1", ".llmw.sandbox/runs/run-1/work/ok.txt", out var bytes) is null,
            "Designated work surface was not readable.");
        AssertEqual("ok", System.Text.Encoding.UTF8.GetString(bytes), "Work surface bytes did not round-trip.");
        AssertTrue(guard.TryOpenWrite(project, "run-1", "Draft/chapter.txt", "nope"u8) == SandboxError.PathOutOfScope,
            "Draft write was allowed through the generic sandbox write seam.");
    }

    private static void HeadTailCaptureBoundsMemory()
    {
        var capture = new StreamingHeadTail();
        capture.Append(Enumerable.Repeat((byte)'A', 200_000).ToArray());
        capture.Append(Enumerable.Repeat((byte)'B', 200_000).ToArray());
        AssertTrue(capture.Truncated, "A 400KiB stream was not truncated.");
        AssertTrue(capture.TotalBytes == 400_000, "Capture lost the total byte count.");
        var text = capture.ToUtf8String();
        AssertTrue(text.Length <= SandboxPathPolicy.MaxCapturedOutputBytes,
            "Captured text exceeded the 256KiB head+tail budget.");
        AssertTrue(text.StartsWith(new string('A', 64), StringComparison.Ordinal), "Head bytes were lost.");
        AssertTrue(text.EndsWith(new string('B', 64), StringComparison.Ordinal), "Tail bytes were lost.");
    }

    [SupportedOSPlatform("windows")]
    private static void CorePrivilegesAreUnchangedAfterSandboxLaunch()
    {
        var before = CoreProcessPrivilegeSnapshot.Capture();
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);
        var work = SandboxPathPolicy.RunWorkDirectory(project, "run-1");
        Directory.CreateDirectory(work);
        var cmd = Path.Combine(work, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-1", "w1", new ProjectScope(Wp10ProjectA, "ws")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            cmd,
            ["/c", "echo", "PRIV_OK"],
            project,
            TimeSpan.FromSeconds(10));
        for (var i = 0; i < 8; i++)
        {
            _ = WindowsSandboxProcessLauncher.Launch(request, identity, work, project, NoSandboxFaultInjector.Instance, grantInternetClient: false);
        }

        var after = CoreProcessPrivilegeSnapshot.Capture();
        AssertEqual(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeIncreaseQuotaPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(after, "SeIncreaseQuotaPrivilege"),
            "SeIncreaseQuotaPrivilege drifted after sandbox launches.");
        AssertEqual(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeAssignPrimaryTokenPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(after, "SeAssignPrimaryTokenPrivilege"),
            "SeAssignPrimaryTokenPrivilege drifted after sandbox launches.");
    }

    [SupportedOSPlatform("windows")]
    private static void CorePrivilegesAreRestoredAfterFaultedLaunch()
    {
        var before = CoreProcessPrivilegeSnapshot.Capture();
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);
        var work = SandboxPathPolicy.RunWorkDirectory(project, "run-1");
        Directory.CreateDirectory(work);
        var cmd = Path.Combine(work, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-1", "w1", new ProjectScope(Wp10ProjectA, "ws")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            cmd,
            ["/c", "echo", "PRIV_FAIL"],
            project,
            TimeSpan.FromSeconds(5));
        var result = WindowsSandboxProcessLauncher.Launch(
            request,
            identity,
            work,
            project,
            new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.CreateProcess },
            grantInternetClient: false);
        AssertTrue(!result.Succeeded, "Injected CreateProcess fault succeeded.");
        var after = CoreProcessPrivilegeSnapshot.Capture();
        AssertEqual(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeIncreaseQuotaPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(after, "SeIncreaseQuotaPrivilege"),
            "SeIncreaseQuotaPrivilege drifted after a faulted launch.");
        AssertEqual(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeAssignPrimaryTokenPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(after, "SeAssignPrimaryTokenPrivilege"),
            "SeAssignPrimaryTokenPrivilege drifted after a faulted launch.");
    }

    [SupportedOSPlatform("windows")]
    private static void DefaultDesktopDaclDoesNotGainAppContainerSid()
    {
        var winsta = NativeMethods.GetProcessWindowStation();
        var desktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        var beforeStation = AppContainerAclManager.ReadUserObject(winsta);
        var beforeDesktop = AppContainerAclManager.ReadUserObject(desktop);
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);
        var work = SandboxPathPolicy.RunWorkDirectory(project, "run-desk");
        Directory.CreateDirectory(work);
        var cmd = Path.Combine(work, "cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-desk", "w1", new ProjectScope(Wp10ProjectA, "ws-desk")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            cmd,
            ["/c", "echo", "DESK_OK"],
            project,
            TimeSpan.FromSeconds(10));
        _ = WindowsSandboxProcessLauncher.Launch(request, identity, work, project, NoSandboxFaultInjector.Instance, grantInternetClient: false);
        var afterStation = AppContainerAclManager.ReadUserObject(winsta);
        var afterDesktop = AppContainerAclManager.ReadUserObject(desktop);
        AssertEqual(beforeStation.ContainsSid(identity.AppContainerSid), afterStation.ContainsSid(identity.AppContainerSid),
            "Sandbox launch added the AppContainer SID to the current window station DACL.");
        AssertEqual(beforeDesktop.ContainsSid(identity.AppContainerSid), afterDesktop.ContainsSid(identity.AppContainerSid),
            "Sandbox launch added the AppContainer SID to the current default desktop DACL.");
    }

    [SupportedOSPlatform("windows")]
    private static void NetworkIsolationQueryFailureFailsClosed()
    {
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        AssertThrows<SandboxLayerException>(
            () => AppContainerNetworkIsolation.EnsureLoopbackNotExempt(
                identity.AppContainerSid,
                new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.NetworkIsolationQuery }),
            "Network isolation query failure did not fail closed.");
    }

    [SupportedOSPlatform("windows")]
    private static void NetworkIsolationSetFailureFailsClosed()
    {
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        AssertThrows<SandboxLayerException>(
            () => AppContainerNetworkIsolation.EnsureLoopbackNotExempt(
                identity.AppContainerSid,
                new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.NetworkIsolationSet }),
            "Network isolation set failure did not fail closed.");
    }

    [SupportedOSPlatform("windows")]
    private static void PrivilegeScopeFaultRestoresPreviousState()
    {
        var before = CoreProcessPrivilegeSnapshot.Capture();
        try
        {
            using (PrivilegeScope.EnableOnCurrentProcess(
                       "SeIncreaseQuotaPrivilege",
                       new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.PrivilegeScopedEnable }))
            {
            }

            throw new InvalidOperationException("PrivilegeScopedEnable fault did not throw.");
        }
        catch (SandboxLayerException)
        {
        }

        var after = CoreProcessPrivilegeSnapshot.Capture();
        AssertEqual(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeIncreaseQuotaPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(after, "SeIncreaseQuotaPrivilege"),
            "Scoped privilege fault left SeIncreaseQuotaPrivilege enabled.");
    }

    private static void CreateJunction(string link, string target)
    {
        if (Directory.Exists(link))
        {
            Directory.Delete(link);
        }

        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start mklink.");
        process.WaitForExit(10_000);
        if (!Directory.Exists(link))
        {
            throw new InvalidOperationException($"Failed to create junction {link} -> {target}: {process.StandardError.ReadToEnd()}");
        }
    }

    private sealed class Wp10TempDir : IDisposable
    {
        private Wp10TempDir(string path) => Path = path;

        public string Path { get; }

        public static Wp10TempDir Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LLMW.Writing.WP10", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new Wp10TempDir(path);
        }

        public void Dispose()
        {
            try
            {
                DeleteTree(Path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var child in Directory.GetDirectories(path))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(child);
                }
                else
                {
                    DeleteTree(child);
                }
            }

            foreach (var file in Directory.GetFiles(path))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            Directory.Delete(path);
        }
    }
}
