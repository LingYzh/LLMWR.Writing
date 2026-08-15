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
        Run(nameof(ToolStagingClosureExcludesUnrelatedFiles), ToolStagingClosureExcludesUnrelatedFiles);
        Run(nameof(ToolStagingIdentityDoesNotKeepStaleDependencies), ToolStagingIdentityDoesNotKeepStaleDependencies);
        Run(nameof(ToolStagingDoesNotFollowRuntimesReparse), ToolStagingDoesNotFollowRuntimesReparse);
        Run(nameof(ToolStagingLoopingReparseDoesNotHang), ToolStagingLoopingReparseDoesNotHang);
        Run(nameof(SandboxRootJunctionFailsClosed), SandboxRootJunctionFailsClosed);
        Run(nameof(SandboxRunsJunctionFailsClosed), SandboxRunsJunctionFailsClosed);
        Run(nameof(SandboxWorkJunctionFailsClosed), SandboxWorkJunctionFailsClosed);
        Run(nameof(ToolStagingIdentityJunctionFailsClosed), ToolStagingIdentityJunctionFailsClosed);
        Run(nameof(JunctionRaceAfterValidateFailsBeforeAcl), JunctionRaceAfterValidateFailsBeforeAcl);
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
        AssertTrue(guard.TryOpenRead(project, "run-1", ".llmw.sandbox/runs/run-1/work/ok.txt", out var bytes) == SandboxError.PathOutOfScope,
            "Generic ProjectFile.Read of designated work was allowed.");
        AssertTrue(bytes is null || bytes.Length == 0, "Generic sandbox work read returned bytes.");
        AssertTrue(guard.TryOpenWrite(project, "run-1", ".llmw.sandbox/runs/run-1/work/typed.txt", "typed"u8) is null,
            "Typed sandbox work write was denied.");
        AssertEqual("typed", File.ReadAllText(Path.Combine(work, "typed.txt")), "Typed work write did not persist.");
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

    [SupportedOSPlatform("windows")]
    private static void ToolStagingClosureExcludesUnrelatedFiles()
    {
        using var dir = Wp10TempDir.Create();
        var source = Path.Combine(dir.Path, "tool");
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);
        var exe = WriteDotNetTool(
            source,
            "tool",
            declared: new Dictionary<string, byte[]> { ["actual.dll"] = [1, 2, 3] },
            undeclared: new Dictionary<string, byte[]>
            {
                ["credentials.json"] = "CREDENTIALS"u8.ToArray(),
                ["secret.config"] = "SECRET-CONFIG"u8.ToArray(),
                ["unrelated.dll"] = [9, 9, 9],
                ["other.exe"] = [8, 8, 8]
            });
        var closure = SandboxToolStager.ResolveClosureForTests(exe);
        AssertFalse(closure.Any(path => path.Contains("credentials", StringComparison.OrdinalIgnoreCase)),
            "credentials.json was included in the dependency closure.");
        AssertFalse(closure.Any(path => path.Contains("secret.config", StringComparison.OrdinalIgnoreCase)),
            "secret.config was included in the dependency closure.");
        AssertFalse(closure.Any(path => path.Equals("unrelated.dll", StringComparison.OrdinalIgnoreCase)),
            "unrelated.dll was included without a .deps.json reference.");
        AssertFalse(closure.Any(path => path.Equals("other.exe", StringComparison.OrdinalIgnoreCase)),
            "other.exe was included without a .deps.json reference.");
        AssertTrue(closure.Any(path => path.Equals("actual.dll", StringComparison.OrdinalIgnoreCase)),
            "A .deps.json-declared assembly was omitted.");
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var staged = SandboxToolStager.ResolveLaunchExecutable(
            project,
            exe,
            SandboxPathPolicy.RunWorkDirectory(project, "run-1"),
            identity.AppContainerSid,
            NoSandboxFaultInjector.Instance);
        var staging = Path.GetDirectoryName(staged) ?? throw new InvalidOperationException("Staged executable directory missing.");
        AssertFalse(File.Exists(Path.Combine(staging, "credentials.json")), "credentials.json was copied into staging.");
        AssertFalse(File.Exists(Path.Combine(staging, "secret.config")), "secret.config was copied into staging.");
        AssertFalse(File.Exists(Path.Combine(staging, "unrelated.dll")), "unrelated.dll was copied into staging.");
        AssertFalse(File.Exists(Path.Combine(staging, "other.exe")), "other.exe was copied into staging.");
        AssertTrue(File.Exists(Path.Combine(staging, "actual.dll")), "Declared actual.dll was not staged.");
    }

    [SupportedOSPlatform("windows")]
    private static void ToolStagingIdentityDoesNotKeepStaleDependencies()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        Directory.CreateDirectory(project);
        var firstDir = Path.Combine(dir.Path, "v1");
        var firstExe = WriteDotNetTool(
            firstDir,
            "tool",
            declared: new Dictionary<string, byte[]> { ["old.dll"] = [1] },
            undeclared: new Dictionary<string, byte[]>());
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var firstStaged = SandboxToolStager.ResolveLaunchExecutable(
            project,
            firstExe,
            SandboxPathPolicy.RunWorkDirectory(project, "run-1"),
            identity.AppContainerSid,
            NoSandboxFaultInjector.Instance);
        var firstStaging = Path.GetDirectoryName(firstStaged)!;
        AssertTrue(File.Exists(Path.Combine(firstStaging, "old.dll")), "First closure did not stage old.dll.");

        var secondDir = Path.Combine(dir.Path, "v2");
        var secondExe = WriteDotNetTool(
            secondDir,
            "tool",
            declared: new Dictionary<string, byte[]> { ["fresh.dll"] = [2] },
            undeclared: new Dictionary<string, byte[]> { ["old.dll"] = [1] });
        var secondStaged = SandboxToolStager.ResolveLaunchExecutable(
            project,
            secondExe,
            SandboxPathPolicy.RunWorkDirectory(project, "run-1"),
            identity.AppContainerSid,
            NoSandboxFaultInjector.Instance);
        var secondStaging = Path.GetDirectoryName(secondStaged)!;
        AssertFalse(string.Equals(firstStaging, secondStaging, StringComparison.OrdinalIgnoreCase),
            "A changed dependency closure reused the previous staging directory.");
        AssertFalse(File.Exists(Path.Combine(secondStaging, "old.dll")), "Second staging tree still exposed old.dll.");
        AssertTrue(File.Exists(Path.Combine(secondStaging, "fresh.dll")), "Second staging tree omitted the current dependency.");
    }

    [SupportedOSPlatform("windows")]
    private static void ToolStagingDoesNotFollowRuntimesReparse()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        var source = Path.Combine(dir.Path, "tool");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "outside-secret.dll");
        File.WriteAllBytes(secret, "OUTSIDE-SECRET"u8.ToArray());
        var outsideAcl = AclFingerprint(AppContainerAclManager.Read(outside));
        var exe = WriteDotNetTool(
            source,
            "tool",
            declared: new Dictionary<string, byte[]> { ["runtimes/win/native/outside-secret.dll"] = [] },
            undeclared: new Dictionary<string, byte[]>(),
            createDeclaredFiles: false);
        Directory.CreateDirectory(Path.Combine(source, "runtimes"));
        Directory.Delete(Path.Combine(source, "runtimes"));
        CreateJunction(Path.Combine(source, "runtimes"), outside);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        try
        {
            _ = SandboxToolStager.ResolveLaunchExecutable(
                project,
                exe,
                SandboxPathPolicy.RunWorkDirectory(project, "run-1"),
                identity.AppContainerSid,
                NoSandboxFaultInjector.Instance);
            throw new InvalidOperationException("Staging followed a runtimes reparse point.");
        }
        catch (SandboxLayerException exception)
        {
            AssertTrue(exception.Error == SandboxError.ReparsePointRejected, "Runtimes reparse did not fail closed.");
        }

        AssertEqual("OUTSIDE-SECRET", File.ReadAllText(secret), "Runtimes junction staging mutated outside bytes.");
        AssertFalse(Directory.Exists(Path.Combine(outside, "win")), "Staging created directories on the outside side of a runtimes junction.");
        AssertEqual(outsideAcl, AclFingerprint(AppContainerAclManager.Read(outside)), "Staging changed the outside ACL.");
        AssertFalse(AppContainerAclManager.Read(outside).ContainsSid(identity.AppContainerSid),
            "Outside received an AppContainer ACE through a runtimes junction.");
        var tools = Path.Combine(project, ".llmw.sandbox", "tools");
        if (Directory.Exists(tools))
        {
            foreach (var staged in Directory.GetFiles(tools, "outside-secret.dll", SearchOption.AllDirectories))
            {
                throw new InvalidOperationException("Outside secret was staged: " + staged);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ToolStagingLoopingReparseDoesNotHang()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var source = Path.Combine(dir.Path, "tool");
        Directory.CreateDirectory(project);
        var exe = WriteDotNetTool(
            source,
            "tool",
            declared: new Dictionary<string, byte[]> { ["runtimes/loop/x.dll"] = [] },
            undeclared: new Dictionary<string, byte[]>(),
            createDeclaredFiles: false);
        var runtimes = Path.Combine(source, "runtimes");
        Directory.CreateDirectory(runtimes);
        CreateJunction(Path.Combine(runtimes, "loop"), runtimes);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var started = Stopwatch.StartNew();
        try
        {
            _ = SandboxToolStager.ResolveLaunchExecutable(
                project,
                exe,
                SandboxPathPolicy.RunWorkDirectory(project, "run-1"),
                identity.AppContainerSid,
                NoSandboxFaultInjector.Instance);
            throw new InvalidOperationException("Looping reparse staging succeeded.");
        }
        catch (SandboxLayerException exception)
        {
            AssertTrue(exception.Error == SandboxError.ReparsePointRejected, "Looping reparse did not fail closed.");
        }

        AssertTrue(started.Elapsed < TimeSpan.FromSeconds(2), "Looping reparse hung during staging.");
    }

    [SupportedOSPlatform("windows")]
    private static void SandboxRootJunctionFailsClosed()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        File.WriteAllText(marker, "outside-original");
        var outsideAcl = AclFingerprint(AppContainerAclManager.Read(outside));
        CreateJunction(Path.Combine(project, ".llmw.sandbox"), outside);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var host = new WindowsSandboxHost(project, new ProjectScope(Wp10ProjectA, "ws-root-j"), cmd);
        AssertTrue(host.Availability is not SandboxAvailability.Available, "Sandbox initialized through a .llmw.sandbox junction.");
        AssertTrue(host.InitializationError == SandboxError.ReparsePointRejected, "Sandbox root junction did not report ReparsePointRejected.");
        AssertFalse(Directory.Exists(Path.Combine(outside, "runs")), "Initialization created runs/ on the outside of a sandbox-root junction.");
        AssertFalse(Directory.Exists(Path.Combine(outside, "tools")), "Initialization created tools/ on the outside of a sandbox-root junction.");
        AssertEqual("outside-original", File.ReadAllText(marker), "Sandbox-root junction mutated outside bytes.");
        AssertEqual(outsideAcl, AclFingerprint(AppContainerAclManager.Read(outside)), "Sandbox-root junction changed the outside ACL.");
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-1", "w1", new ProjectScope(Wp10ProjectA, "ws-root-j")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            cmd,
            ["/c", "echo", "NO"],
            project,
            TimeSpan.FromSeconds(5));
        var result = host.Execute(request);
        AssertTrue(!result.Succeeded, "A child started after a sandbox-root junction was rejected.");
        AssertTrue(result.ProcessId is null, "Sandbox-root junction produced a process id.");
    }

    [SupportedOSPlatform("windows")]
    private static void SandboxRunsJunctionFailsClosed()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        File.WriteAllText(marker, "outside-original");
        var outsideAcl = AclFingerprint(AppContainerAclManager.Read(outside));
        Directory.CreateDirectory(Path.Combine(project, ".llmw.sandbox"));
        CreateJunction(Path.Combine(project, ".llmw.sandbox", "runs"), outside);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var host = new WindowsSandboxHost(project, new ProjectScope(Wp10ProjectA, "ws-runs-j"), cmd);
        AssertTrue(host.Availability is not SandboxAvailability.Available, "Sandbox initialized through a runs junction.");
        AssertTrue(host.InitializationError == SandboxError.ReparsePointRejected, "Runs junction did not report ReparsePointRejected.");
        AssertFalse(Directory.Exists(Path.Combine(outside, "self-test")), "Initialization created a run directory on the outside of a runs junction.");
        AssertEqual("outside-original", File.ReadAllText(marker), "Runs junction mutated outside bytes.");
        AssertEqual(outsideAcl, AclFingerprint(AppContainerAclManager.Read(outside)), "Runs junction changed the outside ACL.");
        var result = host.Execute(new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-1", "w1", new ProjectScope(Wp10ProjectA, "ws-runs-j")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            cmd,
            ["/c", "echo", "NO"],
            project,
            TimeSpan.FromSeconds(5)));
        AssertTrue(!result.Succeeded, "A child started after a runs junction was rejected.");
        AssertTrue(result.ProcessId is null, "Runs junction produced a process id.");
    }

    [SupportedOSPlatform("windows")]
    private static void SandboxWorkJunctionFailsClosed()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        File.WriteAllText(marker, "outside-original");
        var outsideAcl = AclFingerprint(AppContainerAclManager.Read(outside));
        var work = SandboxPathPolicy.RunWorkDirectory(project, "run-1");
        Directory.CreateDirectory(Path.GetDirectoryName(work)!);
        CreateJunction(work, outside);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-1", "w1", new ProjectScope(Wp10ProjectA, "ws-work-j")),
            new TrustedNativePrincipalSource("infra-wp10").ResolveUserInteractive(),
            Capability.ShellExecute,
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/c", "echo", "NO"],
            project,
            TimeSpan.FromSeconds(5));
        var result = WindowsSandboxProcessLauncher.Launch(request, identity, work, project, NoSandboxFaultInjector.Instance, grantInternetClient: false);
        AssertTrue(!result.Succeeded, "Launch succeeded through a work-directory junction.");
        AssertTrue(result.Error == SandboxError.ReparsePointRejected, "Work junction did not fail closed.");
        AssertTrue(result.ProcessId is null, "Work junction started a child.");
        AssertEqual("outside-original", File.ReadAllText(marker), "Work junction mutated outside bytes.");
        AssertEqual(outsideAcl, AclFingerprint(AppContainerAclManager.Read(outside)), "Work junction changed the outside ACL.");
        AssertFalse(AppContainerAclManager.Read(outside).ContainsSid(identity.AppContainerSid),
            "Work junction granted the AppContainer SID on outside.");
        AssertFalse(File.Exists(Path.Combine(outside, "cmd.exe")), "Work junction copied an executable onto outside.");
    }

    [SupportedOSPlatform("windows")]
    private static void ToolStagingIdentityJunctionFailsClosed()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        var source = Path.Combine(dir.Path, "tool");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        File.WriteAllText(marker, "outside-original");
        var outsideAcl = AclFingerprint(AppContainerAclManager.Read(outside));
        var exe = WriteDotNetTool(
            source,
            "tool",
            declared: new Dictionary<string, byte[]> { ["actual.dll"] = [1] },
            undeclared: new Dictionary<string, byte[]>());
        var stagingId = SandboxToolStager.ComputeStagingIdentity(exe);
        Directory.CreateDirectory(Path.Combine(project, ".llmw.sandbox", "tools"));
        CreateJunction(Path.Combine(project, ".llmw.sandbox", "tools", stagingId), outside);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        try
        {
            _ = SandboxToolStager.ResolveLaunchExecutable(
                project,
                exe,
                SandboxPathPolicy.RunWorkDirectory(project, "run-1"),
                identity.AppContainerSid,
                NoSandboxFaultInjector.Instance);
            throw new InvalidOperationException("Staging succeeded through a staging-id junction.");
        }
        catch (SandboxLayerException exception)
        {
            AssertTrue(exception.Error == SandboxError.ReparsePointRejected, "Staging-id junction did not fail closed.");
        }

        AssertEqual("outside-original", File.ReadAllText(marker), "Staging-id junction mutated outside bytes.");
        AssertEqual(outsideAcl, AclFingerprint(AppContainerAclManager.Read(outside)), "Staging-id junction changed the outside ACL.");
        AssertFalse(AppContainerAclManager.Read(outside).ContainsSid(identity.AppContainerSid),
            "Staging-id junction granted the AppContainer SID on outside.");
        AssertFalse(File.Exists(Path.Combine(outside, "tool.exe")), "Staging copied the executable onto outside.");
        AssertFalse(File.Exists(Path.Combine(outside, "actual.dll")), "Staging copied a dependency onto outside.");
    }

    [SupportedOSPlatform("windows")]
    private static void JunctionRaceAfterValidateFailsBeforeAcl()
    {
        using var dir = Wp10TempDir.Create();
        var project = Path.Combine(dir.Path, "project");
        var outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        File.WriteAllText(marker, "outside-original");
        var outsideAcl = AclFingerprint(AppContainerAclManager.Read(outside));
        var work = SafeSandboxHierarchy.EnsureRunWorkDirectory(project, "run-race");
        SafeSandboxHierarchy.VerifyExistingChain(
            project,
            SandboxPathPolicy.SandboxRootDirectoryName,
            "runs",
            "run-race",
            "work");
        Directory.Delete(work);
        CreateJunction(work, outside);
        var identity = AppContainerProfileManager.CreateOrDerive(Wp10ProjectA, NoSandboxFaultInjector.Instance);
        try
        {
            AppContainerAclManager.GrantMinimum(
                work,
                identity.AppContainerSid,
                NativeConstants.SandboxWorkAccess,
                inherit: true,
                NoSandboxFaultInjector.Instance);
            throw new InvalidOperationException("ACL grant succeeded after the work directory was replaced with a junction.");
        }
        catch (SandboxLayerException exception)
        {
            AssertTrue(exception.Error == SandboxError.ReparsePointRejected, "Post-validate junction race did not fail closed.");
        }

        AssertEqual("outside-original", File.ReadAllText(marker), "Post-validate junction race mutated outside bytes.");
        AssertEqual(outsideAcl, AclFingerprint(AppContainerAclManager.Read(outside)), "Post-validate junction race changed the outside ACL.");
        AssertFalse(AppContainerAclManager.Read(outside).ContainsSid(identity.AppContainerSid),
            "Post-validate junction race granted the AppContainer SID on outside.");
    }

    private static string WriteDotNetTool(
        string directory,
        string stem,
        IReadOnlyDictionary<string, byte[]> declared,
        IReadOnlyDictionary<string, byte[]> undeclared,
        bool createDeclaredFiles = true)
    {
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, stem + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);
        File.WriteAllText(Path.Combine(directory, stem + ".runtimeconfig.json"), """{"runtimeOptions":{"tfm":"net8.0"}}""");
        File.WriteAllBytes(Path.Combine(directory, stem + ".dll"), [7]);
        var runtimeEntries = string.Join(
            ",",
            declared.Keys.Select(key => "\"" + key.Replace('\\', '/') + "\":{}").Prepend("\"" + stem + ".dll\":{}"));
        File.WriteAllText(
            Path.Combine(directory, stem + ".deps.json"),
            "{\"targets\":{\"net8.0\":{\"" + stem + "/1.0.0\":{\"runtime\":{" + runtimeEntries + "}}}}}");
        if (createDeclaredFiles)
        {
            foreach (var pair in declared)
            {
                WriteRelativeFile(directory, pair.Key, pair.Value);
            }
        }

        foreach (var pair in undeclared)
        {
            WriteRelativeFile(directory, pair.Key, pair.Value);
        }

        return exe;
    }

    private static void WriteRelativeFile(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(path, bytes);
    }

    private static string AclFingerprint(AclSnapshot snapshot) =>
        string.Join(";", snapshot.AllowedAces.Select(ace => ace.Sid + ":" + ace.Mask.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + ace.Flags).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

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
