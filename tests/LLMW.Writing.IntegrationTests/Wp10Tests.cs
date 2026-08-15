using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Domain.Security;
using LLMW.Writing.Infrastructure.Sandbox;

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
        Console.WriteLine("WP10 sandbox integration tests passed (14).");
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
                     SandboxFaultPoint.BrokerUnavailable
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
            new WindowsSandboxPathGuard());
        var denied = untrusted.ExecuteShell(fixture.Request(["whoami-token"], TimeSpan.FromSeconds(5)));
        Wp10Equal(SandboxError.TrustRequired, denied.Error, "Missing Project Trust did not deny Shell.");

        var scriptOnShell = fixture.Broker.ExecuteScript(fixture.Request(Capability.ShellExecute, ["whoami-token"], TimeSpan.FromSeconds(5)));
        Wp10Equal(SandboxError.CapabilityDenied, scriptOnShell.Error, "Shell was accepted as Script.");

        var flip = new Wp10FlipPolicy();
        var flipping = new TrustedSandboxBroker(
            new CoreAuthorizationService(flip),
            fixture.Host,
            new WindowsSandboxPathGuard());
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
            new WindowsSandboxPathGuard());
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
            string probe)
        {
            Directory = directory;
            Host = host;
            Broker = broker;
            Principal = principal;
            Scope = scope;
            Probe = probe;
        }

        public string Directory { get; }
        public WindowsSandboxHost Host { get; }
        public TrustedSandboxBroker Broker { get; }
        public CallerPrincipal Principal { get; }
        public ProjectScope Scope { get; }
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
            var probe = FindProbe();
            var host = new WindowsSandboxHost(project, scope, probe, injector ?? NoSandboxFaultInjector.Instance);
            var principal = new TrustedNativePrincipalSource("wp10-integration").ResolveUserInteractive();
            var broker = new TrustedSandboxBroker(
                new CoreAuthorizationService(new Wp10Policy(projectTrusted: true)),
                host,
                new WindowsSandboxPathGuard(),
                faultInjector: injector);
            var fixture = new Wp10Fixture(directory, host, broker, principal, scope, probe);
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
