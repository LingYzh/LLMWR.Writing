using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Persistence.Sqlite;
using LLMW.Writing.Infrastructure.Sandbox;
using LLMW.Writing.Infrastructure.Sandbox.Native;

namespace LLMW.Writing.IntegrationTests;

internal static partial class Program
{
    [SupportedOSPlatform("windows")]
    private static void RunWp10Tests()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("WP10 Windows sandbox tests cannot be skipped on a non-Windows runner.");
        }

        SandboxChildHasRestrictedAppContainerToken();
        SandboxWorkSurfaceWorksAndSensitivePathsAreDenied();
        GenericNetworkIsDeniedWithoutLoopbackExemption();
        ParentSecretIsNotInherited();
        ArgvRoundTripsExactTokens();
        OutputIsBoundedHeadAndTailWithoutDeadlock();
        TimeoutKillsProcessTree();
        KillOnCloseContainsChildren();
        FaultInjectionFailsClosedWithoutUnsandboxedChild();
        BrokerEnforcesWp09AndFinalRecheck();
        BypassCannotDisableSandbox();
        JobActiveProcessLimitIsEnforced();
        JobMemoryLimitKillsOversizedChild();
        PowerShellCompatibilityIsProbedWithoutDisablingSandbox();
        ForgedProjectRootIsDenied();
        ForgedProjectScopeIsDenied();
        ForgedRunIdIsDenied();
        ForgedWorkerInstanceIdIsDenied();
        RevokedSessionAfterResolutionIsDenied();
        ExpiredSessionAfterResolutionIsDenied();
        DurableRoleChangeDoesNotKeepStaleMaximum();
        ChildIsNotOnInteractiveDefaultDesktop();
        DefaultDesktopDaclUnchangedAfterSandbox();
        NonSystemExecutableSiblingFilesAreDenied();
        SiblingRunWorkIsDeniedAtOs();
        CorePrivilegesUnchangedAfterSuccessfulAndFaultedLaunch();
        ExtraEnvironmentOverridesAreDenied();
        RequestedCpuPolicyFailureFailsClosed();
        AgentRunGenericProjectFileReadDeniesInternalSandbox();
        StagedToolOmitsUnrelatedSourceFilesFromChild();
        Console.WriteLine("WP10 sandbox integration tests passed (36).");
    }

    private static void SandboxChildHasRestrictedAppContainerToken()
    {
        using var fixture = Wp10Fixture.Create();
        var result = fixture.RunProbe("whoami-token");
        Wp10True(result.Succeeded, $"whoami-token failed: {result.Error} {result.DenyReason} {result.Stderr} {result.Stdout}");
        Wp10True(result.Stdout.Contains("\"hasRestrictions\":true", StringComparison.OrdinalIgnoreCase) ||
                 result.Stdout.Contains("\"hasRestrictions\": true", StringComparison.OrdinalIgnoreCase),
            "Child token does not report TokenHasRestrictions.");
        Wp10True(result.Stdout.Contains("\"isAppContainer\":true", StringComparison.OrdinalIgnoreCase) ||
                 result.Stdout.Contains("\"isAppContainer\": true", StringComparison.OrdinalIgnoreCase),
            "Child is not an AppContainer process.");
        Wp10True(result.Stdout.Contains(fixture.Host.Identity!.AppContainerSid, StringComparison.OrdinalIgnoreCase),
            "Child AppContainer SID did not match the project identity.");
        Wp10True(result.Stdout.Contains("\"inJob\":true", StringComparison.OrdinalIgnoreCase) ||
                 result.Stdout.Contains("\"inJob\": true", StringComparison.OrdinalIgnoreCase),
            "Child is not in a Job Object.");
        Wp10True(!result.Stdout.Contains("SeDebugPrivilege", StringComparison.OrdinalIgnoreCase),
            "Child unexpectedly retained SeDebugPrivilege.");
    }

    private static void SandboxWorkSurfaceWorksAndSensitivePathsAreDenied()
    {
        using var fixture = Wp10Fixture.Create();
        var workFile = Path.Combine(fixture.WorkDirectory, "out.txt");
        var allowed = fixture.RunProbe("write-file", workFile, "sandbox-ok");
        Wp10True(allowed.Succeeded && allowed.Stdout.Contains("WRITE_OK", StringComparison.Ordinal),
            "Worker could not write the designated sandbox work surface.");
        Wp10Equal("sandbox-ok", File.ReadAllText(workFile), "Work surface bytes did not persist.");

        var llmw = fixture.RunProbe("write-file", fixture.LlmwDbPath, "pwn");
        Wp10True(!llmw.Succeeded && llmw.Stdout.Contains("WRITE_DENIED", StringComparison.Ordinal),
            "Worker wrote .llmw internals.");
        Wp10Equal("authority", File.ReadAllText(fixture.LlmwDbPath), ".llmw bytes were modified.");

        var outside = fixture.RunProbe("write-file", fixture.OutsideFile, "pwn");
        Wp10True(!outside.Succeeded && outside.Stdout.Contains("WRITE_DENIED", StringComparison.Ordinal),
            "Worker wrote outside the project.");
        Wp10Equal("outside-original", File.ReadAllText(fixture.OutsideFile), "Outside bytes were modified.");

        var windows = fixture.RunProbe("write-file", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "llmw-wp10-should-not-exist.txt"), "pwn");
        Wp10True(!windows.Succeeded, "Worker wrote to the Windows directory.");
    }

    private static void GenericNetworkIsDeniedWithoutLoopbackExemption()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var fixture = Wp10Fixture.Create();
        var result = fixture.RunProbe("connect", "127.0.0.1", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Wp10True(result.Stdout.Contains("CONNECT_DENIED", StringComparison.Ordinal),
            $"Sandbox child established a loopback TCP connection under the default no-network profile. launch={WindowsSandboxProcessLauncher.LastSuccessfulLaunchPath} stdout={result.Stdout} error={result.Error} deny={result.DenyReason}");
        listener.Stop();
    }

    private static void ParentSecretIsNotInherited()
    {
        Environment.SetEnvironmentVariable("LLMW_TEST_SECRET", "do-not-log-this-value");
        Environment.SetEnvironmentVariable("LLMW_CORE_BOOTSTRAP_TOKEN", "do-not-log-bootstrap");
        try
        {
            using var fixture = Wp10Fixture.Create();
            var secret = fixture.RunProbe("print-env-has", "LLMW_TEST_SECRET");
            var bootstrap = fixture.RunProbe("print-env-has", "LLMW_CORE_BOOTSTRAP_TOKEN");
            Wp10True(secret.Stdout.Contains("\"exists\":false", StringComparison.OrdinalIgnoreCase) ||
                     secret.Stdout.Contains("\"exists\": false", StringComparison.OrdinalIgnoreCase),
                "Child inherited LLMW_TEST_SECRET.");
            Wp10True(bootstrap.Stdout.Contains("\"exists\":false", StringComparison.OrdinalIgnoreCase) ||
                     bootstrap.Stdout.Contains("\"exists\": false", StringComparison.OrdinalIgnoreCase),
                "Child inherited the Core bootstrap token.");
            Wp10True(!secret.Stdout.Contains("do-not-log", StringComparison.Ordinal) &&
                     !bootstrap.Stdout.Contains("do-not-log", StringComparison.Ordinal),
                "A secret value was printed to the test log.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLMW_TEST_SECRET", null);
            Environment.SetEnvironmentVariable("LLMW_CORE_BOOTSTRAP_TOKEN", null);
        }
    }

    private static void ArgvRoundTripsExactTokens()
    {
        using var fixture = Wp10Fixture.Create();
        string[] payload = ["argv", "a b", "", "quote\"here", @"back\slash", "中文", "!@#"];
        var result = fixture.RunProbe(payload);
        Wp10True(result.Succeeded, $"argv probe failed: {result.Error} {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout.Trim().Split('\n').Last());
        var actual = json.RootElement.EnumerateArray().Select(element => element.GetString() ?? "").ToArray();
        Wp10Equal(payload.Length, actual.Length, "Child argv length drifted.");
        for (var i = 0; i < payload.Length; i++)
        {
            Wp10Equal(payload[i], actual[i], $"Child argv[{i}] did not round-trip.");
        }
    }

    private static void OutputIsBoundedHeadAndTailWithoutDeadlock()
    {
        using var fixture = Wp10Fixture.Create();
        var result = fixture.RunProbe(TimeSpan.FromSeconds(20), "flood-output", "300000", "300000");
        Wp10True(result.StdoutTruncated, "300KiB stdout was not truncated.");
        Wp10True(result.StderrTruncated, "300KiB stderr was not truncated.");
        Wp10True(result.Stdout.Length <= SandboxPathPolicy.MaxCapturedOutputBytes,
            "Stdout capture exceeded 256KiB.");
        Wp10True(result.Stderr.Length <= SandboxPathPolicy.MaxCapturedOutputBytes,
            "Stderr capture exceeded 256KiB.");
        Wp10True(result.Stdout.StartsWith(new string('A', 32), StringComparison.Ordinal), "Stdout head was lost.");
        Wp10True(result.Stdout.EndsWith(new string('A', 32), StringComparison.Ordinal), "Stdout tail was lost.");
        Wp10True(result.Stderr.StartsWith(new string('B', 32), StringComparison.Ordinal), "Stderr head was lost.");
        Wp10True(result.Stderr.EndsWith(new string('B', 32), StringComparison.Ordinal), "Stderr tail was lost.");
    }

    private static void TimeoutKillsProcessTree()
    {
        using var fixture = Wp10Fixture.Create();
        var result = fixture.RunProbe(TimeSpan.FromMilliseconds(400), "spawn-child", "20000", "2");
        Wp10True(result.TimedOut, "Hanging child was not reported as Timeout.");
        Wp10Equal(SandboxError.Timeout, result.Error, "Timeout did not return the typed Timeout error.");
        if (result.ProcessId is int pid)
        {
            Wp10True(ProcessHasExited(pid), "Timed-out sandbox process is still running.");
        }
    }

    private static void KillOnCloseContainsChildren()
    {
        using var fixture = Wp10Fixture.Create();
        var request = fixture.Request(["spawn-child", "20000", "2"], TimeSpan.FromSeconds(30));
        using var live = fixture.Host.StartLive(request);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && !live.Stdout.ToUtf8String().Contains("child", StringComparison.Ordinal))
        {
            Thread.Sleep(50);
        }

        var stdout = live.Stdout.ToUtf8String();
        Wp10True(stdout.Contains("child", StringComparison.Ordinal), $"Child tree did not report PIDs: {stdout}");
        using var json = JsonDocument.Parse(stdout.Trim().Split('\n')[0]);
        var parent = json.RootElement.GetProperty("pid").GetInt32();
        var child = json.RootElement.GetProperty("child").GetInt32();
        live.Dispose();
        var exitDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < exitDeadline && (!ProcessHasExited(parent) || (child != 0 && !ProcessHasExited(child))))
        {
            Thread.Sleep(50);
        }

        Wp10True(ProcessHasExited(parent), "Kill-on-close left the worker process running.");
        Wp10True(child == 0 || ProcessHasExited(child), "Kill-on-close left a child process running.");
    }

    private static void FaultInjectionFailsClosedWithoutUnsandboxedChild()
    {
        foreach (var fault in new[]
                 {
                     SandboxFaultPoint.RestrictedTokenInit,
                     SandboxFaultPoint.AppContainerProfile,
                     SandboxFaultPoint.AppContainerAcl,
                     SandboxFaultPoint.SecurityCapabilities,
                     SandboxFaultPoint.CreateProcess,
                     SandboxFaultPoint.JobCreation,
                     SandboxFaultPoint.JobConfiguration,
                     SandboxFaultPoint.JobAssignment,
                     SandboxFaultPoint.SelfTest,
                     SandboxFaultPoint.BrokerUnavailable,
                     SandboxFaultPoint.RunSessionRevalidation,
                     SandboxFaultPoint.NetworkIsolationQuery,
                     SandboxFaultPoint.NetworkIsolationSet,
                     SandboxFaultPoint.PrivilegeScopedEnable,
                     SandboxFaultPoint.CpuJobConfiguration
                 })
        {
            var injector = new MutableSandboxFaultInjector { Fault = fault };
            using var fixture = Wp10Fixture.Create(injector);
            var marker = Path.Combine(fixture.WorkDirectory, $"marker-{fault}.txt");
            var result = fixture.Broker.ExecuteShell(fixture.Request(["write-file", marker, "unsandboxed"], TimeSpan.FromSeconds(5)));
            Wp10True(!result.Succeeded, $"{fault} did not fail closed.");
            Wp10True(!File.Exists(marker), $"{fault} started an unsandboxed child that wrote a marker.");
        }
    }

    private static void BrokerEnforcesWp09AndFinalRecheck()
    {
        using var fixture = Wp10Fixture.Create();
        var untrusted = new TrustedSandboxBroker(
            new CoreAuthorizationService(new Wp10Policy(projectTrusted: false)),
            fixture.Host,
            new WindowsSandboxPathGuard(),
            fixture.Context);
        var denied = untrusted.ExecuteShell(fixture.Request(["whoami-token"], TimeSpan.FromSeconds(5)));
        Wp10Equal(SandboxError.TrustRequired, denied.Error, "Missing Project Trust did not deny Shell.");

        var scriptOnShell = fixture.Broker.ExecuteScript(fixture.Request(Capability.ShellExecute, ["whoami-token"], TimeSpan.FromSeconds(5)));
        Wp10Equal(SandboxError.CapabilityDenied, scriptOnShell.Error, "Shell was accepted as Script.");

        var flip = new Wp10FlipPolicy();
        var flipping = new TrustedSandboxBroker(
            new CoreAuthorizationService(flip),
            fixture.Host,
            new WindowsSandboxPathGuard(),
            fixture.Context);
        var recheck = flipping.ExecuteShell(fixture.Request(["whoami-token"], TimeSpan.FromSeconds(5)));
        Wp10True(!recheck.Succeeded, "Final recheck allowed a flipped policy to launch.");
        Wp10Equal(2, flip.Calls, "Entry + final authorization were not both executed.");
    }

    private static void BypassCannotDisableSandbox()
    {
        using var fixture = Wp10Fixture.Create();
        var bypassBroker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new Wp10Policy(projectTrusted: true)),
            fixture.Host,
            new WindowsSandboxPathGuard(),
            fixture.Context);
        var result = bypassBroker.ExecuteShell(fixture.Request(["whoami-token"], TimeSpan.FromSeconds(15)));
        Wp10True(result.Succeeded, $"BYPASS sandbox launch failed: {result.Error} {result.Stderr}");
        Wp10True(result.Stdout.Contains("\"isAppContainer\":true", StringComparison.OrdinalIgnoreCase) ||
                 result.Stdout.Contains("\"isAppContainer\": true", StringComparison.OrdinalIgnoreCase),
            "BYPASS disabled AppContainer.");
        Wp10True(result.Stdout.Contains("\"hasRestrictions\":true", StringComparison.OrdinalIgnoreCase) ||
                 result.Stdout.Contains("\"hasRestrictions\": true", StringComparison.OrdinalIgnoreCase),
            "BYPASS disabled Restricted Token.");
    }

    private static void JobActiveProcessLimitIsEnforced()
    {
        using var fixture = Wp10Fixture.Create();
        var request = fixture.Request(["spawn-many", "8"], TimeSpan.FromSeconds(15)) with
        {
            Limits = new SandboxResourceLimits(SandboxResourceLimits.DefaultProcessMemoryBytes, 3, null)
        };
        var result = fixture.Broker.ExecuteShell(request);
        Wp10True(result.Succeeded, $"process-limit probe failed: {result.Error} {result.Stderr} {result.Stdout}");
        using var json = JsonDocument.Parse(result.Stdout.Trim().Split('\n')[0]);
        var started = json.RootElement.GetProperty("started").GetInt32();
        Wp10True(started < 8, $"Active process limit did not constrain spawn-many. started={started}");
        Wp10True(started <= 2, $"Active process limit 3 allowed too many children. started={started}");
    }

    private static void JobMemoryLimitKillsOversizedChild()
    {
        using var fixture = Wp10Fixture.Create();
        var request = fixture.Request(["allocate-memory", "128"], TimeSpan.FromSeconds(15)) with
        {
            Limits = new SandboxResourceLimits(32L * 1024 * 1024, SandboxResourceLimits.DefaultActiveProcessLimit, null)
        };
        var result = fixture.Broker.ExecuteShell(request);
        Wp10True(!result.Succeeded, "A 128MiB allocation succeeded under a 32MiB job memory limit.");
        Wp10True(!result.TimedOut, "Memory-limit child was reported as Timeout instead of a resource failure.");
        if (result.ProcessId is int pid)
        {
            Wp10True(ProcessHasExited(pid), "Memory-limit child is still running.");
        }
    }

    private static void PowerShellCompatibilityIsProbedWithoutDisablingSandbox()
    {
        using var fixture = Wp10Fixture.Create();
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var request = new SandboxExecutionRequest(
            SandboxLaunchBinding.Create("run-wp10", "worker-wp10", fixture.Scope),
            fixture.Principal,
            Capability.ShellExecute,
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'PS_SANDBOX_OK'"],
            fixture.ProjectRoot,
            TimeSpan.FromSeconds(20));
        var powershellResult = fixture.Broker.ExecuteShell(request);
        Console.WriteLine(
            powershellResult.Succeeded && powershellResult.Stdout.Contains("PS_SANDBOX_OK", StringComparison.Ordinal)
                ? "WP10 PowerShell compatibility: available in mandatory sandbox stack."
                : $"WP10 PowerShell compatibility: unavailable (error={powershellResult.Error} exit={powershellResult.ExitCode}). Shell/Script remains sandboxed; AppContainer was not disabled.");
        var whoami = fixture.RunProbe("whoami-token");
        Wp10True(whoami.Succeeded, "PowerShell probe disabled the sandbox stack.");
        Wp10True(whoami.Stdout.Contains("\"isAppContainer\":true", StringComparison.OrdinalIgnoreCase) ||
                 whoami.Stdout.Contains("\"isAppContainer\": true", StringComparison.OrdinalIgnoreCase),
            "PowerShell incompatibility caused an unsandboxed fallback.");
    }

    private static void ForgedProjectRootIsDenied()
    {
        using var agent = Wp10AgentHarness.Create();
        var stolenDir = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP10.stolen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stolenDir);
        var secretPath = Path.Combine(stolenDir, "secret.txt");
        File.WriteAllText(secretPath, "TOP-SECRET-BYTES");
        try
        {
            var result = agent.Broker.ReadFile(new SandboxFileReadRequest(
                agent.Principal,
                agent.Scope,
                stolenDir,
                "secret.txt",
                agent.RunId));
            Wp10True(!result.Succeeded, "Forged ProjectRoot read succeeded.");
            Wp10True(result.Bytes is null || result.Bytes.Length == 0, "Core returned stolen secret bytes.");
            Wp10True(result.Error is SandboxError.PathOutOfScope or SandboxError.SessionBindingMismatch,
                $"Forged ProjectRoot returned {result.Error}.");
        }
        finally
        {
            try { Directory.Delete(stolenDir, true); } catch (IOException) { }
        }
    }

    private static void ForgedProjectScopeIsDenied()
    {
        using var agent = Wp10AgentHarness.Create();
        var other = new ProjectScope(Guid.Parse("018f3e78-9999-7abc-8def-0123456789ab"), "other-ws");
        var result = agent.Broker.ReadFile(new SandboxFileReadRequest(
            agent.Principal,
            other,
            agent.ProjectRoot,
            ".llmw.sandbox/runs/" + agent.RunId + "/work/ok.txt",
            agent.RunId));
        Wp10True(!result.Succeeded, "Forged ProjectScope was allowed.");
        Wp10Equal(SandboxError.SessionBindingMismatch, result.Error, "Forged ProjectScope did not return SessionBindingMismatch.");
    }

    private static void ForgedRunIdIsDenied()
    {
        using var agent = Wp10AgentHarness.Create();
        var request = agent.ShellRequest(["whoami-token"]) with
        {
            Binding = SandboxLaunchBinding.Create("run-forged", agent.WorkerId, agent.Scope)
        };
        var result = agent.Broker.ExecuteShell(request);
        Wp10True(!result.Succeeded, "Forged RunId launched a child.");
        Wp10Equal(SandboxError.SessionBindingMismatch, result.Error, "Forged RunId did not return SessionBindingMismatch.");
        Wp10True(result.ProcessId is null, "Forged RunId produced a process id.");
    }

    private static void ForgedWorkerInstanceIdIsDenied()
    {
        using var agent = Wp10AgentHarness.Create();
        var request = agent.ShellRequest(["whoami-token"]) with
        {
            Binding = SandboxLaunchBinding.Create(agent.RunId, "worker-forged", agent.Scope)
        };
        var result = agent.Broker.ExecuteShell(request);
        Wp10True(!result.Succeeded, "Forged WorkerInstanceId launched a child.");
        Wp10Equal(SandboxError.SessionBindingMismatch, result.Error, "Forged WorkerInstanceId did not return SessionBindingMismatch.");
    }

    private static void RevokedSessionAfterResolutionIsDenied()
    {
        using var agent = Wp10AgentHarness.Create();
        agent.Sessions.Revoke(agent.Principal.SessionHandleId!);
        var result = agent.Broker.ExecuteShell(agent.ShellRequest(["whoami-token"]));
        Wp10True(!result.Succeeded, "Revoked session launched a child.");
        Wp10Equal(SandboxError.SessionRevoked, result.Error, "Revoked session did not return SessionRevoked.");
        Wp10True(result.ProcessId is null, "Revoked session produced a process id.");
    }

    private static void ExpiredSessionAfterResolutionIsDenied()
    {
        using var agent = Wp10AgentHarness.Create(expiresIn: TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);
        agent.Clock.UtcNow = agent.Clock.UtcNow.AddMinutes(5);
        var result = agent.Broker.ExecuteShell(agent.ShellRequest(["whoami-token"]));
        Wp10True(!result.Succeeded, "Expired session launched a child.");
        Wp10Equal(SandboxError.SessionExpired, result.Error, "Expired session did not return SessionExpired.");
        Wp10True(result.ProcessId is null, "Expired session produced a process id.");
    }

    private static void DurableRoleChangeDoesNotKeepStaleMaximum()
    {
        using var agent = Wp10AgentHarness.Create(role: "pm");
        agent.SetDurableRole("reviewer");
        var write = agent.Broker.WriteSandboxWorkFile(new SandboxFileWriteRequest(
            agent.Principal,
            agent.Scope,
            agent.ProjectRoot,
            ".llmw.sandbox/runs/" + agent.RunId + "/work/stale.txt",
            agent.RunId,
            "stale-pm"u8.ToArray()));
        Wp10True(!write.Succeeded, "Stale PM RawWrite was kept after durable role downgrade.");
        Wp10True(write.Error is SandboxError.CapabilityDenied or SandboxError.SessionBindingMismatch,
            $"Role downgrade returned {write.Error}.");
    }

    private static void ChildIsNotOnInteractiveDefaultDesktop()
    {
        using var fixture = Wp10Fixture.Create();
        var result = fixture.RunProbe("whoami-desktop");
        Wp10True(result.Succeeded, $"whoami-desktop failed: {result.Error} {result.Stderr} {result.Stdout}");
        Wp10True(!result.Stdout.Contains("winsta0\\default", StringComparison.OrdinalIgnoreCase) &&
                 !result.Stdout.Contains("WinSta0\\Default", StringComparison.OrdinalIgnoreCase),
            "Sandbox child reported winsta0\\default. stdout=" + result.Stdout);
        Wp10True(result.Stdout.Contains("llmw.sbx", StringComparison.OrdinalIgnoreCase) ||
                 result.Stdout.Contains("llmw.desk", StringComparison.OrdinalIgnoreCase),
            "Sandbox child did not report the dedicated sandbox window station/desktop. stdout=" + result.Stdout);
    }

    private static void DefaultDesktopDaclUnchangedAfterSandbox()
    {
        var winsta = NativeMethods.GetProcessWindowStation();
        var desktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
        var beforeStation = AppContainerAclManager.ReadUserObject(winsta);
        var beforeDesktop = AppContainerAclManager.ReadUserObject(desktop);
        using var fixture = Wp10Fixture.Create();
        var whoami = fixture.RunProbe("whoami-token");
        Wp10True(whoami.Succeeded, "Sandbox whoami failed while checking default desktop DACL.");
        var sid = fixture.Host.Identity!.AppContainerSid;
        var afterStation = AppContainerAclManager.ReadUserObject(winsta);
        var afterDesktop = AppContainerAclManager.ReadUserObject(desktop);
        Wp10Equal(beforeStation.ContainsSid(sid), afterStation.ContainsSid(sid),
            "Sandbox added the AppContainer SID to the interactive window station DACL.");
        Wp10Equal(beforeDesktop.ContainsSid(sid), afterDesktop.ContainsSid(sid),
            "Sandbox added the AppContainer SID to the interactive default desktop DACL.");
    }

    private static void NonSystemExecutableSiblingFilesAreDenied()
    {
        using var fixture = Wp10Fixture.Create();
        var toolDir = Path.Combine(fixture.Directory, "tool-dir");
        Directory.CreateDirectory(Path.Combine(toolDir, "nested"));
        var probeDir = Path.GetDirectoryName(fixture.Probe)!;
        foreach (var file in Directory.EnumerateFiles(probeDir))
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".config", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, Path.Combine(toolDir, Path.GetFileName(file)), overwrite: true);
            }
        }

        var probeName = Path.GetFileName(fixture.Probe);
        var toolProbe = Path.Combine(toolDir, probeName);
        File.WriteAllText(Path.Combine(toolDir, "sibling-secret.txt"), "SIBLING-SECRET");
        File.WriteAllText(Path.Combine(toolDir, "nested", "private.txt"), "NESTED-SECRET");
        var sibling = fixture.Broker.ExecuteShell(fixture.Request(["read-file", Path.Combine(toolDir, "sibling-secret.txt")], TimeSpan.FromSeconds(15)) with
        {
            ExecutablePath = toolProbe
        });
        Wp10True(sibling.Stdout.Contains("READ_DENIED", StringComparison.Ordinal),
            "Worker read sibling-secret.txt next to the executable. stdout=" + sibling.Stdout);
        var nested = fixture.Broker.ExecuteShell(fixture.Request(["read-file", Path.Combine(toolDir, "nested", "private.txt")], TimeSpan.FromSeconds(15)) with
        {
            ExecutablePath = toolProbe
        });
        Wp10True(nested.Stdout.Contains("READ_DENIED", StringComparison.Ordinal),
            "Worker read nested/private.txt. stdout=" + nested.Stdout);
        var snapshot = AppContainerAclManager.Read(toolDir);
        Wp10True(!snapshot.ContainsSid(fixture.Host.Identity!.AppContainerSid) ||
                 !snapshot.Grants(fixture.Host.Identity.AppContainerSid, NativeConstants.FILE_READ_DATA),
            "AppContainer SID has recursive read access to the original tool directory.");
    }

    private static void SiblingRunWorkIsDeniedAtOs()
    {
        using var fixture = Wp10Fixture.Create();
        var runB = SandboxPathPolicy.RunWorkDirectory(fixture.ProjectRoot, "run-b");
        Directory.CreateDirectory(runB);
        var secretB = Path.Combine(runB, "secret-B.txt");
        File.WriteAllText(secretB, "SECRET-B");
        var requestB = fixture.Request(["write-file", Path.Combine(runB, "touch-b.txt"), "b"], TimeSpan.FromSeconds(15)) with
        {
            Binding = SandboxLaunchBinding.Create("run-b", "worker-wp10", fixture.Scope)
        };
        _ = fixture.Broker.ExecuteShell(requestB);
        var requestA = fixture.Request(["read-file", secretB], TimeSpan.FromSeconds(15));
        var result = fixture.Broker.ExecuteShell(requestA);
        Wp10True(result.Stdout.Contains("READ_DENIED", StringComparison.Ordinal),
            "Run A read Run B work. stdout=" + result.Stdout + " error=" + result.Error);
    }

    private static void CorePrivilegesUnchangedAfterSuccessfulAndFaultedLaunch()
    {
        var before = CoreProcessPrivilegeSnapshot.Capture();
        using (var fixture = Wp10Fixture.Create())
        {
            for (var i = 0; i < 100; i++)
            {
                var result = fixture.RunProbe("whoami-token");
                Wp10True(result.Succeeded, $"Privilege drift launch {i} failed: {result.Error}");
            }
        }

        var afterSuccess = CoreProcessPrivilegeSnapshot.Capture();
        Wp10Equal(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeIncreaseQuotaPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(afterSuccess, "SeIncreaseQuotaPrivilege"),
            "SeIncreaseQuotaPrivilege drifted after 100 launches.");
        Wp10Equal(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeAssignPrimaryTokenPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(afterSuccess, "SeAssignPrimaryTokenPrivilege"),
            "SeAssignPrimaryTokenPrivilege drifted after 100 launches.");

        using (var failed = Wp10Fixture.Create(new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.CreateProcess }))
        {
            var result = failed.Broker.ExecuteShell(failed.Request(["whoami-token"], TimeSpan.FromSeconds(5)));
            Wp10True(!result.Succeeded, "Faulted launch succeeded.");
        }

        var afterFault = CoreProcessPrivilegeSnapshot.Capture();
        Wp10Equal(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeIncreaseQuotaPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(afterFault, "SeIncreaseQuotaPrivilege"),
            "SeIncreaseQuotaPrivilege drifted after a faulted launch.");
        Wp10Equal(
            CoreProcessPrivilegeSnapshot.Attribute(before, "SeAssignPrimaryTokenPrivilege"),
            CoreProcessPrivilegeSnapshot.Attribute(afterFault, "SeAssignPrimaryTokenPrivilege"),
            "SeAssignPrimaryTokenPrivilege drifted after a faulted launch.");
    }

    private static void ExtraEnvironmentOverridesAreDenied()
    {
        using var fixture = Wp10Fixture.Create();
        foreach (var name in new[] { "PATH", "USERPROFILE", "DOTNET_STARTUP_HOOKS" })
        {
            var request = fixture.Request(["print-env-has", name], TimeSpan.FromSeconds(10)) with
            {
                ExtraEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = "attacker" }
            };
            var result = fixture.Broker.ExecuteShell(request);
            Wp10True(!result.Succeeded, name + " ExtraEnvironment override launched a child.");
            Wp10Equal(SandboxError.EnvironmentRejected, result.Error, name + " ExtraEnvironment was not rejected.");
            Wp10True(result.ProcessId is null, name + " ExtraEnvironment produced a process id.");
        }
    }

    private static void RequestedCpuPolicyFailureFailsClosed()
    {
        using var fixture = Wp10Fixture.Create(new MutableSandboxFaultInjector { Fault = SandboxFaultPoint.CpuJobConfiguration });
        var request = fixture.Request(["whoami-token"], TimeSpan.FromSeconds(5)) with
        {
            Limits = new SandboxResourceLimits(32L * 1024 * 1024, 4, CpuRateHundredthsPercent: 1000)
        };
        var result = fixture.Broker.ExecuteShell(request);
        Wp10True(!result.Succeeded, "CPU job configuration failure launched a child.");
        Wp10True(result.Error is SandboxError.JobConfigurationFailed or SandboxError.SandboxUnavailable or SandboxError.SandboxSelfTestFailed,
            $"CPU failure returned {result.Error}.");
        Wp10True(result.ProcessId is null, "CPU failure produced a process id.");
    }

    private static void AgentRunGenericProjectFileReadDeniesInternalSandbox()
    {
        using var agent = Wp10AgentHarness.Create();
        File.WriteAllText(Path.Combine(agent.ProjectRoot, "notes.txt"), "project-ok");
        var runB = SandboxPathPolicy.RunWorkDirectory(agent.ProjectRoot, "run-B");
        Directory.CreateDirectory(runB);
        var secretB = Path.Combine(runB, "secret-B.txt");
        File.WriteAllText(secretB, "SECRET-B");
        var tools = Path.Combine(agent.ProjectRoot, ".llmw.sandbox", "tools", "known-path");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "file"), "TOOL-SECRET");

        foreach (var logical in new[]
                 {
                     ".llmw.sandbox/runs/run-B/work/secret-B.txt",
                     ".LLMW.SANDBOX/runs/run-B/work/secret-B.txt",
                     @".llmw.sandbox\runs\run-B\work\secret-B.txt",
                     ".llmw.sandbox/tools/known-path/file"
                 })
        {
            var denied = agent.Broker.ReadFile(new SandboxFileReadRequest(
                agent.Principal,
                agent.Scope,
                agent.ProjectRoot,
                logical,
                agent.RunId));
            Wp10True(!denied.Succeeded, "AgentRun generic read of " + logical + " succeeded.");
            Wp10Equal(SandboxError.PathOutOfScope, denied.Error, "AgentRun generic read of " + logical + " returned " + denied.Error + ".");
            Wp10True(denied.Bytes is null || denied.Bytes.Length == 0, "AgentRun generic read of " + logical + " returned bytes.");
        }

        Wp10Equal("SECRET-B", File.ReadAllText(secretB), "Denied sandbox read mutated Run B bytes.");
        Wp10Equal("TOOL-SECRET", File.ReadAllText(Path.Combine(tools, "file")), "Denied sandbox read mutated tools bytes.");

        var allowed = agent.Broker.ReadFile(new SandboxFileReadRequest(
            agent.Principal,
            agent.Scope,
            agent.ProjectRoot,
            "notes.txt",
            agent.RunId));
        Wp10True(allowed.Succeeded, "Legitimate project file read was denied. error=" + allowed.Error);
        Wp10Equal("project-ok", System.Text.Encoding.UTF8.GetString(allowed.Bytes ?? []), "Legitimate project file bytes did not round-trip.");
    }

    private static void StagedToolOmitsUnrelatedSourceFilesFromChild()
    {
        using var fixture = Wp10Fixture.Create();
        var toolDir = Path.Combine(fixture.Directory, "tool-dir");
        Directory.CreateDirectory(toolDir);
        var probeDir = Path.GetDirectoryName(fixture.Probe)!;
        foreach (var file in Directory.EnumerateFiles(probeDir))
        {
            File.Copy(file, Path.Combine(toolDir, Path.GetFileName(file)), overwrite: true);
        }

        File.WriteAllText(Path.Combine(toolDir, "credentials.json"), "CREDENTIALS");
        File.WriteAllText(Path.Combine(toolDir, "secret.config"), "SECRET-CONFIG");
        File.WriteAllBytes(Path.Combine(toolDir, "unrelated.dll"), [1, 2, 3]);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(toolDir, "other.exe"), overwrite: true);
        var toolProbe = Path.Combine(toolDir, Path.GetFileName(fixture.Probe));
        var result = fixture.Broker.ExecuteShell(fixture.Request(["whoami-token"], TimeSpan.FromSeconds(15)) with
        {
            ExecutablePath = toolProbe
        });
        Wp10True(result.Succeeded, "Staged probe failed: " + result.Error + " " + result.DenyReason + " " + result.Stderr);
        var toolsRoot = Path.Combine(fixture.ProjectRoot, ".llmw.sandbox", "tools");
        Wp10True(Directory.Exists(toolsRoot), "Tool staging directory was not created.");
        foreach (var staged in Directory.GetDirectories(toolsRoot))
        {
            Wp10True(!File.Exists(Path.Combine(staged, "credentials.json")), "credentials.json was staged into " + staged);
            Wp10True(!File.Exists(Path.Combine(staged, "secret.config")), "secret.config was staged into " + staged);
            Wp10True(!File.Exists(Path.Combine(staged, "unrelated.dll")), "unrelated.dll was staged into " + staged);
            Wp10True(!File.Exists(Path.Combine(staged, "other.exe")), "other.exe was staged into " + staged);
        }

        var credentials = fixture.Broker.ExecuteShell(fixture.Request(["read-file", Path.Combine(toolDir, "credentials.json")], TimeSpan.FromSeconds(15)) with
        {
            ExecutablePath = toolProbe
        });
        Wp10True(credentials.Stdout.Contains("READ_DENIED", StringComparison.Ordinal),
            "Sandbox child read undeclared credentials.json. stdout=" + credentials.Stdout);
        foreach (var staged in Directory.GetDirectories(toolsRoot))
        {
            var planted = Path.Combine(staged, "credentials.json");
            var fromStaging = fixture.Broker.ExecuteShell(fixture.Request(["read-file", planted], TimeSpan.FromSeconds(15)) with
            {
                ExecutablePath = toolProbe
            });
            Wp10True(fromStaging.Stdout.Contains("READ_DENIED", StringComparison.Ordinal) || !File.Exists(planted),
                "Sandbox child read credentials.json from staging. stdout=" + fromStaging.Stdout);
        }
    }

    private static bool ProcessHasExited(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Wp10True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Wp10Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class Wp10MutableClock(DateTimeOffset utcNow) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class Wp10FixedPermission(RuntimePermissionMode mode) : IRunSecurityPolicySource
    {
        public RuntimePermissionMode GetRuntimePermissionMode(string runId) => mode;
    }

    private sealed class Wp10AgentHarness : IDisposable
    {
        private Wp10AgentHarness(
            string directory,
            string databasePath,
            WindowsSandboxHost host,
            TrustedSandboxBroker broker,
            CallerPrincipal principal,
            ProjectScope scope,
            RunSessionService sessions,
            SqliteRunSessionStore store,
            Wp10MutableClock clock,
            string runId,
            string workerId,
            string probe)
        {
            Directory = directory;
            DatabasePath = databasePath;
            Host = host;
            Broker = broker;
            Principal = principal;
            Scope = scope;
            Sessions = sessions;
            Store = store;
            Clock = clock;
            RunId = runId;
            WorkerId = workerId;
            Probe = probe;
        }

        public string Directory { get; }
        public string DatabasePath { get; }
        public WindowsSandboxHost Host { get; }
        public TrustedSandboxBroker Broker { get; }
        public CallerPrincipal Principal { get; }
        public ProjectScope Scope { get; }
        public RunSessionService Sessions { get; }
        public SqliteRunSessionStore Store { get; }
        public Wp10MutableClock Clock { get; }
        public string RunId { get; }
        public string WorkerId { get; }
        public string Probe { get; }
        public string ProjectRoot => Path.Combine(Directory, "project");

        public static Wp10AgentHarness Create(string role = "pm", TimeSpan? expiresIn = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP10.A", Guid.NewGuid().ToString("N"));
            var project = Path.Combine(directory, "project");
            System.IO.Directory.CreateDirectory(Path.Combine(project, ".llmw"));
            var databasePath = Path.Combine(project, ".llmw", "project.db");
            new SqliteMigrationRunner().Migrate(databasePath, "wp10-agent");
            SeedRun(databasePath, "run-agent", role);
            var scope = new ProjectScope(Guid.Parse("018f3e78-cc10-7abc-8def-0123456789ab"), "wp10-agent");
            var context = new SandboxProjectContext(project, scope);
            var probe = Wp10FixtureFindProbe();
            var host = new WindowsSandboxHost(project, scope, probe);
            var clock = new Wp10MutableClock(DateTimeOffset.UtcNow);
            var store = new SqliteRunSessionStore(databasePath);
            var sessions = new RunSessionService(store, clock, new Wp10FixedPermission(RuntimePermissionMode.AutoApproveScoped));
            var channel = new AuthenticatedChannelContext(
                "channel-agent",
                AuthenticatedClientKind.AgentRuntime,
                "worker-agent",
                scope);
            var issued = sessions.Create(new CreateRunSessionRequest(
                "run-agent",
                channel,
                clock.UtcNow.Add(expiresIn ?? TimeSpan.FromMinutes(5))));
            if (!issued.Succeeded || issued.Value is null)
            {
                throw new InvalidOperationException("Agent harness session issuance failed: " + issued.Failure?.Code);
            }

            var resolved = sessions.Resolve(new ResolveRunSessionRequest(
                "run-agent",
                issued.Value.Token.ExportOnceForAuthenticatedTransport(),
                channel));
            if (!resolved.Succeeded || resolved.Value is null)
            {
                throw new InvalidOperationException("Agent harness session resolve failed: " + resolved.Failure?.Code);
            }

            var revalidator = new RunSessionRevalidator(store, clock, new Wp10FixedPermission(RuntimePermissionMode.AutoApproveScoped));
            var broker = new TrustedSandboxBroker(
                new CoreAuthorizationService(new Wp10Policy(projectTrusted: true)),
                host,
                new WindowsSandboxPathGuard(),
                context,
                sessionRevalidator: revalidator);
            System.IO.Directory.CreateDirectory(SandboxPathPolicy.RunWorkDirectory(project, "run-agent"));
            File.WriteAllText(Path.Combine(SandboxPathPolicy.RunWorkDirectory(project, "run-agent"), "ok.txt"), "ok");
            return new Wp10AgentHarness(
                directory,
                databasePath,
                host,
                broker,
                resolved.Value,
                scope,
                sessions,
                store,
                clock,
                "run-agent",
                "worker-agent",
                probe);
        }

        public SandboxExecutionRequest ShellRequest(IReadOnlyList<string> arguments) =>
            new(
                SandboxLaunchBinding.Create(RunId, WorkerId, Scope),
                Principal,
                Capability.ShellExecute,
                Probe,
                arguments,
                ProjectRoot,
                TimeSpan.FromSeconds(15));

        public void SetDurableRole(string role)
        {
            using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET role=$role WHERE run_id=$run_id;";
            var roleParam = command.CreateParameter();
            roleParam.ParameterName = "$role";
            roleParam.Value = role;
            command.Parameters.Add(roleParam);
            var runParam = command.CreateParameter();
            runParam.ParameterName = "$run_id";
            runParam.Value = RunId;
            command.Parameters.Add(runParam);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void SeedRun(string databasePath, string runId, string role)
        {
            using var connection = new SqliteDatabaseConnectionFactory().OpenConfigured(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO workflow_runs(workflow_run_id,status,created_at_ms,updated_at_ms)
                VALUES ('wp10-workflow','running',1,1);
                INSERT INTO runs(run_id,workflow_run_id,role,status,depth,created_at_ms,updated_at_ms)
                VALUES ($run_id,'wp10-workflow',$role,'running',0,1,1);
                """;
            var runParam = command.CreateParameter();
            runParam.ParameterName = "$run_id";
            runParam.Value = runId;
            command.Parameters.Add(runParam);
            var roleParam = command.CreateParameter();
            roleParam.ParameterName = "$role";
            roleParam.Value = role;
            command.Parameters.Add(roleParam);
            command.ExecuteNonQuery();
        }

        private static string Wp10FixtureFindProbe()
        {
            var names = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "sandbox-probe", "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(AppContext.BaseDirectory, "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(Environment.CurrentDirectory, "tests", "LLMW.Writing.SandboxProbe", "bin", "Release", "net8.0", "win-x64", "LLMW.Writing.SandboxProbe.exe")
            };
            return names.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Build LLMW.Writing.SandboxProbe before running WP10 tests.");
        }
    }

    private sealed class Wp10Policy(bool projectTrusted) : ISecurityPolicySource
    {
        public SecurityPolicySnapshot? Resolve(CallerPrincipal principal, Capability capability) =>
            new(true, true, true, projectTrusted, SecurityScopeClassification.InScope, HardDeny.None, false, false);
    }

    private sealed class Wp10FlipPolicy : ISecurityPolicySource
    {
        public int Calls { get; private set; }

        public SecurityPolicySnapshot? Resolve(CallerPrincipal principal, Capability capability)
        {
            Calls++;
            return new(Calls == 1, true, true, true, SecurityScopeClassification.InScope, HardDeny.None, false, false);
        }
    }

    private sealed class Wp10Fixture : IDisposable
    {
        private Wp10Fixture(
            string directory,
            WindowsSandboxHost host,
            TrustedSandboxBroker broker,
            CallerPrincipal principal,
            ProjectScope scope,
            SandboxProjectContext context,
            string probe)
        {
            Directory = directory;
            Host = host;
            Broker = broker;
            Principal = principal;
            Scope = scope;
            Context = context;
            Probe = probe;
        }

        public string Directory { get; }
        public WindowsSandboxHost Host { get; }
        public TrustedSandboxBroker Broker { get; }
        public CallerPrincipal Principal { get; }
        public ProjectScope Scope { get; }
        public SandboxProjectContext Context { get; }
        public string Probe { get; }
        public string ProjectRoot => Path.Combine(Directory, "project");
        public string WorkDirectory => SandboxPathPolicy.RunWorkDirectory(ProjectRoot, "run-wp10");
        public string LlmwDbPath => Path.Combine(ProjectRoot, ".llmw", "project.db");
        public string OutsideFile => Path.Combine(Directory, "outside", "file.txt");

        public static Wp10Fixture Create(ISandboxFaultInjector? injector = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "LLMW.Writing.WP10.I", Guid.NewGuid().ToString("N"));
            var project = Path.Combine(directory, "project");
            System.IO.Directory.CreateDirectory(Path.Combine(project, ".llmw"));
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "outside"));
            File.WriteAllText(Path.Combine(project, ".llmw", "project.db"), "authority");
            File.WriteAllText(Path.Combine(directory, "outside", "file.txt"), "outside-original");
            var scope = new ProjectScope(Guid.Parse("018f3e78-cc10-7abc-8def-0123456789ab"), "wp10-tests");
            var context = new SandboxProjectContext(project, scope);
            var probe = FindProbe();
            var host = new WindowsSandboxHost(project, scope, probe, injector ?? NoSandboxFaultInjector.Instance);
            var principal = new TrustedNativePrincipalSource("wp10-integration").ResolveUserInteractive();
            var broker = new TrustedSandboxBroker(
                new CoreAuthorizationService(new Wp10Policy(projectTrusted: true)),
                host,
                new WindowsSandboxPathGuard(),
                context,
                faultInjector: injector);
            var fixture = new Wp10Fixture(directory, host, broker, principal, scope, context, probe);
            System.IO.Directory.CreateDirectory(fixture.WorkDirectory);
            return fixture;
        }

        public SandboxExecutionRequest Request(IReadOnlyList<string> arguments, TimeSpan timeout) =>
            Request(Capability.ShellExecute, arguments, timeout);

        public SandboxExecutionRequest Request(Capability capability, IReadOnlyList<string> arguments, TimeSpan timeout) =>
            new(
                SandboxLaunchBinding.Create("run-wp10", "worker-wp10", Scope),
                Principal,
                capability,
                Probe,
                arguments,
                ProjectRoot,
                timeout);

        public SandboxExecutionResult RunProbe(params string[] arguments) =>
            RunProbe(TimeSpan.FromSeconds(20), arguments);

        public SandboxExecutionResult RunProbe(TimeSpan timeout, params string[] arguments) =>
            Broker.ExecuteShell(Request(arguments, timeout));

        public void Dispose()
        {
            try
            {
                DeleteTree(Directory);
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
            if (!System.IO.Directory.Exists(path))
            {
                return;
            }

            foreach (var child in System.IO.Directory.GetDirectories(path))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    System.IO.Directory.Delete(child);
                }
                else
                {
                    DeleteTree(child);
                }
            }

            foreach (var file in System.IO.Directory.GetFiles(path))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            System.IO.Directory.Delete(path);
        }

        private static string FindProbe()
        {
            var names = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "sandbox-probe", "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(AppContext.BaseDirectory, "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(Environment.CurrentDirectory, "tests", "LLMW.Writing.SandboxProbe", "bin", "Release", "net8.0", "win-x64", "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(Environment.CurrentDirectory, "tests", "LLMW.Writing.SandboxProbe", "bin", "Debug", "net8.0", "win-x64", "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(Environment.CurrentDirectory, "tests", "LLMW.Writing.SandboxProbe", "bin", "Release", "net8.0", "LLMW.Writing.SandboxProbe.exe"),
                Path.Combine(Environment.CurrentDirectory, "tests", "LLMW.Writing.SandboxProbe", "bin", "Debug", "net8.0", "LLMW.Writing.SandboxProbe.exe")
            };
            return names.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Build LLMW.Writing.SandboxProbe before running WP10 tests.", names[1]);
        }
    }
}
