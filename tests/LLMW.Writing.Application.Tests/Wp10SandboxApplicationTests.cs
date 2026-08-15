using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Tests;

internal static class Wp10SandboxApplicationTests
{
    private static readonly CallerPrincipal UserPrincipal =
        new TrustedNativePrincipalSource("wp10-application-tests").ResolveUserInteractive();

    public static int Run()
    {
        WindowsCommandLineQuotesSpacesQuotesBackslashEmptyAndUnicode();
        ExactCommandFingerprintDoesNotGeneralizePowerShell();
        EnvironmentPolicyStripsSecretsAndDoesNotInheritParentSecret();
        PathPolicyUsesProjectUuidAndKeepsWorkOutsideLlmw();
        BrokerDeniesWhenTrustMissingAndDoesNotLaunch();
        BrokerRecheckPreventsLaunchAfterPolicyFlip();
        BrokerKeepsShellAndScriptDistinct();
        BypassStillRequiresSandboxHost();
        Console.WriteLine("Application WP10 sandbox tests passed (8).");
        return 8;
    }

    private static void WindowsCommandLineQuotesSpacesQuotesBackslashEmptyAndUnicode()
    {
        var command = WindowsCommandLine.Build(
            @"C:\Program Files\probe.exe",
            ["a b", "", "quote\"here", @"c:\temp\\", "中文", "tab\tchar"]);
        AssertTrue(command.StartsWith("\"C:\\Program Files\\probe.exe\"", StringComparison.Ordinal), "Executable with spaces was not quoted.");
        AssertTrue(command.Contains("\"a b\"", StringComparison.Ordinal), "Argument with spaces was not quoted.");
        AssertTrue(command.Contains("\"\"", StringComparison.Ordinal), "Empty argument was not quoted as an empty argv token.");
        AssertTrue(command.Contains("\\\"", StringComparison.Ordinal), "Embedded quote was not escaped.");
        AssertTrue(command.Contains("中文", StringComparison.Ordinal), "Unicode argument was dropped.");
        AssertFalse(command.Contains("Program Files\\probe.exe a b", StringComparison.Ordinal),
            "Command line used naive space joining.");
    }

    private static void ExactCommandFingerprintDoesNotGeneralizePowerShell()
    {
        var first = ExactCommand.Fingerprint(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ["-Command", "Get-Date"]);
        var second = ExactCommand.Fingerprint(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ["-Command", "Get-Process"]);
        AssertFalse(StringComparer.Ordinal.Equals(first.Digest, second.Digest),
            "Distinct PowerShell argument lists shared an approval fingerprint.");
    }

    private static void EnvironmentPolicyStripsSecretsAndDoesNotInheritParentSecret()
    {
        var sanitized = SandboxEnvironmentPolicy.Sanitize(
            new Dictionary<string, string?>
            {
                ["PATH"] = @"C:\stolen",
                ["LLMW_TEST_SECRET"] = "top-secret",
                ["PARENT_SECRET"] = "top-secret",
                ["LLMW_CORE_BOOTSTRAP_TOKEN"] = "bootstrap",
                ["NUMBER_OF_PROCESSORS"] = "8",
                ["TEMP"] = @"C:\Users\someone\AppData\Local\Temp"
            },
            @"C:\proj\.llmw.sandbox\runs\r1\work",
            @"C:\Windows",
            @"C:\Windows\System32");
        AssertFalse(sanitized.ContainsKey("LLMW_TEST_SECRET"), "Test secret leaked into the child environment policy.");
        AssertFalse(sanitized.ContainsKey("PARENT_SECRET"), "PARENT_SECRET leaked into the child environment policy.");
        AssertFalse(sanitized.ContainsKey("LLMW_CORE_BOOTSTRAP_TOKEN"), "Bootstrap token leaked into the child environment.");
        AssertEqual(@"C:\proj\.llmw.sandbox\runs\r1\work", sanitized["TEMP"], "TEMP was not redirected to the sandbox work directory.");
        AssertEqual(@"C:\Windows\System32", sanitized["PATH"], "PATH was inherited from the parent instead of System32.");
        AssertEqual("8", sanitized["NUMBER_OF_PROCESSORS"], "A safe OS variable was stripped.");
    }

    private static void PathPolicyUsesProjectUuidAndKeepsWorkOutsideLlmw()
    {
        var projectA = Guid.Parse("018f3e78-aaaa-7abc-8def-0123456789ab");
        var projectB = Guid.Parse("018f3e78-bbbb-7abc-8def-0123456789ab");
        AssertFalse(StringComparer.Ordinal.Equals(SandboxPathPolicy.AppContainerName(projectA), SandboxPathPolicy.AppContainerName(projectB)),
            "Different projects shared an AppContainer name.");
        AssertTrue(SandboxPathPolicy.AppContainerName(projectA).StartsWith("llmw.w.", StringComparison.Ordinal),
            "AppContainer name is not derived from the Project UUID prefix.");
        var root = @"C:\writing\project";
        var work = SandboxPathPolicy.RunWorkDirectory(root, "run-1");
        AssertTrue(work.Contains(".llmw.sandbox", StringComparison.OrdinalIgnoreCase), "Work directory is not the private sandbox surface.");
        AssertFalse(work.Contains(@"\.llmw\", StringComparison.OrdinalIgnoreCase), "Work directory was placed inside .llmw.");
        AssertTrue(SandboxPathPolicy.IsDesignatedWorkRelative(".llmw.sandbox/runs/run-1/work/out.txt", "run-1"),
            "Designated work relative path was rejected.");
        AssertTrue(SandboxPathPolicy.IsAuthorityTree(".llmw/project.db"), ".llmw internals were not classified as authority.");
    }

    private static void BrokerDeniesWhenTrustMissingAndDoesNotLaunch()
    {
        var host = new CountingHost();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: false)),
            host,
            new CountingPathGuard());
        var result = broker.ExecuteShell(Request(Capability.ShellExecute));
        AssertEqual(SandboxError.TrustRequired, result.Error, "Missing trust did not fail closed.");
        AssertEqual(0, host.Launches, "A trust denial launched a child.");
    }

    private static void BrokerRecheckPreventsLaunchAfterPolicyFlip()
    {
        var host = new CountingHost();
        var policy = new FlipPolicy();
        var broker = new TrustedSandboxBroker(new CoreAuthorizationService(policy), host, new CountingPathGuard());
        var result = broker.ExecuteShell(Request(Capability.ShellExecute));
        AssertEqual(SandboxError.CapabilityDenied, result.Error, "Final recheck did not deny after the policy flipped.");
        AssertEqual(0, host.Launches, "A flipped policy still started a child.");
        AssertEqual(2, policy.Calls, "The broker did not perform entry + final authorization.");
    }

    private static void BrokerKeepsShellAndScriptDistinct()
    {
        var host = new CountingHost();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            host,
            new CountingPathGuard());
        var shellAsScript = broker.ExecuteScript(Request(Capability.ShellExecute));
        AssertEqual(SandboxError.CapabilityDenied, shellAsScript.Error, "Shell request was accepted as Script.");
        AssertEqual(0, host.Launches, "Mismatched Shell/Script launched a process.");
        var script = broker.ExecuteScript(Request(Capability.ScriptExecute));
        AssertEqual(1, host.Launches, "A legitimate Script request was not launched.");
        AssertTrue(script.Succeeded, "Fake host did not run the Script request.");
    }

    private static void BypassStillRequiresSandboxHost()
    {
        var host = new UnavailableSandboxHost(SandboxError.SandboxUnavailable);
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            host,
            new CountingPathGuard());
        var result = broker.ExecuteShell(Request(Capability.ShellExecute));
        AssertEqual(SandboxError.SandboxUnavailable, result.Error, "BYPASS launched without a sandbox host.");
    }

    private static SandboxExecutionRequest Request(Capability capability) =>
        new(
            SandboxLaunchBinding.Create("run-wp10", "worker-wp10", new ProjectScope(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), "ws")),
            UserPrincipal,
            capability,
            @"C:\Windows\System32\notepad.exe",
            ["ok"],
            @"C:\proj",
            TimeSpan.FromSeconds(1));

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertFalse(bool condition, string message) => AssertTrue(!condition, message);

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
        }
    }

    private sealed class CountingHost : ISandboxHost
    {
        public int Launches { get; private set; }

        public SandboxAvailability Availability => SandboxAvailability.Available;

        public SandboxIdentity? Identity => new("llmw.w.test", "S-1-15-2-1");

        public SandboxExecutionResult Execute(SandboxExecutionRequest request)
        {
            Launches++;
            return new SandboxExecutionResult(
                true, null, 0, "", "", false, false, false,
                request.Binding.RunId, request.Binding.WorkerInstanceId, Identity?.AppContainerSid,
                request.ExecutablePath, request.Capability, null, 1);
        }
    }

    private sealed class CountingPathGuard : ISandboxPathGuard
    {
        public SandboxError? TryOpenRead(string projectRoot, string runId, string logicalRelativePath, out byte[] bytes)
        {
            bytes = "ok"u8.ToArray();
            return null;
        }

        public SandboxError? TryOpenWrite(string projectRoot, string runId, string logicalRelativePath, ReadOnlySpan<byte> contents) => null;
    }

    private sealed class StaticPolicy(bool projectTrusted) : ISecurityPolicySource
    {
        public SecurityPolicySnapshot? Resolve(CallerPrincipal principal, Capability capability) =>
            new(true, true, true, projectTrusted, SecurityScopeClassification.InScope, HardDeny.None, false, false);
    }

    private sealed class FlipPolicy : ISecurityPolicySource
    {
        public int Calls { get; private set; }

        public SecurityPolicySnapshot? Resolve(CallerPrincipal principal, Capability capability)
        {
            Calls++;
            return new(
                ProductAllowed: Calls == 1,
                ToolGranted: true,
                ExtensionGranted: true,
                ProjectTrusted: true,
                SecurityScopeClassification.InScope,
                HardDeny.None,
                false,
                false);
        }
    }
}
