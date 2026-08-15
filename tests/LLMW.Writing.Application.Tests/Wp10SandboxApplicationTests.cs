using LLMW.Writing.Application.Security;
using LLMW.Writing.Application.Security.Sandbox;
using LLMW.Writing.Domain.Security;

namespace LLMW.Writing.Application.Tests;

internal static class Wp10SandboxApplicationTests
{
    private static readonly ProjectScope Scope = new(Guid.Parse("018f3e78-1234-7abc-8def-0123456789ab"), "ws");
    private static readonly SandboxProjectContext Context = new(@"C:\proj", Scope);
    private static readonly CallerPrincipal UserPrincipal =
        new TrustedNativePrincipalSource("wp10-application-tests").ResolveUserInteractive();

    public static int Run()
    {
        WindowsCommandLineQuotesSpacesQuotesBackslashEmptyAndUnicode();
        ExactCommandFingerprintDoesNotGeneralizePowerShell();
        EnvironmentPolicyStripsSecretsAndDoesNotInheritParentSecret();
        ExtraEnvironmentOverridesAreRejected();
        PathPolicyUsesProjectUuidAndKeepsWorkOutsideLlmw();
        BrokerDeniesWhenTrustMissingAndDoesNotLaunch();
        BrokerRecheckPreventsLaunchAfterPolicyFlip();
        BrokerKeepsShellAndScriptDistinct();
        BypassStillRequiresSandboxHost();
        BrokerRejectsForgedProjectRootWithoutReading();
        BrokerRejectsForgedProjectScope();
        BrokerRequiresSessionRevalidationForAgentRun();
        BrokerDeniesGenericReadOfInternalSandboxTreeWithoutOpening();
        BrokerAllowsOrdinaryProjectRead();
        Console.WriteLine("Application WP10 sandbox tests passed (14).");
        return 14;
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

    private static void ExtraEnvironmentOverridesAreRejected()
    {
        foreach (var name in new[] { "PATH", "USERPROFILE", "LOCALAPPDATA", "DOTNET_STARTUP_HOOKS", "COMPlus_EnableDiagnostics" })
        {
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = "attacker" };
            AssertEqual(SandboxError.EnvironmentRejected, SandboxEnvironmentPolicy.ValidateExtraEnvironment(extra),
                name + " ExtraEnvironment override was not rejected.");
        }

        AssertTrue(SandboxEnvironmentPolicy.ValidateExtraEnvironment(null) is null, "Null ExtraEnvironment was rejected.");
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
        AssertTrue(SandboxPathPolicy.IsInternalSandboxTree(".llmw.sandbox/runs/run-B/work/secret-B.txt"),
            "Sibling-run sandbox work was not classified as Core-internal.");
        AssertTrue(SandboxPathPolicy.IsInternalSandboxTree(".LLMW.SANDBOX/tools/abc/file"),
            "Case-varied sandbox tools path was not classified as Core-internal.");
        AssertTrue(SandboxPathPolicy.IsInternalSandboxTree(@".llmw.sandbox\runs\run-B\work\secret-B.txt"),
            "Backslash sandbox path was not classified as Core-internal.");
        AssertFalse(SandboxPathPolicy.IsInternalSandboxTree("notes.txt"), "A normal project file was classified as sandbox-internal.");
        AssertFalse(SandboxPathPolicy.IsInternalSandboxTree("Draft/chapter.md"), "A Draft path was classified as sandbox-internal.");
    }

    private static void BrokerDeniesWhenTrustMissingAndDoesNotLaunch()
    {
        var host = new CountingHost();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: false)),
            host,
            new CountingPathGuard(),
            Context);
        var result = broker.ExecuteShell(Request(Capability.ShellExecute));
        AssertEqual(SandboxError.TrustRequired, result.Error, "Missing trust did not fail closed.");
        AssertEqual(0, host.Launches, "A trust denial launched a child.");
    }

    private static void BrokerRecheckPreventsLaunchAfterPolicyFlip()
    {
        var host = new CountingHost();
        var policy = new FlipPolicy();
        var broker = new TrustedSandboxBroker(new CoreAuthorizationService(policy), host, new CountingPathGuard(), Context);
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
            new CountingPathGuard(),
            Context);
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
            new CountingPathGuard(),
            Context);
        var result = broker.ExecuteShell(Request(Capability.ShellExecute));
        AssertEqual(SandboxError.SandboxUnavailable, result.Error, "BYPASS launched without a sandbox host.");
    }

    private static void BrokerRejectsForgedProjectRootWithoutReading()
    {
        var host = new CountingHost();
        var guard = new CountingPathGuard();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            host,
            guard,
            Context);
        var result = broker.ReadFile(new SandboxFileReadRequest(
            UserPrincipal,
            Scope,
            @"C:\stolen",
            "secret.txt",
            "run-wp10"));
        AssertEqual(SandboxError.PathOutOfScope, result.Error, "Forged ProjectRoot was not denied.");
        AssertEqual(0, guard.Reads, "Broker opened a file using the caller-supplied ProjectRoot.");
        AssertTrue(result.Bytes is null, "Forged ProjectRoot returned file bytes.");
    }

    private static void BrokerRejectsForgedProjectScope()
    {
        var host = new CountingHost();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            host,
            new CountingPathGuard(),
            Context);
        var other = new ProjectScope(Guid.Parse("018f3e78-9999-7abc-8def-0123456789ab"), "other");
        var result = broker.ReadFile(new SandboxFileReadRequest(
            UserPrincipal,
            other,
            Context.TrustedProjectRoot,
            ".llmw.sandbox/runs/run-wp10/work/ok.txt",
            "run-wp10"));
        AssertEqual(SandboxError.SessionBindingMismatch, result.Error, "Forged ProjectScope was not denied.");
        AssertEqual(0, host.Launches, "Forged ProjectScope launched a child.");
    }

    private static void BrokerDeniesGenericReadOfInternalSandboxTreeWithoutOpening()
    {
        var host = new CountingHost();
        var guard = new CountingPathGuard();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            host,
            guard,
            Context);
        foreach (var logical in new[]
                 {
                     ".llmw.sandbox/runs/run-B/work/secret-B.txt",
                     ".LLMW.SANDBOX/runs/run-B/work/secret-B.txt",
                     @".llmw.sandbox\runs\run-B\work\secret-B.txt",
                     ".llmw.sandbox/tools/known/file"
                 })
        {
            var result = broker.ReadFile(new SandboxFileReadRequest(
                UserPrincipal,
                Scope,
                Context.TrustedProjectRoot,
                logical,
                "run-wp10"));
            AssertEqual(SandboxError.PathOutOfScope, result.Error, "Generic ProjectFile.Read of " + logical + " was not denied.");
            AssertTrue(result.Bytes is null || result.Bytes.Length == 0, "Generic sandbox-internal read returned bytes for " + logical + ".");
        }

        AssertEqual(0, guard.Reads, "Generic ProjectFile.Read opened a Core-internal sandbox path.");
    }

    private static void BrokerAllowsOrdinaryProjectRead()
    {
        var guard = new CountingPathGuard();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            new CountingHost(),
            guard,
            Context);
        var result = broker.ReadFile(new SandboxFileReadRequest(
            UserPrincipal,
            Scope,
            Context.TrustedProjectRoot,
            "notes.txt",
            "run-wp10"));
        AssertTrue(result.Succeeded, "A legitimate project file read was denied.");
        AssertEqual(1, guard.Reads, "A legitimate project file read did not reach the path guard.");
        AssertEqual("ok", System.Text.Encoding.UTF8.GetString(result.Bytes ?? []), "Legitimate project read did not return file bytes.");
    }

    private static void BrokerRequiresSessionRevalidationForAgentRun()
    {
        var host = new CountingHost();
        var agent = CreateAgentPrincipal();
        var broker = new TrustedSandboxBroker(
            new CoreAuthorizationService(new StaticPolicy(projectTrusted: true)),
            host,
            new CountingPathGuard(),
            Context);
        var result = broker.ExecuteShell(Request(Capability.ShellExecute) with { Principal = agent });
        AssertEqual(SandboxError.SessionBindingMismatch, result.Error, "AgentRun without a revalidator was allowed.");
        AssertEqual(0, host.Launches, "AgentRun without revalidation launched a child.");
    }

    private static CallerPrincipal CreateAgentPrincipal()
    {
        var store = new MemoryRunSessionStore();
        store.Runs["run-wp10"] = new DurableRunIdentity("run-wp10", "pm");
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeMilliseconds(50_000));
        var sessions = new RunSessionService(store, clock, new FixedPermission(RuntimePermissionMode.AutoApproveScoped));
        var channel = new AuthenticatedChannelContext(
            "channel-wp10",
            AuthenticatedClientKind.AgentRuntime,
            "worker-wp10",
            Scope);
        var issued = sessions.Create(new CreateRunSessionRequest("run-wp10", channel, clock.UtcNow.AddMinutes(5)));
        if (!issued.Succeeded || issued.Value is null)
        {
            throw new InvalidOperationException("Test session issuance failed.");
        }

        var resolved = sessions.Resolve(new ResolveRunSessionRequest(
            "run-wp10",
            issued.Value.Token.ExportOnceForAuthenticatedTransport(),
            channel));
        if (!resolved.Succeeded || resolved.Value is null)
        {
            throw new InvalidOperationException("Test session resolve failed.");
        }

        return resolved.Value;
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

        public SandboxedWorkerStartResult StartWorker(SandboxExecutionRequest request) =>
            SandboxedWorkerStartResult.Fail(SandboxError.SandboxUnavailable, "WP10 counting host does not launch Run Workers.");
    }

    private sealed class CountingPathGuard : ISandboxPathGuard
    {
        public int Reads { get; private set; }

        public SandboxError? TryOpenRead(string projectRoot, string runId, string logicalRelativePath, out byte[] bytes)
        {
            Reads++;
            bytes = "ok"u8.ToArray();
            return null;
        }

        public SandboxError? TryOpenWrite(string projectRoot, string runId, string logicalRelativePath, ReadOnlySpan<byte> contents) => null;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISecurityClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class FixedPermission(RuntimePermissionMode mode) : IRunSecurityPolicySource
    {
        public RuntimePermissionMode GetRuntimePermissionMode(string runId) => mode;
    }

    private sealed class MemoryRunSessionStore : IRunSessionStore
    {
        public Dictionary<string, DurableRunIdentity> Runs { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredRunSession> byHandle = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoredRunSession> byHash = new(StringComparer.Ordinal);

        public DurableRunIdentity? LoadRun(string runId) =>
            Runs.TryGetValue(runId, out var run) ? run : null;

        public StoredRunSession IssueReplacingActive(PersistRunSessionRequest request)
        {
            var stored = new StoredRunSession(
                Guid.NewGuid().ToString("D"),
                request.RunId,
                request.WorkerInstanceId,
                request.ChannelInstanceId,
                request.ProjectScope,
                request.TokenHash,
                request.ExpiresAtMs,
                null,
                request.CreatedAtMs);
            byHandle[stored.HandleId] = stored;
            byHash[stored.TokenHash] = stored;
            return stored;
        }

        public StoredRunSession? FindByTokenHash(string tokenHash) =>
            byHash.TryGetValue(tokenHash, out var session) ? session : null;

        public StoredRunSession? FindByHandleId(string handleId) =>
            byHandle.TryGetValue(handleId, out var session) ? session : null;

        public int RevokeHandle(string handleId, long revokedAtMs)
        {
            if (!byHandle.TryGetValue(handleId, out var session) || session.RevokedAtMs is not null)
            {
                return 0;
            }

            var revoked = session with { RevokedAtMs = revokedAtMs };
            byHandle[handleId] = revoked;
            byHash[revoked.TokenHash] = revoked;
            return 1;
        }

        public int RevokeByRun(string runId, long revokedAtMs) => 0;

        public int RevokeByChannelWorker(string channelInstanceId, string workerInstanceId, long revokedAtMs) => 0;
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
